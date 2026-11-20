using Microsoft.Extensions.Logging;
using SoundRecorder.Core;

namespace SoundRecorder.Core.Tests;

public sealed class RecorderApplicationMutationTests
{
    [Fact]
    public async Task ApplicationEmitsLifecycleAndFailureEvents()
    {
        var logger = new RecordingLogger<RecorderApplication>();
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine, logger: logger);

        await app.StartAsync(ValidRequest(), permissionConfirmed: false);
        await app.StartAsync(
            ValidRequest() with { MicrophoneId = null },
            permissionConfirmed: true);
        await app.StartAsync(ValidRequest(), permissionConfirmed: true);
        await app.StartAsync(ValidRequest(), permissionConfirmed: true);
        await app.PauseAsync();
        await app.ResumeAsync();
        await app.StopAsync();

        var failingEngine = new TestRecordingEngine
        {
            StartFailure = new RecordingStartException("Capture failed."),
        };
        var failingApp = new RecorderApplication(failingEngine, logger: logger);
        await failingApp.StartAsync(ValidRequest(), permissionConfirmed: true);

        var completionLogger = new RecordingLogger<RecorderApplication>();
        var completionFailureHistory = new FailAlwaysHistory();
        var completionApp = new RecorderApplication(
            engine,
            history: completionFailureHistory,
            logger: completionLogger);
        await Assert.ThrowsAsync<IOException>(
            () => completionApp.AcceptCompletedRecordingAsync(engine.Result));

        Assert.Contains(210, logger.EventIds);
        Assert.Contains(211, logger.EventIds);
        Assert.Contains(212, logger.EventIds);
        Assert.Contains(200, logger.EventIds);
        Assert.Contains(201, logger.EventIds);
        Assert.Contains(202, logger.EventIds);
        Assert.Contains(203, logger.EventIds);
        Assert.Contains(901, logger.EventIds);
        Assert.Contains(902, completionLogger.EventIds);
    }

    [Fact]
    public async Task StartRequiresPermissionAndReportsExactAction()
    {
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine);

        await app.StartAsync(ValidRequest(), permissionConfirmed: false);

        Assert.False(engine.Started);
        Assert.Equal("Idle", app.VisibleStatus);
        Assert.Equal(
            "Confirm that you have permission to record before starting.",
            app.LastError);
    }

    [Fact]
    public async Task StartRejectsASecondActiveOrPausedRecording()
    {
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine);
        await app.StartAsync(ValidRequest(), permissionConfirmed: true);

        await app.StartAsync(ValidRequest(), permissionConfirmed: true);
        Assert.Equal(1, engine.StartCalls);
        Assert.Equal(
            "A recording is already active. Stop it before starting another.",
            app.LastError);

        await app.PauseAsync();
        await app.StartAsync(ValidRequest(), permissionConfirmed: true);
        Assert.Equal(1, engine.StartCalls);
        Assert.Equal("Paused", app.VisibleStatus);
    }

    [Fact]
    public async Task StartReportsEachMissingPrerequisiteInSelectionOrder()
    {
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine);

        await app.StartAsync(
            ValidRequest() with { MicrophoneId = null, PlaybackDeviceId = null, DestinationFolder = null },
            true);
        Assert.Equal("Select an available microphone before starting.", app.LastError);

        await app.StartAsync(
            ValidRequest() with { PlaybackDeviceId = null, DestinationFolder = null },
            true);
        Assert.Equal("Select an available playback device before starting.", app.LastError);

        await app.StartAsync(ValidRequest() with { DestinationFolder = null }, true);
        Assert.Equal(
            "Select a writable local destination folder before starting.",
            app.LastError);
    }

    [Fact]
    public async Task StartExposesEngineFailureWithoutChangingState()
    {
        var engine = new TestRecordingEngine
        {
            StartFailure = new RecordingStartException("Microphone is unavailable."),
        };
        var app = new RecorderApplication(engine);

        await app.StartAsync(ValidRequest(), true);

        Assert.Equal("Idle", app.VisibleStatus);
        Assert.Equal("Microphone is unavailable.", app.LastError);
    }

    [Fact]
    public async Task PauseResumeAndInvalidTransitionsDelegateOnlyInValidStates()
    {
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine);

        await app.PauseAsync();
        await app.ResumeAsync();
        Assert.Equal(0, engine.PauseCalls);
        Assert.Equal(0, engine.ResumeCalls);

        await app.StartAsync(ValidRequest(), true);
        await app.PauseAsync();
        Assert.Equal(1, engine.PauseCalls);
        Assert.Equal("Paused", app.VisibleStatus);
        await app.PauseAsync();
        Assert.Equal(1, engine.PauseCalls);

        await app.ResumeAsync();
        Assert.Equal(1, engine.ResumeCalls);
        Assert.Equal("Recording", app.VisibleStatus);
        await app.ResumeAsync();
        Assert.Equal(1, engine.ResumeCalls);
    }

    [Fact]
    public async Task StopReturnsNullWhenIdleAndFinalizesWhenActive()
    {
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine);

        Assert.Null(await app.StopAsync());
        await app.StartAsync(ValidRequest(), true);
        var saved = await app.StopAsync();

        Assert.Same(engine.Result, saved);
        Assert.Equal(1, engine.StopCalls);
        Assert.Equal("Idle", app.VisibleStatus);
        Assert.False(app.IsActive);
    }

    [Fact]
    public async Task StopAlsoFinalizesAPausedRecording()
    {
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine);
        await app.StartAsync(ValidRequest(), true);
        await app.PauseAsync();

        var saved = await app.StopAsync();

        Assert.Same(engine.Result, saved);
        Assert.Equal(1, engine.StopCalls);
        Assert.Equal("Idle", app.VisibleStatus);
    }

    [Fact]
    public async Task StopFailureLeavesTheRecorderIdleWithRecoveryGuidance()
    {
        var logger = new RecordingLogger<RecorderApplication>();
        var engine = new TestRecordingEngine
        {
            StopFailure = new IOException("The partial WAV is still open."),
        };
        var app = new RecorderApplication(engine, logger: logger);
        await app.StartAsync(ValidRequest(), true);

        var recording = await app.StopAsync();

        Assert.Null(recording);
        Assert.Equal("Idle", app.VisibleStatus);
        Assert.Contains("recovery", app.LastError);
        Assert.Contains(910, logger.EventIds);
    }

    [Fact]
    public async Task CompleteRecordingClearsAnEngineFailure()
    {
        var engine = new TestRecordingEngine
        {
            LastFailure = "Only interrupted recordings use this value.",
        };
        var app = new RecorderApplication(engine);

        await app.AcceptCompletedRecordingAsync(engine.Result);

        Assert.Null(app.LastError);
    }

    [Fact]
    public async Task CompletionPersistsHistoryAndUsesExactInterruptedFailure()
    {
        var history = new TestHistory();
        var engine = new TestRecordingEngine
        {
            LastFailure = "The storage device disconnected.",
        };
        var app = new RecorderApplication(engine, history: history);
        var interrupted = engine.Result with
        {
            Outcome = RecordingOutcome.Interrupted,
        };

        await app.AcceptCompletedRecordingAsync(interrupted);

        Assert.Equal("The storage device disconnected.", app.LastError);
        Assert.Same(interrupted, Assert.Single(history.Recordings));
        Assert.Null(app.TranscriptionNotice);
    }

    [Fact]
    public async Task CompletionUsesFallbackErrorAndNeverQueuesInterruptedAudio()
    {
        var queue = new TestQueue();
        var models = new TestModels(true);
        var app = new RecorderApplication(
            new TestRecordingEngine(),
            queue,
            models);
        var interrupted = new TestRecordingEngine().Result with
        {
            Outcome = RecordingOutcome.Interrupted,
        };

        await app.AcceptCompletedRecordingAsync(interrupted);

        Assert.Equal(
            "Recording was interrupted. The available captured audio was preserved.",
            app.LastError);
        Assert.Empty(queue.Recordings);
        Assert.Null(app.TranscriptionNotice);
    }

    [Fact]
    public async Task CompletionQueuesOnlyCompleteAudioWithAnAvailableModel()
    {
        var queue = new TestQueue();
        var models = new TestModels(true);
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine, queue, models);

        await app.AcceptCompletedRecordingAsync(engine.Result);
        Assert.Same(engine.Result, Assert.Single(queue.Recordings));
        Assert.Null(app.TranscriptionNotice);

        queue.Recordings.Clear();
        await app.AcceptCompletedRecordingAsync(
            engine.Result,
            enqueueTranscription: false);
        Assert.Empty(queue.Recordings);

        var noModel = new RecorderApplication(
            engine,
            queue,
            new TestModels(false));
        await noModel.AcceptCompletedRecordingAsync(engine.Result);
        Assert.Empty(queue.Recordings);
        Assert.Equal(
            "Audio was saved. Install a compatible transcription model to transcribe this recording.",
            noModel.TranscriptionNotice);
    }

    [Fact]
    public async Task CompletionDoesNotRequireAQueueToPreserveAudio()
    {
        var history = new TestHistory();
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(engine, modelStore: new TestModels(true), history: history);

        await app.AcceptCompletedRecordingAsync(engine.Result);

        Assert.Same(engine.Result, Assert.Single(history.Recordings));
        Assert.Null(app.TranscriptionNotice);
    }

    [Fact]
    public async Task DuplicateCompletionEventsAreProcessedOnlyOnce()
    {
        var queue = new TestQueue();
        var history = new TestHistory();
        var engine = new TestRecordingEngine();
        var app = new RecorderApplication(
            engine,
            queue,
            new TestModels(true),
            history);

        await app.AcceptCompletedRecordingAsync(engine.Result);
        await app.AcceptCompletedRecordingAsync(engine.Result);

        Assert.Single(queue.Recordings);
        Assert.Single(history.Recordings);
    }

    [Fact]
    public async Task ACompletionFailureCanBeRetried()
    {
        var history = new FailOnceHistory();
        var app = new RecorderApplication(new TestRecordingEngine(), history: history);
        var recording = new TestRecordingEngine().Result;

        await Assert.ThrowsAsync<IOException>(
            () => app.AcceptCompletedRecordingAsync(recording));
        await app.AcceptCompletedRecordingAsync(recording);

        Assert.Same(recording, Assert.Single(history.Recordings));
    }

    internal static RecordingRequest ValidRequest()
    {
        return new RecordingRequest(
            AudioSource.MicrophoneAndSystemAudio,
            "microphone",
            "playback",
            Path.GetTempPath(),
            null);
    }
}

public sealed class FilenameAndStorageMutationTests
{
    [Fact]
    public void UniqueFileNamesIncrementWithoutOverwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var first = RecordingFileName.CreateUnique(directory, "team:sync", timestamp);
        File.WriteAllText(first, "first");
        var second = RecordingFileName.CreateUnique(directory, "team:sync", timestamp);
        File.WriteAllText(second, "second");
        var third = RecordingFileName.CreateUnique(directory, "team:sync", timestamp);

        Assert.Equal("20260102-030405-team-sync.wav", Path.GetFileName(first));
        Assert.Equal("20260102-030405-team-sync-1.wav", Path.GetFileName(second));
        Assert.Equal("20260102-030405-team-sync-2.wav", Path.GetFileName(third));
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    }

    [Fact]
    public void FileNamesPreserveValidCharactersAndHandleNoTitle()
    {
        Assert.Equal("valid title", RecordingFileName.Sanitize("valid title"));
        var path = RecordingFileName.CreateUnique(
            Path.GetTempPath(),
            "   ",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Assert.Matches(@"20260102-030405(?:-\d+)?\.wav$", Path.GetFileName(path));
    }

    [Fact]
    public void RequiredStorageUsesTheActualAudioFormat()
    {
        const long mono =
            48_000L * 2 * (4 * 60 * 60);
        const long stereo = mono * 2;

        Assert.Equal(mono, StoragePreflight.RequiredBytes(AudioSource.MicrophoneOnly));
        Assert.Equal(stereo, StoragePreflight.RequiredBytes(AudioSource.SystemAudioOnly));
        Assert.Equal(stereo, StoragePreflight.RequiredBytes(AudioSource.MicrophoneAndSystemAudio));
    }

    [Theory]
    [InlineData(999, 1000, false, true)]
    [InlineData(1000, 1000, false, false)]
    [InlineData(1001, 1000, false, false)]
    [InlineData(999, 1000, true, false)]
    public void StorageValidationCoversBoundaryAndExplicitConsent(
        long available,
        long required,
        bool allowed,
        bool hasError)
    {
        var error = StoragePreflight.Validate(available, required, allowed);

        Assert.Equal(hasError, error is not null);
        if (hasError)
        {
            Assert.Equal(
                "The destination does not have enough free space for a four-hour recording.",
                error);
        }
    }
}

public sealed class TranscriptionQueueMutationTests
{
    [Fact]
    public async Task EnqueuePersistsEveryStateAndCompletes()
    {
        var transcriber = new ControlledTranscriber();
        var writer = new ControlledWriter();
        var repository = new RecordingJobRepository();
        var logger = new RecordingLogger<TranscriptionQueue>();
        var queue = new TranscriptionQueue(transcriber, writer, repository, logger);
        var recording = Recording();

        await queue.EnqueueAsync(recording, CancellationToken.None);
        await queue.WaitForIdleAsync(CancellationToken.None);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(TranscriptionJobStatus.Succeeded, job.Status);
        Assert.Same(recording, job.Recording);
        Assert.Same(writer.Outputs, job.Outputs);
        Assert.Contains(TranscriptionJobStatus.Queued, repository.SavedStatuses);
        Assert.Contains(TranscriptionJobStatus.Running, repository.SavedStatuses);
        Assert.Equal(TranscriptionJobStatus.Succeeded, repository.LastSavedStatus);
        Assert.Contains(301, logger.EventIds);
        Assert.Contains(305, logger.EventIds);
        Assert.Contains(306, logger.EventIds);
    }

    [Fact]
    public async Task RestoreOrdersJobsMarksRunningInterruptedAndStartsOnlyQueued()
    {
        var old = Recording() with { Id = Guid.NewGuid() };
        var newer = Recording() with { Id = Guid.NewGuid() };
        var interrupted = new TranscriptionJob(
            Guid.NewGuid(),
            old,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            TranscriptionJobStatus.Running,
            null,
            null);
        var queued = new TranscriptionJob(
            Guid.NewGuid(),
            newer,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TranscriptionJobStatus.Queued,
            null,
            null);
        var repository = new RecordingJobRepository([queued, interrupted]);
        var transcriber = new ControlledTranscriber(block: true);
        var logger = new RecordingLogger<TranscriptionQueue>();
        var queue = new TranscriptionQueue(
            transcriber,
            new ControlledWriter(),
            repository,
            logger);

        await queue.RestoreAsync(CancellationToken.None);
        await WaitForAsync(() => transcriber.StartedIds.Count == 1);

        Assert.Equal(interrupted.Id, queue.Jobs[0].Id);
        Assert.Equal(TranscriptionJobStatus.Interrupted, queue.Jobs[0].Status);
        Assert.Equal(queued.Id, queue.Jobs[1].Id);
        Assert.Equal(TranscriptionJobStatus.Running, queue.Jobs[1].Status);
        Assert.True(repository.SavedSnapshots.Count >= 2);
        transcriber.Release();
        await queue.WaitForIdleAsync(CancellationToken.None);
        Assert.Equal(TranscriptionJobStatus.Succeeded, queued.Status);
        Assert.Contains(300, logger.EventIds);
    }

    [Fact]
    public async Task CancelUnknownJobIsANoOpButQueuedAndRunningAreObservable()
    {
        var transcriber = new ControlledTranscriber(block: true);
        var repository = new RecordingJobRepository();
        var logger = new RecordingLogger<TranscriptionQueue>();
        var queue = new TranscriptionQueue(
            transcriber,
            new ControlledWriter(),
            repository,
            logger);
        await queue.EnqueueAsync(Recording(), CancellationToken.None);
        await WaitForAsync(() => queue.Jobs[0].Status == TranscriptionJobStatus.Running);
        await queue.EnqueueAsync(Recording(), CancellationToken.None);

        await queue.CancelAsync(Guid.NewGuid(), CancellationToken.None);
        await queue.CancelAsync(queue.Jobs[1].Id, CancellationToken.None);
        Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[1].Status);
        Assert.Contains(
            repository.SavedSnapshots.Last(),
            status => status == TranscriptionJobStatus.Canceled);

        await queue.CancelAsync(queue.Jobs[0].Id, CancellationToken.None);
        transcriber.Release();
        await queue.WaitForIdleAsync(CancellationToken.None);
        Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[0].Status);
        Assert.Contains(TranscriptionJobStatus.Canceled, repository.SavedStatuses);
        Assert.Contains(302, logger.EventIds);
        Assert.Contains(303, logger.EventIds);
        Assert.Contains(307, logger.EventIds);
    }

    [Fact]
    public async Task FailureRecordsActionableErrorAndRetryCreatesANewJob()
    {
        var transcriber = new ControlledTranscriber(failure: new IOException("disk full"));
        var repository = new RecordingJobRepository();
        var logger = new RecordingLogger<TranscriptionQueue>();
        var queue = new TranscriptionQueue(
            transcriber,
            new ControlledWriter(),
            repository,
            logger);
        var recording = Recording();
        await queue.EnqueueAsync(recording, CancellationToken.None);
        await queue.WaitForIdleAsync(CancellationToken.None);

        var failed = Assert.Single(queue.Jobs);
        Assert.Equal(TranscriptionJobStatus.Failed, failed.Status);
        Assert.Equal("disk full", failed.Error);
        Assert.Null(failed.Outputs);

        transcriber.Failure = null;
        var retry = await queue.RetryAsync(recording, CancellationToken.None);
        await queue.WaitForIdleAsync(CancellationToken.None);
        Assert.NotEqual(failed.Id, retry.Id);
        Assert.Equal(2, queue.Jobs.Count);
        Assert.Equal(TranscriptionJobStatus.Succeeded, retry.Status);
        Assert.Contains(304, logger.EventIds);
        Assert.Contains(903, logger.EventIds);
    }

    [Fact]
    public async Task IdleWaitAndDuplicateWorkerStartsDoNotDuplicateWork()
    {
        var transcriber = new ControlledTranscriber(block: true);
        var repository = new RecordingJobRepository();
        var queue = new TranscriptionQueue(transcriber, new ControlledWriter(), repository);
        await queue.WaitForIdleAsync(CancellationToken.None);
        await queue.EnqueueAsync(Recording(), CancellationToken.None);
        await WaitForAsync(() => queue.Jobs[0].Status == TranscriptionJobStatus.Running);
        await queue.EnqueueAsync(Recording(), CancellationToken.None);

        transcriber.Release();
        await queue.WaitForIdleAsync(CancellationToken.None);

        Assert.Equal(2, transcriber.StartedIds.Count);
        Assert.All(queue.Jobs, job => Assert.Equal(TranscriptionJobStatus.Succeeded, job.Status));
    }

    [Fact]
    public async Task RetryPersistsTheQueuedStateAndWaitForIdleWaitsForCompletion()
    {
        var transcriber = new ControlledTranscriber(block: true);
        var repository = new RecordingJobRepository();
        var queue = new TranscriptionQueue(transcriber, new ControlledWriter(), repository);
        var retry = await queue.RetryAsync(Recording(), CancellationToken.None);
        Assert.Contains(
            repository.SavedSnapshots,
            snapshot => snapshot.Contains(TranscriptionJobStatus.Queued));

        await WaitForAsync(() => retry.Status == TranscriptionJobStatus.Running);
        var idle = queue.WaitForIdleAsync(CancellationToken.None);
        await Task.Delay(25);
        Assert.False(idle.IsCompleted);

        transcriber.Release();
        await idle;
        Assert.Equal(TranscriptionJobStatus.Succeeded, retry.Status);
    }

    [Fact]
    public async Task RecoveryEmitsCandidateSuccessAndFailureEvents()
    {
        var logger = new RecordingLogger<RecoveryManager>();
        var candidate = new RecoveryCandidate(
            Guid.NewGuid(),
            "state",
            "English",
            TimeSpan.FromSeconds(1),
            DateTimeOffset.UtcNow);
        var success = new RecordingRecoveryStorage(candidate);
        var recovery = new RecoveryManager(success, logger);

        await recovery.FindAsync();
        await recovery.RecoverAsync(candidate);
        var failureStorage = new RecordingRecoveryStorage(
            candidate,
            new IOException("Recovery storage failed."));
        var failure = new RecoveryManager(failureStorage, logger);
        await failure.RecoverAsync(candidate);

        Assert.Contains(400, logger.EventIds);
        Assert.Contains(401, logger.EventIds);
        Assert.Contains(904, logger.EventIds);
    }

    private static SavedRecording Recording()
    {
        return new SavedRecording(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav"),
            "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(2));
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline);
            await Task.Delay(10);
        }
    }

    private sealed class ControlledTranscriber : ILocalTranscriber
    {
        private readonly bool _block;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledTranscriber(bool block = false, Exception? failure = null)
        {
            _block = block;
            Failure = failure;
        }

        public Exception? Failure { get; set; }
        public List<Guid> StartedIds { get; } = [];

        public async Task<TranscriptDocument> TranscribeAsync(
            SavedRecording recording,
            CancellationToken cancellationToken)
        {
            StartedIds.Add(recording.Id);
            if (_block)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return new TranscriptDocument(
                recording,
                recording.Language,
                [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "text")]);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ControlledWriter : ITranscriptWriter
    {
        public TranscriptOutputs Outputs { get; } =
            new("recording.txt", "recording.srt");

        public Task<TranscriptOutputs> WriteAsync(
            TranscriptDocument document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Outputs);
        }

    }

    private sealed class RecordingJobRepository(
        IEnumerable<TranscriptionJob>? initial = null) : ITranscriptionJobRepository
    {
        private readonly List<TranscriptionJob> _initial = initial?.ToList() ?? [];
        public List<TranscriptionJobStatus> SavedStatuses { get; } = [];
        public List<TranscriptionJobStatus[]> SavedSnapshots { get; } = [];
        public TranscriptionJobStatus LastSavedStatus =>
            SavedStatuses.LastOrDefault();

        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TranscriptionJob>>(_initial.ToArray());
        }

        public Task SaveAsync(
            IReadOnlyList<TranscriptionJob> jobs,
            CancellationToken cancellationToken)
        {
            SavedStatuses.AddRange(jobs.Select(job => job.Status));
            SavedSnapshots.Add(jobs.Select(job => job.Status).ToArray());
            return Task.CompletedTask;
        }
    }
}

internal sealed class TestRecordingEngine : IRecordingEngine
{
    public TestRecordingEngine()
    {
        Result = new SavedRecording(
            Guid.NewGuid(),
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav"),
            "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(10));
    }

    public RecordingStartException? StartFailure { get; init; }
    public IOException? StopFailure { get; init; }
    public string? LastFailure { get; init; }
    public SavedRecording Result { get; }
    public bool Started { get; private set; }
    public int StartCalls { get; private set; }
    public int PauseCalls { get; private set; }
    public int ResumeCalls { get; private set; }
    public int StopCalls { get; private set; }

    public Task StartAsync(
        RecordingRequest request,
        CancellationToken cancellationToken)
    {
        StartCalls++;
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        Started = true;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        PauseCalls++;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        ResumeCalls++;
        return Task.CompletedTask;
    }

    public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
    {
        StopCalls++;
        if (StopFailure is not null)
        {
            throw StopFailure;
        }

        return Task.FromResult(Result);
    }
}

internal sealed class TestQueue : ITranscriptionQueue
{
    public List<SavedRecording> Recordings { get; } = [];

    public Task EnqueueAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        Recordings.Add(recording);
        return Task.CompletedTask;
    }
}

internal sealed class TestModels(bool available) : ITranscriptionModelStore
{
    public bool HasCompatibleModel(string language) => available;
    public string? GetModelPath(string language) => available ? "model.bin" : null;
}

internal sealed class TestHistory : IRecordingHistory
{
    public List<SavedRecording> Recordings { get; } = [];

    public Task<IReadOnlyList<SavedRecording>> LoadAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<SavedRecording>>(Recordings);
    }

    public Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        Recordings.Add(recording);
        return Task.CompletedTask;
    }
}

internal sealed class FailOnceHistory : IRecordingHistory
{
    private bool _fail = true;
    public List<SavedRecording> Recordings { get; } = [];

    public Task<IReadOnlyList<SavedRecording>> LoadAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<SavedRecording>>(Recordings);
    }

    public Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        if (_fail)
        {
            _fail = false;
            throw new IOException("Unable to persist recording history.");
        }

        Recordings.Add(recording);
        return Task.CompletedTask;
    }
}

internal sealed class FailAlwaysHistory : IRecordingHistory
{
    public Task<IReadOnlyList<SavedRecording>> LoadAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<SavedRecording>>([]);
    }

    public Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        throw new IOException("Unable to persist recording history.");
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<int> EventIds { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        EventIds.Add(eventId.Id);
    }
}

internal sealed class RecordingRecoveryStorage(
    RecoveryCandidate candidate,
    Exception? failure = null) : IRecoveryStorage
{
    public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RecoveryCandidate>>([candidate]);
    }

    public Task<SavedRecording> RecoverAsync(
        RecoveryCandidate requested,
        CancellationToken cancellationToken)
    {
        if (failure is not null)
        {
            throw failure;
        }

        return Task.FromResult(new SavedRecording(
            candidate.RecordingId,
            "recording.wav",
            "English",
            RecordingOutcome.Recovered,
            TimeSpan.FromSeconds(1)));
    }

    public Task DiscardAsync(
        RecoveryCandidate requested,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
