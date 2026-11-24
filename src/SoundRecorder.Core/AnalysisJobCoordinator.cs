using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Core;

/// <summary>One durable FIFO analysis worker, independent of transcription and native runtime APIs.</summary>
public sealed partial class AnalysisJobCoordinator
{
    private readonly IAnalysisQueueRepository repository;
    private readonly ITranscriptAnalysisStore store;
    private readonly Func<AnalysisJobExecution, ILocalTranscriptAnalysis> processor;
    private readonly TimeProvider clock;
    private readonly ILogger<AnalysisJobCoordinator> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<AnalysisJob> jobs = [];
    private readonly AnalysisJobInferenceGate inference = new();
    private Task? worker;
    private CancellationTokenSource? runningCancellation;

    /// <summary>Binds processing to the immutable job selection and its owned revision.</summary>
    public AnalysisJobCoordinator(IAnalysisQueueRepository repository, ITranscriptAnalysisStore store,
        Func<AnalysisJobExecution, ILocalTranscriptAnalysis> processor, TimeProvider? clock = null,
        ILogger<AnalysisJobCoordinator>? logger = null)
    {
        this.repository = repository;
        this.store = store;
        this.processor = processor;
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<AnalysisJobCoordinator>.Instance;
    }

    /// <summary>Immutable snapshots; read again after asynchronous operations.</summary>
    public IReadOnlyList<AnalysisJob> Jobs
    {
        get { lock (jobs) return jobs.ToArray(); }
    }

    /// <summary>Durably queues an explicit request without changing its transcript or model pins.</summary>
    public async Task<AnalysisJob> EnqueueAsync(AnalysisJobRequest request, CancellationToken token = default)
    {
        AnalysisJobValidation.Request(request);
        await gate.WaitAsync(token);
        try
        {
            EnsureAvailable(request.Identity.AttemptId);
            var job = new AnalysisJob(Guid.NewGuid(), request, clock.GetUtcNow(), AnalysisJobStatus.Queued);
            await PersistAsync(Jobs.Append(job).ToArray(), token);
            StartWorker();
            return job;
        }
        finally { gate.Release(); }
    }

    /// <summary>Waits for currently scheduled work to release; does not initiate recovered work.</summary>
    public async Task WaitForIdleAsync()
    {
        Task? current;
        await gate.WaitAsync();
        try { current = worker; }
        finally { gate.Release(); }
        if (current is not null) await current;
    }

    /// <summary>Announces capture intent immediately; an existing native call releases only at its safe boundary.</summary>
    public Task SetRecordingIntentAsync(bool active)
    {
        inference.SetRecording(active);
        return active ? Task.CompletedTask : RestartAfterCaptureAsync();
    }

    private async Task RestartAfterCaptureAsync()
    {
        await gate.WaitAsync();
        try { StartWorker(); }
        finally { gate.Release(); }
    }

    private void StartWorker()
    {
        worker ??= Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            AnalysisJob? job;
            await gate.WaitAsync();
            try
            {
                job = Jobs.FirstOrDefault(j => j.Status == AnalysisJobStatus.Queued);
                if (job is null || inference.Recording) { worker = null; return; }
                job = await PrepareAsync(job);
            }
            finally { gate.Release(); }
            if (job is not null) await ProcessAsync(job, runningCancellation!.Token);
        }
    }

    private async Task<AnalysisJob?> PrepareAsync(AnalysisJob job)
    {
        AnalysisStoreRevision revision;
        try { revision = job.Revision ?? await store.BeginAsync(job.Request.Identity, default); }
        catch (Exception error)
        {
            JobFailed(logger, job.Id, error.GetType().Name);
            await ReplaceAsync(job with { Status = AnalysisJobStatus.Failed, ErrorCode = error.GetType().Name });
            return null;
        }
        job = job with { Status = AnalysisJobStatus.Running, Revision = revision };
        await ReplaceAsync(job);
        runningCancellation = new();
        return job;
    }

    private async Task ProcessAsync(AnalysisJob job, CancellationToken token)
    {
        var finished = await ExecuteAsync(job, token);
        await FinishAsync(finished);
    }

    private async Task<AnalysisJob> ExecuteAsync(AnalysisJob job, CancellationToken token)
    {
        try
        {
            var execution = new AnalysisJobExecution(job.Request, job.Revision!,
                inference.EnterAsync);
            await processor(execution).AnalyzeAsync(job.Request.Transcript, new SilentProgress(), token);
            var snapshot = await store.LoadAsync(job.Request.Identity, default);
            if (snapshot.Completed != job.Revision)
                throw new InvalidOperationException();
            return job with { Status = AnalysisJobStatus.Complete };
        }
        catch (Exception error)
        {
            JobFailed(logger, job.Id, error.GetType().Name);
            var snapshot = await store.LoadAsync(job.Request.Identity, default);
            if (snapshot.Completed == job.Revision)
                return job with { Status = AnalysisJobStatus.Complete };
            var cancelled = token.IsCancellationRequested;
            await store.SetUnfinishedAsync(job.Revision!,
                cancelled ? AnalysisStoreStatus.Cancelled : AnalysisStoreStatus.Failed, default);
            return job with { Status = cancelled ? AnalysisJobStatus.Cancelled : AnalysisJobStatus.Failed,
                ErrorCode = error.GetType().Name };
        }
    }

    private async Task FinishAsync(AnalysisJob job)
    {
        await gate.WaitAsync();
        try
        {
            await ReplaceAsync(job);
            runningCancellation?.Dispose();
            runningCancellation = null;
        }
        finally { gate.Release(); }
    }

    private Task ReplaceAsync(AnalysisJob job) =>
        PersistAsync(Jobs.Select(j => j.Id == job.Id ? job : j).ToArray(), default);

    private void EnsureAvailable(Guid attemptId)
    {
        if (Jobs.Any(j => j.Request.Identity.AttemptId == attemptId &&
            j.Status is AnalysisJobStatus.Queued or AnalysisJobStatus.Running or AnalysisJobStatus.CancellationPending))
            throw new InvalidOperationException("This transcript already has an active analysis request.");
    }

    private async Task PersistAsync(IReadOnlyList<AnalysisJob> updated, CancellationToken token)
    {
        await repository.SaveAsync(updated, token);
        lock (jobs) { jobs.Clear(); jobs.AddRange(updated); }
    }

    private sealed class SilentProgress : IProgress<string>
    {
        public void Report(string value) { }
    }

    [LoggerMessage(8100, LogLevel.Warning, "Analysis job {JobId} failed with {ErrorType}.")]
    private static partial void JobFailed(ILogger logger, Guid jobId, string errorType);
}
