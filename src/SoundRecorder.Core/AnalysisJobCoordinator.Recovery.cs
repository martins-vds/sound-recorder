namespace SoundRecorder.Core;

public sealed partial class AnalysisJobCoordinator
{
    /// <summary>Restores durable history once; unfinished work is interrupted, never automatically restarted.</summary>
    public async Task RecoverAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (worker is not null || Jobs.Count != 0)
                throw new InvalidOperationException("Restore requires a fresh idle coordinator.");
            var restored = await repository.LoadAsync(token);
            var updated = new List<AnalysisJob>();
            foreach (var job in restored)
                updated.Add(await RecoverJobAsync(job, token));
            await PersistAsync(updated, token);
        }
        finally { gate.Release(); }
    }

    private async Task<AnalysisJob> RecoverJobAsync(AnalysisJob job, CancellationToken token)
    {
        if (job.Status is not (AnalysisJobStatus.Queued or AnalysisJobStatus.Running or AnalysisJobStatus.CancellationPending))
            return job;
        return await RecoverUnfinishedAsync(job, token);
    }

    private async Task<AnalysisJob> RecoverUnfinishedAsync(AnalysisJob job, CancellationToken token)
    {
        var snapshot = await TryLoadAsync(job, token);
        var revision = job.Revision;
        if (revision is null && snapshot?.Status == AnalysisStoreStatus.Processing)
            revision = snapshot.Unfinished;
        if (revision is null)
            return job with { Status = AnalysisJobStatus.Interrupted };
        return await ReconcileAsync(job, revision, snapshot, token);
    }

    private async Task<AnalysisJob> ReconcileAsync(AnalysisJob job, AnalysisStoreRevision revision,
        AnalysisStoreSnapshot? snapshot, CancellationToken token)
    {
        if (snapshot?.Completed == revision)
            return job with { Status = AnalysisJobStatus.Complete };
        if (snapshot?.Unfinished != revision)
            throw new InvalidOperationException("Recovered job no longer owns the current revision.");
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Interrupted, token);
        return job with { Status = AnalysisJobStatus.Interrupted, Revision = revision };
    }

    private async Task<AnalysisStoreSnapshot?> TryLoadAsync(AnalysisJob job, CancellationToken token)
    {
        try { return await store.LoadAsync(job.Request.Identity, token); }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidDataException) when (job.Revision is null) { return null; }
    }

    /// <summary>Explicitly continues only the same current compatible revision and its retained checkpoint.</summary>
    public async Task ResumeAsync(Guid jobId, AnalysisStoreIdentity expectedIdentity, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var job = FindTerminal(jobId);
            EnsureAvailable(job.Request.Identity.AttemptId);
            if (job.Request.Identity != expectedIdentity || job.Revision is null)
                throw new InvalidOperationException("Resume requires the exact retained transcript/model identity.");
            var snapshot = await store.LoadAsync(expectedIdentity, token);
            if (snapshot.Unfinished != job.Revision)
                throw new InvalidOperationException("Resume requires the current retained revision.");
            await store.ResumeAsync(job.Revision, token);
            await ReplaceAsync(job with { Status = AnalysisJobStatus.Queued, ErrorCode = null });
            StartWorker();
        }
        finally { gate.Release(); }
    }
}
