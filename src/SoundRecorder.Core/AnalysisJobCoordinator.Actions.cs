namespace SoundRecorder.Core;

public sealed partial class AnalysisJobCoordinator
{
    /// <summary>Deletes only released attempt-owned analysis and journal entries; unrelated work is not stopped.</summary>
    public async Task DeleteOwnedAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken token = default)
    {
        var owned = attemptIds.ToHashSet();
        await CancelOwnedAsync(owned, token);
        await gate.WaitAsync(token);
        try { await DeleteReleasedOwnedAsync(owned, token); }
        finally { gate.Release(); }
    }

    private async Task CancelOwnedAsync(HashSet<Guid> owned, CancellationToken token)
    {
        foreach (var job in Jobs.Where(job => owned.Contains(job.Request.Identity.AttemptId)))
            await CancelAsync(job.Id, token);
        while (Jobs.Any(job => owned.Contains(job.Request.Identity.AttemptId) &&
            job.Status is AnalysisJobStatus.Running or AnalysisJobStatus.CancellationPending))
            await Task.Delay(10, token);
    }

    private async Task DeleteReleasedOwnedAsync(HashSet<Guid> owned, CancellationToken token)
    {
        if (Jobs.Any(job => owned.Contains(job.Request.Identity.AttemptId) && job.Status == AnalysisJobStatus.Queued))
            throw new InvalidOperationException("New owned work was queued during deletion; explicitly retry deletion.");
        foreach (var attempt in owned)
            await store.DeleteOwnedAsync(attempt, token);
        await PersistAsync(Jobs.Where(job => !owned.Contains(job.Request.Identity.AttemptId)).ToArray(), token);
    }

    /// <summary>Persists cancellation intent; the job remains pending until its owned native call releases.</summary>
    public async Task CancelAsync(Guid jobId, CancellationToken token = default)
    {
        Task? cancellation = null;
        Exception? persistenceFailure = null;
        await gate.WaitAsync(token);
        try
        {
            var job = Find(jobId);
            if (job.Status == AnalysisJobStatus.Running)
            {
                persistenceFailure = await TryRecordCancellationAsync(job);
                cancellation = runningCancellation!.CancelAsync();
            }
            else if (job.Status == AnalysisJobStatus.Queued)
                await CancelQueuedAsync(job);
        }
        finally { gate.Release(); }
        await ObserveCancellationAsync(cancellation, persistenceFailure);
    }

    private async Task<Exception?> TryRecordCancellationAsync(AnalysisJob job)
    {
        try
        {
            await ReplaceAsync(job with { Status = AnalysisJobStatus.CancellationPending });
            return null;
        }
        catch (Exception error) { return error; }
    }

    private async Task CancelQueuedAsync(AnalysisJob job)
    {
        if (job.Revision is not null)
            await store.SetUnfinishedAsync(job.Revision, AnalysisStoreStatus.Cancelled, default);
        await ReplaceAsync(job with { Status = AnalysisJobStatus.Cancelled });
    }

    private static async Task ObserveCancellationAsync(Task? cancellation, Exception? persistenceFailure)
    {
        if (cancellation is not null) await cancellation;
        if (persistenceFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(persistenceFailure).Throw();
    }

    /// <summary>Explicitly starts a new attempt with the same immutable pins; never retries automatically.</summary>
    public async Task<AnalysisJob> RetryAsync(Guid jobId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var previous = FindTerminal(jobId);
            EnsureAvailable(previous.Request.Identity.AttemptId);
            var job = new AnalysisJob(Guid.NewGuid(), previous.Request, clock.GetUtcNow(),
                AnalysisJobStatus.Queued, PreviousJobId: previous.Id);
            await PersistAsync(Jobs.Append(job).ToArray(), token);
            StartWorker();
            return job;
        }
        finally { gate.Release(); }
    }

    private AnalysisJob Find(Guid id) => Jobs.SingleOrDefault(j => j.Id == id)
        ?? throw new KeyNotFoundException("Analysis job is not available.");

    private AnalysisJob FindTerminal(Guid id)
    {
        var job = Find(id);
        if (job.Status is not (AnalysisJobStatus.Cancelled or AnalysisJobStatus.Interrupted or AnalysisJobStatus.Failed))
            throw new InvalidOperationException("Only released unfinished jobs can be retried or resumed.");
        return job;
    }
}
