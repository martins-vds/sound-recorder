using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Analysis;
using System.IO;

namespace SoundRecorder.Desktop;

/// <summary>Desktop adapter for durable explicitly requested analysis and exact-source restoration.</summary>
public sealed class PersistentTranscriptAnalysis : ILocalTranscriptAnalysis, ILocalAnalysisDiagnostics
{
    private readonly ITranscriptAnalysisStore store;
    private readonly AnalysisJobCoordinator coordinator;
    private IProgress<string>? progress;
    private string? failure;
    private readonly IReadOnlyList<ILocalAnalysisDiagnostics> diagnostics;

    /// <summary>Binds the production coordinator to the pinned processing factory, without starting inference.</summary>
    public PersistentTranscriptAnalysis(ITranscriptAnalysisStore store, IAnalysisQueueRepository repository,
        IAnalysisModelSetup setup, Func<AnalysisJobExecution, ILocalTranscriptAnalysis> processor,
        string modelStorageLocation = "Controlled adapter", IReadOnlyList<ILocalAnalysisDiagnostics>? diagnostics = null)
    {
        this.store = store;
        Setup = setup;
        ModelStorageLocation = modelStorageLocation;
        this.diagnostics = diagnostics ?? [];
        coordinator = new(repository, store, execution => new ObservedProcessor(processor(execution), this));
    }

    public IAnalysisModelSetup Setup { get; }
    public string ModelStorageLocation { get; }
    public IReadOnlyList<AnalysisJob> Jobs => coordinator.Jobs;
    public string History { get; private set; } = "";
    public string? ReleaseError { get; private set; }
    public Task RecoverAsync() => coordinator.RecoverAsync();
    public Task SetRecordingIntentAsync(bool active) => coordinator.SetRecordingIntentAsync(active || ReleaseError is not null);

    /// <summary>Validates current original TXT/SRT bytes against the retained exact-attempt pins without inference.</summary>
    public async Task VerifySourceAsync(TranscriptAnalysisRequest request, CancellationToken token)
    {
        var identity = await AnalysisProcessingNative.PinAsync(request, token);
        if (!Jobs.Any(job => job.Request.Identity == identity))
            throw new InvalidDataException("The exact original source is unavailable or changed. No citation was retargeted.");
    }

    /// <inheritdoc />
    public async Task DeleteDiagnosticsAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken)
    {
        await coordinator.DeleteOwnedAsync(attemptIds, cancellationToken);
        foreach (var adapter in diagnostics)
            await adapter.DeleteDiagnosticsAsync(attemptIds, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken cancellationToken) =>
        RunAsync(request, request.Question is null ? AnalysisJobMode.Summary : AnalysisJobMode.GlobalQuestion,
            null, progress, cancellationToken);

    /// <summary>Queues only an explicit coverage choice after revalidating the selected offline profile.</summary>
    public async Task<TranscriptAnalysisResult> RunAsync(TranscriptAnalysisRequest request, AnalysisJobMode mode,
        AnalysisJobDiscussionContext? discussion, IProgress<string> report, CancellationToken token)
    {
        var readiness = await Setup.GetReadinessAsync(token);
        if (readiness.State != AnalysisModelReadinessState.Ready || readiness.Directory is null)
            throw new InvalidOperationException(readiness.Error ?? "Explicit verified analysis model setup is required.");
        var identity = await AnalysisProcessingNative.PinAsync(request, token);
        this.progress = report;
        failure = null;
        var job = await coordinator.EnqueueAsync(new(request, identity, readiness.Directory, mode, discussion), token);
        return await FinishAsync(job.Id, request, token);
    }

    /// <summary>Reads completed history only for the selected exact source; never loads models or starts jobs.</summary>
    public async Task<TranscriptAnalysisResult> RestoreAsync(TranscriptAnalysisRequest request, CancellationToken token)
    {
        var identity = await AnalysisProcessingNative.PinAsync(request, token);
        var owned = Jobs.Where(job => job.Request.Identity.AttemptId == request.AttemptId).ToArray();
        if (owned.Length == 0)
        {
            History = "";
            return new("No saved analysis", "Generate or ask explicitly.", "", []);
        }
        if (owned.Any(job => job.Request.Identity != identity))
            throw new InvalidDataException("The exact transcript or processing identity changed. No saved analysis was retargeted.");
        AnalysisStoreSnapshot snapshot;
        try { snapshot = await store.LoadAsync(identity, token); }
        catch (KeyNotFoundException)
        {
            History = "";
            return new(owned[^1].Status.ToString(), "No completed output; explicitly retry this interrupted request.", "", []);
        }
        if (snapshot.Completed is null)
        {
            History = "";
            return new(owned[^1].Status.ToString(), "Unfinished work is not output. Retry or compatible resume requires an explicit request.", "", []);
        }
        return await ReadCompletedAsync(snapshot.Completed, token);
    }

    private async Task<TranscriptAnalysisResult> ReadCompletedAsync(AnalysisStoreRevision completed, CancellationToken token)
    {
        var claims = new List<TranscriptAnalysisClaim>();
        var history = new List<string>();
        var portions = 0;
        await foreach (var output in store.ReadOutputsAsync(completed, token))
        {
            portions++;
            if (history.Count < 200)
                history.Add($"{output.Kind}" + (output.Question is null ? "" : $" · {output.Question}") + "\n" +
                    (output.Claims.Count == 0 ? output.Content : string.Join("\n", output.Claims.Select(claim => claim.Text))));
            claims.AddRange(output.Claims.Take(Math.Max(0, 200 - claims.Count)));
        }
        History = string.Join("\n\n", history);
        return new("Completed", $"Restored exact-source history · UNVERIFIED. Showing at most 200 portions/candidates of {portions}; complete history and original citations remain in the local store.",
            "", claims);
    }

    /// <summary>Retries a released request with its original pins, or explicitly resumes the same compatible checkpoint.</summary>
    public async Task<TranscriptAnalysisResult> ContinueAsync(Guid jobId, TranscriptAnalysisRequest target,
        bool resume, IProgress<string> report, CancellationToken token)
    {
        var previous = Jobs.Single(job => job.Id == jobId);
        var identity = await AnalysisProcessingNative.PinAsync(target, token);
        if (identity != previous.Request.Identity)
            throw new InvalidDataException("Retry/resume requires this exact transcript, language and model configuration.");
        progress = report;
        failure = null;
        if (resume)
            await coordinator.ResumeAsync(jobId, identity, token);
        else
            jobId = (await coordinator.RetryAsync(jobId, token)).Id;
        return await FinishAsync(jobId, target, token);
    }

    private async Task<TranscriptAnalysisResult> FinishAsync(Guid jobId, TranscriptAnalysisRequest target,
        CancellationToken token)
    {
        var idle = coordinator.WaitForIdleAsync();
        var cancelled = Task.Delay(Timeout.Infinite, token);
        if (await Task.WhenAny(idle, cancelled) == cancelled)
            await coordinator.CancelAsync(jobId);
        await idle;
        var job = Jobs.Single(job => job.Id == jobId);
        if (job.Status == AnalysisJobStatus.Cancelled) throw new OperationCanceledException(token);
        if (job.Status != AnalysisJobStatus.Complete)
            throw new InvalidOperationException(failure ?? job.ErrorCode ?? $"Analysis is {job.Status}; explicitly retry.");
        return await RestoreAsync(target, CancellationToken.None);
    }

    public async Task CancelAndReleaseAsync()
    {
        try
        {
            foreach (var job in Jobs.Where(job => job.Status is AnalysisJobStatus.Queued or AnalysisJobStatus.Running))
                await coordinator.CancelAsync(job.Id);
        }
        catch (Exception error) { ReleaseError = error.Message; }
        try { await coordinator.WaitForIdleAsync(); }
        catch (Exception error) { ReleaseError = error.Message; }
    }

    private sealed class ObservedProcessor(ILocalTranscriptAnalysis inner, PersistentTranscriptAnalysis owner)
        : ILocalTranscriptAnalysis
    {
        public async Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
            IProgress<string> ignored, CancellationToken token)
        {
            try
            {
                var result = await inner.AnalyzeAsync(request, owner.progress ?? ignored, token);
                if (result.Status != "Completed") owner.failure = $"{result.Status}: {result.Detail}";
                return result;
            }
            catch (Exception error)
            {
                owner.failure = error.Message;
                throw;
            }
        }
    }
}
