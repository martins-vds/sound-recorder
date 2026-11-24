namespace SoundRecorder.Core;

public sealed partial class AnalysisJobCoordinator
{
    /// <summary>Persists cancellation intent; the job remains pending until its owned native call releases.</summary>
    public async Task CancelAsync(Guid jobId, CancellationToken token = default)
    {
        Task? cancellation = null;
        await gate.WaitAsync(token);
        try
        {
            var job = Find(jobId);
            if (job.Status == AnalysisJobStatus.Running)
            {
                await ReplaceAsync(job with { Status = AnalysisJobStatus.CancellationPending });
                cancellation = runningCancellation!.CancelAsync();
            }
            else if (job.Status == AnalysisJobStatus.Queued)
            {
                if (job.Revision is not null)
                    await store.SetUnfinishedAsync(job.Revision, AnalysisStoreStatus.Cancelled, default);
                await ReplaceAsync(job with { Status = AnalysisJobStatus.Cancelled });
            }
        }
        finally { gate.Release(); }
        if (cancellation is not null) await cancellation;
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
