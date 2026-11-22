using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace SoundRecorder.Core;

public sealed class TranscriptionQueue : ITranscriptionQueue
{
    private readonly ILocalTranscriber _transcriber;
    private readonly ITranscriptWriter _writer;
    private readonly ITranscriptionJobRepository _repository;
    private readonly ILogger<TranscriptionQueue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<TranscriptionJob> _jobs = [];
    private CancellationTokenSource? _runningJobCancellation;
    private Task? _runningCancellationCompletion;
    private Task? _worker;
    private readonly ITranscriptionModelStore? _models;
    private readonly IRecordingHistory? _history;
    private readonly HashSet<Guid> _blockedRecordings = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _releases = [];
    private readonly ConcurrentQueue<Action> _notifications = new();
    private int _dispatching;

    /// <summary>Asynchronous refresh notification; read the job's latest state on the observer's dispatcher.</summary>
    public event EventHandler<TranscriptionJob>? JobChanged;
    /// <summary>Ordered immutable estimates; backlogged events from terminated attempts are discarded.</summary>
    public event EventHandler<TranscriptionJobProgress>? ProgressChanged;

    public TranscriptionQueue(
        ILocalTranscriber transcriber,
        ITranscriptWriter writer,
        ITranscriptionJobRepository repository,
        ILogger<TranscriptionQueue>? logger = null,
        ITranscriptionModelStore? models = null,
        IRecordingHistory? history = null)
    {
        _transcriber = transcriber;
        _writer = writer;
        _repository = repository;
        _logger = logger ?? NullLogger<TranscriptionQueue>.Instance;
        _models = models;
        _history = history;
    }

    public IReadOnlyList<TranscriptionJob> Jobs
    {
        get
        {
            lock (_jobs)
            {
                return _jobs.ToArray();
            }
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var persisted = await _repository.LoadAsync(cancellationToken);
        lock (_jobs)
        {
            foreach (var job in persisted.OrderBy(job => job.CreatedAt))
            {
                if (job.Status == TranscriptionJobStatus.Running)
                {
                    job.Status = TranscriptionJobStatus.Interrupted;
                }

                _jobs.Add(job);
            }
        }

        StartWorker();
        await SaveAsync(cancellationToken);
        CoreLog.QueueRestored(_logger, _jobs.Count);
    }

    public async Task EnqueueAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        var job = CreateJob(recording);
        await ValidateRecordingAsync(recording, cancellationToken);
        lock (_jobs)
        {
            EnsureCanQueue(recording.Id);
            _jobs.Add(job);
        }

        await SaveAsync(cancellationToken);
        CoreLog.JobQueued(_logger, job.Id, recording.Id);
        Notify(job);
        StartWorker();
    }

    public Task CancelAsync(Guid jobId, CancellationToken cancellationToken) =>
        RequestTerminationAsync(jobId, false, cancellationToken);

    /// <summary>Acknowledges interruption; IsTerminationPending remains true until owned work releases.</summary>
    public Task StopAsync(Guid jobId, CancellationToken cancellationToken) =>
        RequestTerminationAsync(jobId, true, cancellationToken);

    private async Task RequestTerminationAsync(Guid jobId, bool stop, CancellationToken token)
    {
        Task? cancellation;
        bool running;
        lock (_jobs)
        {
            var job = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (job is null) return;
            if (stop && job.Status != TranscriptionJobStatus.Running)
                throw new InvalidOperationException("Only a running job can be stopped.");
            (running, cancellation) = BeginTermination(job, stop);
            Notify(job);
        }
        if (running)
        {
            if (cancellation is not null)
            {
                await cancellation;
                CoreLog.RunningJobCancellationRequested(_logger, jobId);
            }
            _ = PersistTerminationRequestAsync(jobId);
        }
        else await SaveAsync(token);
        CoreLog.JobCancellationCompleted(_logger, jobId);
    }

    private (bool Running, Task? Cancellation) BeginTermination(TranscriptionJob job, bool stop)
    {
        if (job.Status == TranscriptionJobStatus.Queued) job.Status = TranscriptionJobStatus.Canceled;
        var running = job.Status == TranscriptionJobStatus.Running;
        Task? cancellation = null;
        if (job.Status == TranscriptionJobStatus.Running && !job.IsTerminationPending)
        {
            job.IsTerminationPending = true;
            job.InterruptionReason = stop ? "User requested Stop." : null;
            cancellation = _runningJobCancellation!.CancelAsync();
            _runningCancellationCompletion = cancellation;
        }
        return (running, cancellation);
    }

    private async Task PersistTerminationRequestAsync(Guid jobId)
    {
        try { await SaveAsync(CancellationToken.None); }
        catch (Exception exception) { CoreLog.JobFailed(_logger, exception, jobId); }
    }

    /// <summary>Durably removes terminal history; unsuccessful residuals require completed full-deletion receipts, never transcript registration.</summary>
    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var selected = GetDeletableJob(jobId);
        if (selected is null) return;
        await EnsureOutputOwnershipAsync(selected, cancellationToken);
        await PersistJobDeletionAsync(selected, jobId, cancellationToken);
    }

    private TranscriptionJob? GetDeletableJob(Guid jobId)
    {
        lock (_jobs)
        {
            var selected = _jobs.FirstOrDefault(item => item.Id == jobId);
            if (selected is null) return null;
            if (selected.Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running || selected.IsTerminationPending)
                throw new InvalidOperationException("Stop or cancel the job and wait for worker release before deleting its history.");
            return selected;
        }
    }

    private async Task EnsureOutputOwnershipAsync(TranscriptionJob selected, CancellationToken cancellationToken)
    {
        if (selected.Outputs is { } outputs)
        {
            if (selected.Status == TranscriptionJobStatus.Succeeded)
                await RegisterOutputOwnershipAsync(selected, outputs, cancellationToken);
            else
                await EnsureResidualOutputsRemovedAsync(selected, outputs, cancellationToken);
        }
    }

    private async Task EnsureResidualOutputsRemovedAsync(
        TranscriptionJob selected, TranscriptOutputs outputs, CancellationToken cancellationToken)
    {
        var pending = await LoadResidualDeletionAsync(selected, cancellationToken);
        if (!IsFullDeletionForJob(pending, selected.Id))
            throw new InvalidOperationException("Residual output artifacts must be removed and durably recorded before deleting unsuccessful job history.");
        EnsureResidualArtifactRemoved(pending!, selected.Id, RecordingArtifactKind.Text, outputs.TextPath);
        EnsureResidualArtifactRemoved(pending!, selected.Id, RecordingArtifactKind.Subtitle, outputs.SubtitlePath);
    }

    private async Task<PendingRecordingDeletion?> LoadResidualDeletionAsync(
        TranscriptionJob selected, CancellationToken cancellationToken)
    {
        if (_history is null) return selected.Recording.PendingDeletion;
        var recording = (await _history.LoadAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == selected.Recording.Id);
        return recording?.PendingDeletion;
    }

    private static bool IsFullDeletionForJob(PendingRecordingDeletion? pending, Guid jobId) =>
        pending is not null && pending.Scope == RecordingDeletionScope.AllArtifacts && pending.RemainingJobIds.Contains(jobId);

    private static void EnsureResidualArtifactRemoved(
        PendingRecordingDeletion pending, Guid jobId, RecordingArtifactKind kind, string path)
    {
        if (!pending.Artifacts.Any(artifact => MatchesResidualArtifact(artifact, jobId, kind, path) && IsRemovedArtifact(artifact)))
            throw new InvalidOperationException("Residual output artifacts must be removed and durably recorded before deleting unsuccessful job history.");
    }

    private static bool MatchesResidualArtifact(ArtifactDeletion artifact, Guid jobId, RecordingArtifactKind kind, string path) =>
        artifact.JobId == jobId && artifact.Kind == kind &&
        string.Equals(Path.GetFullPath(artifact.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsRemovedArtifact(ArtifactDeletion artifact) =>
        artifact.State is ArtifactDeletionState.Removed or ArtifactDeletionState.AlreadyMissing;

    private async Task RegisterOutputOwnershipAsync(
        TranscriptionJob selected, TranscriptOutputs outputs, CancellationToken cancellationToken)
    {
        if (_history is not null)
            await _history.RegisterOutputsAsync(selected.Recording.Id, new(selected.Id, outputs), cancellationToken);
        else if (!selected.Recording.TranscriptOutputs.Any(owned => owned.JobId == selected.Id && owned.Outputs == outputs))
            throw new InvalidOperationException("Register the output pair independently in recording history before deleting job history.");
    }

    private async Task PersistJobDeletionAsync(
        TranscriptionJob selected, Guid jobId, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            TranscriptionJob[] remaining;
            lock (_jobs)
            {
                if (!_jobs.Contains(selected)) return;
                remaining = _jobs.Where(job => job.Id != jobId).ToArray();
            }
            await _repository.SaveAsync(remaining, cancellationToken);
            lock (_jobs) _jobs.Remove(selected);
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>Blocks new attempts for this queue lifetime and awaits all related owned-worker releases.</summary>
    public async Task CancelRecordingAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        TranscriptionJob[] related;
        Task[] releases;
        lock (_jobs)
        {
            _blockedRecordings.Add(recordingId);
            related = _jobs.Where(job => job.Recording.Id == recordingId).ToArray();
            releases = related.Where(job => _releases.ContainsKey(job.Id))
                .Select(job => _releases[job.Id].Task).ToArray();
        }
        foreach (var job in related) await CancelAsync(job.Id, cancellationToken);
        await Task.WhenAll(releases).WaitAsync(cancellationToken);
    }

    public async Task<TranscriptionJob> RetryAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        var job = CreateJob(recording);
        await ValidateRecordingAsync(recording, cancellationToken);
        lock (_jobs)
        {
            EnsureCanQueue(recording.Id);
            _jobs.Add(job);
        }

        await SaveAsync(cancellationToken);
        Notify(job);
        StartWorker();
        CoreLog.JobRetryQueued(_logger, job.Id, recording.Id);
        return job;
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (_worker is { } worker && !worker.IsCompleted)
        {
            await worker.WaitAsync(cancellationToken);
        }
        await _saveGate.WaitAsync(cancellationToken);
        _saveGate.Release();
    }

    private void StartWorker()
    {
        // Stryker disable once all: removing this guard only creates a redundant, gated worker task.
        lock (_jobs)
        {
            if (_worker is { IsCompleted: false }) return;
            _worker = Task.Run(RunWorkerAsync);
        }
    }

    private async Task RunWorkerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            while (NextQueuedJob() is { } job)
            {
                await RunAsync(job);
            }
        }
        finally
        {
            _gate.Release();
            lock (_jobs)
            {
                _worker = null;
                // Stryker disable once all: this race guard is observable only if enqueue and worker teardown interleave.
                if (NextQueuedJob() is not null) StartWorker();
            }
        }
    }

    private TranscriptionJob? NextQueuedJob()
    {
        lock (_jobs)
        {
            return _jobs
                .Where(job => job.Status == TranscriptionJobStatus.Queued)
                .OrderBy(job => job.CreatedAt)
                .FirstOrDefault();
        }
    }

    private async Task RunAsync(TranscriptionJob job)
    {
        var lifetime = StartJob(job);
        if (lifetime is null) return;
        var (cancellation, release) = lifetime.Value;
        ReportProgress(job, new(TranscriptionPhase.Preparing));
        CoreLog.JobStarted(_logger, job.Id);
        try
        {
            await ProcessJobAsync(job, cancellation);
        }
        catch (OperationCanceledException)
        {
            CompleteCanceledJob(job);
        }
        catch (Exception exception)
        {
            CompleteFailedJob(job, exception);
        }
        finally
        {
            await ReleaseJobAsync(job, cancellation, release);
        }
    }

    private (CancellationTokenSource Cancellation, TaskCompletionSource Release)? StartJob(TranscriptionJob job)
    {
        lock (_jobs)
        {
            if (job.Status != TranscriptionJobStatus.Queued) return null;
            var cancellation = _runningJobCancellation = new CancellationTokenSource();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releases[job.Id] = release;
            job.Status = TranscriptionJobStatus.Running;
            return (cancellation, release);
        }
    }

    private async Task ProcessJobAsync(TranscriptionJob job, CancellationTokenSource cancellation)
    {
        await SaveAsync(cancellation.Token);
        await ValidateRecordingAsync(job.Recording, cancellation.Token);
        var modelPath = ResolveJobModelPath(job);
        ValidatePinnedTranscriber(job);
        var document = await TranscribeJobAsync(job, modelPath);
        await ValidateRecordingAsync(job.Recording, cancellation.Token);
        ReportProgress(job, new(TranscriptionPhase.Writing));
        job.Outputs = await _writer.WriteAsync(document, _runningJobCancellation!.Token);
        await ValidateRecordingAsync(job.Recording, cancellation.Token);
        await RegisterPublishedOutputsAsync(job);
        lock (_jobs)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            job.Status = TranscriptionJobStatus.Succeeded;
        }
        CoreLog.JobSucceeded(_logger, job.Id);
    }

    private string? ResolveJobModelPath(TranscriptionJob job)
    {
        var modelPath = _models?.GetModelPath(job.Language, job.ModelId);
        if (_models is not null && (job.ModelId is null || modelPath is null))
        {
            throw new InvalidOperationException(
                $"The pinned model '{job.ModelId ?? "unknown"}' is unavailable for {job.Language}. Select a downloaded model and request a new attempt.");
        }
        return modelPath;
    }

    private void ValidatePinnedTranscriber(TranscriptionJob job)
    {
        if (job.ModelId is not null && _transcriber is not IJobTranscriber)
            throw new InvalidOperationException("This transcriber cannot honor a pinned model. Use a job-aware local transcriber.");
    }

    private Task<TranscriptDocument> TranscribeJobAsync(TranscriptionJob job, string? modelPath) =>
        _transcriber is IJobTranscriber jobTranscriber
            ? jobTranscriber.TranscribeAsync(
                new(job.Id, job.Recording, job.ModelId, modelPath),
                new JobProgress(value => ReportProgress(job, value)), _runningJobCancellation!.Token)
            : _transcriber.TranscribeAsync(job.Recording, _runningJobCancellation!.Token);

    private async Task RegisterPublishedOutputsAsync(TranscriptionJob job)
    {
        if (_history is not null)
            await _history.RegisterOutputsAsync(job.Recording.Id, new(job.Id, job.Outputs!), CancellationToken.None);
    }

    private void CompleteCanceledJob(TranscriptionJob job)
    {
        var cleanupError = DiscardUnsuccessfulOutputs(job);
        job.Status = cleanupError is not null ? TranscriptionJobStatus.Failed : job.InterruptionReason is null
            ? TranscriptionJobStatus.Canceled : TranscriptionJobStatus.Interrupted;
        job.Error = cleanupError;
        CoreLog.JobCanceled(_logger, job.Id);
    }

    private void CompleteFailedJob(TranscriptionJob job, Exception exception)
    {
        var cleanupError = DiscardUnsuccessfulOutputs(job);
        job.Status = TranscriptionJobStatus.Failed;
        job.Error = cleanupError is null ? exception.Message : $"{exception.Message} {cleanupError}";
        CoreLog.JobFailed(_logger, exception, job.Id);
    }

    private async Task ReleaseJobAsync(
        TranscriptionJob job, CancellationTokenSource cancellation, TaskCompletionSource release)
    {
        Task? cancellationCompletion;
        lock (_jobs) cancellationCompletion = _runningCancellationCompletion;
        await CompleteCancellationCallbacksAsync(cancellationCompletion, job.Id);
        lock (_jobs)
        {
            job.IsTerminationPending = false;
            cancellation.Dispose();
            _runningJobCancellation = null;
            _runningCancellationCompletion = null;
        }
        try { await SaveAsync(CancellationToken.None); }
        finally
        {
            lock (_jobs) _releases.Remove(job.Id);
            release.TrySetResult();
            Notify(job);
        }
    }

    private async Task CompleteCancellationCallbacksAsync(Task? completion, Guid jobId)
    {
        if (completion is null) return;
        try { await completion; }
        catch (Exception exception)
        {
            // The request still propagates callback failures; release must nevertheless dispose owned resources.
            CoreLog.JobFailed(_logger, exception, jobId);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try { await _repository.SaveAsync(Jobs, cancellationToken); }
        finally { _saveGate.Release(); }
    }

    private TranscriptionJob CreateJob(SavedRecording recording) =>
        new(Guid.NewGuid(), recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Queued,
            null, null, _models?.SelectedModelId);

    private void EnsureCanQueue(Guid recordingId)
    {
        if (_blockedRecordings.Contains(recordingId) ||
            _jobs.Any(job => job.Recording.Id == recordingId && job.IsTerminationPending))
            throw new InvalidOperationException("Recording deletion or worker termination is pending.");
    }

    private async Task ValidateRecordingAsync(SavedRecording recording, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_jobs) EnsureCanQueue(recording.Id);
        var current = _history is null ? recording :
            (await _history.LoadAsync(token)).FirstOrDefault(item => item.Id == recording.Id)
                ?? throw new InvalidOperationException("Register the recording in history before enqueueing transcription.");
        if (current.AudioDeleted || current.PendingDeletion is not null)
            throw new InvalidOperationException("The recording audio is deleted or deletion is pending.");
    }

    private static string? DiscardUnsuccessfulOutputs(TranscriptionJob job)
    {
        if (job.Outputs is not { } outputs) return null;
        var failures = DiscardOutputPaths(job, outputs);
        if (failures.Count == 0) job.Outputs = null;
        return failures.Count == 0 ? null : string.Join(" ", failures);
    }

    private static List<string> DiscardOutputPaths(TranscriptionJob job, TranscriptOutputs outputs)
    {
        var failures = new List<string>();
        foreach (var path in new[] { outputs.TextPath, outputs.SubtitlePath })
        {
            var failure = DiscardOutputPath(job, path);
            if (failure is not null) failures.Add(failure);
        }
        return failures;
    }

    private static string? DiscardOutputPath(TranscriptionJob job, string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(job.Recording.AudioPath), StringComparison.OrdinalIgnoreCase))
            return null;
        return DeleteOutputFile(path);
    }

    private static string? DeleteOutputFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (IsOutputDeletionFailure(exception))
        {
            return $"Could not remove this attempt's output '{path}': {exception.Message}";
        }
        return null;
    }

    private static bool IsOutputDeletionFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private void ReportProgress(TranscriptionJob job, TranscriptionProgress progress)
    {
        lock (_jobs)
        {
            if (job.Status != TranscriptionJobStatus.Running || job.IsTerminationPending) return;
            job.Progress = progress;
        }
        var update = new TranscriptionJobProgress(job.Id, progress);
        NotifyProgress(job, update);
        Notify(job);
    }

    private void NotifyProgress(TranscriptionJob job, TranscriptionJobProgress update)
    {
        var subscribers = ProgressChanged;
        if (subscribers is not null)
        {
            foreach (EventHandler<TranscriptionJobProgress> handler in subscribers.GetInvocationList())
                Dispatch(() =>
                {
                    if (job.Status == TranscriptionJobStatus.Running && !job.IsTerminationPending)
                        handler(this, update);
                });
        }
    }

    private void Notify(TranscriptionJob job)
    {
        // Observers cannot hold native inference or fail a job.
        var subscribers = JobChanged;
        if (subscribers is null) return;
        foreach (EventHandler<TranscriptionJob> handler in subscribers.GetInvocationList())
            Dispatch(() => handler(this, job));
    }

    private void Dispatch(Action notification)
    {
        _notifications.Enqueue(notification);
        if (Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            do
            {
                while (_notifications.TryDequeue(out var callback))
                    try { callback(); } catch { }
                Interlocked.Exchange(ref _dispatching, 0);
            } while (!_notifications.IsEmpty && Interlocked.CompareExchange(ref _dispatching, 1, 0) == 0);
        });
    }

    private sealed class JobProgress(Action<TranscriptionProgress> report) : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) => report(value);
    }
}
