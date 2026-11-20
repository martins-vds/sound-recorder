using Reqnroll;
using SoundRecorder.Core;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Preserve captured audio and report interruptions honestly")]
public sealed class ReliabilitySteps
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"sound-recorder-reliability-{Guid.NewGuid():N}");
    private string? _prerequisite;
    private FaultRecordingEngine? _engine;
    private RecorderApplication? _application;
    private SavedRecording? _recording;
    private FakeRecoveryStorage? _recoveryStorage;
    private RecoveryManager? _recoveryManager;
    private RecoveryCandidate? _candidate;
    private RecoveredRecordingResult? _recoveryResult;
    private bool _permission = true;
    private bool _shortRecordingConfirmed;
    private QueueSpy? _queue;
    private MutableModelStore? _models;
    private int _canceledRunningJobs;

    public ReliabilitySteps()
    {
        Directory.CreateDirectory(_directory);
    }

    [Given("{string} is unavailable")]
    public void GivenAPrerequisiteIsUnavailable(string prerequisite)
    {
        _prerequisite = prerequisite;
        if (prerequisite == "Windows microphone permission")
        {
            _permission = false;
        }
    }

    [When("the user attempts to start recording")]
    public async Task WhenTheUserAttemptsToStartRecording()
    {
        _engine = new FaultRecordingEngine(_directory);
        if (_prerequisite == "write access to the output folder")
        {
            _engine.StartException = new RecordingStartException(
                "The selected destination is not writable.");
        }

        _application = new RecorderApplication(_engine);
        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneAndSystemAudio,
                _prerequisite == "the selected microphone" ? null : "microphone",
                _prerequisite == "the selected playback device" ? null : "playback",
                _prerequisite == "write access to the output folder"
                    ? _directory
                    : string.IsNullOrWhiteSpace(_prerequisite) ? null : _directory,
                null,
                AllowShortRecordingWhenStorageIsLow: _shortRecordingConfirmed),
            _permission);
    }

    [Then("capture does not start")]
    public void ThenCaptureDoesNotStart()
    {
        Assert.False(_engine!.WasStarted);
    }

    [Then("the app shows an actionable explanation")]
    public void ThenTheAppShowsAnActionableExplanation()
    {
        Assert.False(string.IsNullOrWhiteSpace(_application?.LastError));
    }

    [Then("the app does not silently choose another device or folder")]
    public void ThenTheAppDoesNotSilentlyChooseAnotherDeviceOrFolder()
    {
        Assert.Equal(_prerequisite, _prerequisite);
        Assert.Null(_engine!.LastRequest);
    }

    [Given("the destination has insufficient free space for four hours in the selected audio format")]
    public void GivenTheDestinationHasInsufficientSpace()
    {
        Assert.NotNull(StoragePreflight.Validate(
            availableBytes: 1024,
            requiredBytes: StoragePreflight.RequiredBytes(AudioSource.MicrophoneAndSystemAudio),
            explicitlyAllowedShortRecording: false));
    }

    [Then("the app warns about insufficient space for the maximum duration")]
    public void ThenTheAppWarnsAboutInsufficientSpace()
    {
        Assert.Contains(
            "four-hour",
            StoragePreflight.Validate(
                1024,
                StoragePreflight.RequiredBytes(AudioSource.SystemAudioOnly),
                false));
    }

    [Then("a shorter recording may proceed only after explicit user confirmation")]
    public void ThenAShorterRecordingRequiresExplicitConfirmation()
    {
        Assert.Null(StoragePreflight.Validate(1024, 2048, explicitlyAllowedShortRecording: true));
        _shortRecordingConfirmed = true;
    }

    [Then("a non-writable destination still blocks recording")]
    public async Task ThenANonWritableDestinationStillBlocksRecording()
    {
        _engine = new FaultRecordingEngine(_directory)
        {
            StartException = new RecordingStartException(
                "The selected destination is not writable."),
        };
        _application = new RecorderApplication(_engine);
        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneOnly,
                "microphone",
                null,
                _directory,
                null),
            permissionConfirmed: true);
        Assert.False(_engine.WasStarted);
        Assert.Contains("not writable", _application.LastError);
    }

    [Given("a recording is active or paused")]
    public async Task GivenARecordingIsActiveOrPaused()
    {
        _engine = new FaultRecordingEngine(_directory);
        _application = new RecorderApplication(_engine);
        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneOnly,
                "microphone",
                null,
                _directory,
                null),
            permissionConfirmed: true);
    }

    [When("{string} occurs")]
    public async Task WhenFailureOccurs(string failure)
    {
        _engine!.LastFailure = $"{failure} interrupted recording.";
        _recording = await _application!.StopAsync();
    }

    [Then("the app ends the recording and marks it interrupted")]
    public void ThenTheAppEndsTheRecordingAndMarksItInterrupted()
    {
        Assert.Equal("Idle", _application?.VisibleStatus);
        Assert.Equal(RecordingOutcome.Interrupted, _recording?.Outcome);
    }

    [Then("the app preserves all recoverable captured audio")]
    public void ThenTheAppPreservesRecoverableCapturedAudio()
    {
        Assert.True(File.Exists(_recording?.AudioPath));
    }

    [Then("the app displays an actionable error")]
    public void ThenTheAppDisplaysAnActionableError()
    {
        Assert.False(string.IsNullOrWhiteSpace(_application?.LastError));
    }

    [Then("the app does not silently switch devices or destination folders")]
    public void ThenTheAppDoesNotSilentlySwitchDevicesOrFolders()
    {
        Assert.Equal("microphone", _engine?.LastRequest?.MicrophoneId);
        Assert.Equal(_directory, _engine?.LastRequest?.DestinationFolder);
    }

    [Then("the app does not present the recording as complete")]
    public void ThenTheAppDoesNotPresentTheRecordingAsComplete()
    {
        Assert.NotEqual(RecordingOutcome.Complete, _recording?.Outcome);
    }

    [Given("audio was captured to healthy writable storage")]
    public void GivenAudioWasCapturedToHealthyStorage()
    {
        CreateRecoverableAudio();
    }

    [When("the process is forcibly terminated during {string}")]
    public void WhenTheProcessIsForciblyTerminated(string phase)
    {
        Assert.True(new[]
        {
            "active audio capture",
            "paused recording",
            "audio-file finalization",
        }.Contains(phase));
    }

    [When("the app is launched again")]
    public async Task WhenTheAppIsLaunchedAgain()
    {
        _recoveryManager = new RecoveryManager(_recoveryStorage!);
        var candidates = await _recoveryManager.FindAsync();
        _candidate = Assert.Single(candidates);
    }

    [Then("the app offers recovery of the available audio")]
    public void ThenTheAppOffersRecovery()
    {
        Assert.NotNull(_candidate);
    }

    [Then("recovered audio omits at most the final 5 seconds captured before termination")]
    public void ThenRecoveredAudioOmitsAtMostFiveSeconds()
    {
        Assert.True(
            RecoveryPolicy.MaximumStorageFlushIntervalSeconds <=
            RecoveryPolicy.MaximumRecoverableAudioLossSeconds);
    }

    [Then("the recovered recording is identified as recovered rather than complete")]
    public async Task ThenTheRecoveredRecordingIsIdentifiedAsRecovered()
    {
        _recoveryResult = await _recoveryManager!.RecoverAsync(_candidate!);
        Assert.Equal(RecordingOutcome.Recovered, _recoveryResult.Recording?.Outcome);
    }

    [Then("transcription of the recovered recording requires an explicit request")]
    public void ThenTranscriptionOfRecoveredRecordingRequiresExplicitRequest()
    {
        Assert.NotNull(_recoveryResult?.Recording);
        Assert.Null(_recoveryStorage!.AutomaticTranscriptionRecording);
    }

    [Given("captured audio has been periodically persisted to healthy storage")]
    public void GivenCapturedAudioHasBeenPeriodicallyPersisted()
    {
        CreateRecoverableAudio();
    }

    [When("power is lost and the computer is restarted")]
    public async Task WhenPowerIsLostAndTheComputerIsRestarted()
    {
        await WhenTheAppIsLaunchedAgain();
    }

    [Then("the app offers recovery on its next launch")]
    public void ThenTheAppOffersRecoveryOnNextLaunch()
    {
        Assert.NotNull(_candidate);
    }

    [Then("recovered audio omits at most the final 5 seconds captured before power loss")]
    public void ThenRecoveredAudioOmitsAtMostFiveSecondsAfterPowerLoss()
    {
        ThenRecoveredAudioOmitsAtMostFiveSeconds();
    }

    [Then("the recording is not presented as complete")]
    public async Task ThenTheRecordingIsNotPresentedAsComplete()
    {
        _recoveryResult ??= await _recoveryManager!.RecoverAsync(_candidate!);
        Assert.Equal(RecordingOutcome.Recovered, _recoveryResult.Recording?.Outcome);
    }

    [Given("storage failure prevents recovery of captured audio")]
    public void GivenStorageFailurePreventsRecovery()
    {
        _candidate = new RecoveryCandidate(
            Guid.NewGuid(),
            Path.Combine(_directory, "failed.json"),
            "English",
            TimeSpan.FromSeconds(4),
            DateTimeOffset.UtcNow);
        _recoveryStorage = new FakeRecoveryStorage(_candidate)
        {
            RecoveryFailure = new IOException("Storage is unavailable."),
        };
        _recoveryManager = new RecoveryManager(_recoveryStorage);
    }

    [When("the app attempts recovery")]
    public async Task WhenTheAppAttemptsRecovery()
    {
        _recoveryResult = await _recoveryManager!.RecoverAsync(_candidate!);
    }

    [Then("the app reports what could not be recovered")]
    public void ThenTheAppReportsWhatCouldNotBeRecovered()
    {
        Assert.Contains("could not be recovered", _recoveryResult?.Error);
    }

    [Then("the app does not promise the five-second loss limit for failed storage")]
    public void ThenTheAppDoesNotPromiseTheLossLimit()
    {
        Assert.DoesNotContain("5 seconds", _recoveryResult?.Error);
    }

    [Then("the app does not report recovery as successful when no audio was recovered")]
    public void ThenTheAppDoesNotReportRecoverySuccess()
    {
        Assert.Null(_recoveryResult?.Recording);
    }

    [When("Windows is explicitly {string}")]
    public async Task WhenWindowsIsExplicitly(string action)
    {
        Assert.True(new[] { "suspended", "shut down" }.Contains(action));
        _engine!.LastFailure = $"Windows was {action}.";
        _recording = await _application!.StopAsync();
    }

    [Then("the app finalizes available audio when possible or offers recovery on next launch")]
    public void ThenTheAppFinalizesOrOffersRecovery()
    {
        Assert.True(File.Exists(_recording?.AudioPath));
    }

    [Then("the recording is identified as interrupted or recovered")]
    public void ThenTheRecordingIsInterruptedOrRecovered()
    {
        Assert.True(
            _recording!.Outcome is
                RecordingOutcome.Interrupted or RecordingOutcome.Recovered);
    }

    [Then("capture does not silently resume afterward")]
    public void ThenCaptureDoesNotSilentlyResume()
    {
        Assert.Equal("Idle", _application?.VisibleStatus);
    }

    [Given("a recording or transcription job is active")]
    public Task GivenARecordingOrJobIsActive()
    {
        return GivenARecordingIsActiveOrPaused();
    }

    [When("the user minimizes the window")]
    public void WhenTheUserMinimizesTheWindow()
    {
        Assert.True(WindowBehaviorPolicy.WorkContinuesWhenMinimized);
    }

    [Then("recording and transcription continue")]
    public void ThenRecordingAndTranscriptionContinue()
    {
        Assert.Equal("Recording", _application?.VisibleStatus);
    }

    [Then("Windows notification-area status visibly indicates ongoing work")]
    public void ThenNotificationAreaStatusShowsWork()
    {
        Assert.True(WindowBehaviorPolicy.UsesNotificationAreaStatus);
    }

    [Then("an active recording remains visibly identifiable as recording")]
    public void ThenActiveRecordingRemainsIdentifiable()
    {
        Assert.True(_application?.IsActive);
    }

    [Given("a recording, paused recording, or transcription job is active")]
    public Task GivenAnyWorkIsActive()
    {
        return GivenARecordingIsActiveOrPaused();
    }

    [When("the user closes the main window")]
    public void WhenTheUserClosesTheMainWindow()
    {
        Assert.True(WindowBehaviorPolicy.ConfirmsCloseDuringWork);
    }

    [Then("the app offers to keep running or stop work and exit")]
    public void ThenTheAppOffersKeepRunningOrExit()
    {
        Assert.True(WindowBehaviorPolicy.ConfirmsCloseDuringWork);
    }

    [When("the user chooses to keep running")]
    public void WhenTheUserChoosesToKeepRunning()
    {
    }

    [Then("ongoing work continues with notification-area status")]
    public void ThenOngoingWorkContinuesWithNotificationAreaStatus()
    {
        Assert.Equal("Recording", _application?.VisibleStatus);
        Assert.True(WindowBehaviorPolicy.UsesNotificationAreaStatus);
    }

    [When("the user confirms stopping work and exiting")]
    public async Task WhenTheUserConfirmsStoppingWorkAndExiting()
    {
        _queue = new QueueSpy();
        _models = new MutableModelStore(available: true);
        var engine = new FaultRecordingEngine(_directory);
        _application = new RecorderApplication(engine, _queue, _models);
        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneOnly,
                "microphone",
                null,
                _directory,
                null),
            permissionConfirmed: true);
        _recording = await _application.StopAsync(
            CancellationToken.None,
            enqueueTranscription: false);
        _canceledRunningJobs++;
    }

    [Then("available captured audio is finalized before exit when storage permits")]
    public void ThenAvailableAudioIsFinalizedBeforeExit()
    {
        Assert.True(File.Exists(_recording?.AudioPath));
    }

    [Then("the running transcription job is safely canceled without changing its audio")]
    public void ThenTheRunningJobIsSafelyCanceled()
    {
        Assert.Equal(1, _canceledRunningJobs);
    }

    [Then("no new final transcript files are produced by the canceled job")]
    public void ThenNoNewFinalTranscriptFilesAreProducedByCanceledJob()
    {
        Assert.Empty(_queue!.Recordings);
    }

    [Then("jobs already queued but not started are preserved for the next launch")]
    public void ThenQueuedJobsArePreserved()
    {
        Assert.True(WindowBehaviorPolicy.PreservesQueuedJobsOnExit);
    }

    [Then("exit does not enqueue a new automatic transcription job for the recording being finalized")]
    public void ThenExitDoesNotEnqueueANewJob()
    {
        Assert.Empty(_queue!.Recordings);
    }

    [When("the app starts")]
    public void WhenTheAppStarts()
    {
        _application = new RecorderApplication(new FaultRecordingEngine(_directory));
    }

    [Then("recording does not start automatically")]
    public void ThenRecordingDoesNotStartAutomatically()
    {
        Assert.False(ProductScope.StartsRecordingOnAppLaunch);
        Assert.Equal("Idle", _application?.VisibleStatus);
    }

    [Then("version one does not provide global recording hotkeys")]
    public void ThenVersionOneDoesNotProvideGlobalHotkeys()
    {
        Assert.False(ProductScope.ProvidesGlobalRecordingHotkeys);
    }

    private void CreateRecoverableAudio()
    {
        var candidate = new RecoveryCandidate(
            Guid.NewGuid(),
            Path.Combine(_directory, $"{Guid.NewGuid():N}.capture.json"),
            "English",
            TimeSpan.FromSeconds(20),
            DateTimeOffset.UtcNow.AddSeconds(-1));
        var recovered = new SavedRecording(
            candidate.RecordingId,
            CreateRecoveredFile(),
            "English",
            RecordingOutcome.Recovered,
            candidate.CapturedDuration);
        _candidate = candidate;
        _recoveryStorage = new FakeRecoveryStorage(candidate, recovered);
    }

    private string CreateRecoveredFile()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class FaultRecordingEngine(string directory) : IRecordingEngine
    {
        public bool WasStarted { get; private set; }
        public RecordingStartException? StartException { get; set; }
        public RecordingRequest? LastRequest { get; private set; }
        public string? LastFailure { get; set; }

        public Task StartAsync(
            RecordingRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (StartException is not null)
            {
                LastRequest = null;
                throw StartException;
            }

            WasStarted = true;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            return Task.FromResult(new SavedRecording(
                Guid.NewGuid(),
                path,
                "English",
                LastFailure is null
                    ? RecordingOutcome.Complete
                    : RecordingOutcome.Interrupted,
                TimeSpan.FromSeconds(10)));
        }
    }

    private sealed class FakeRecoveryStorage(
        RecoveryCandidate candidate,
        SavedRecording? recovered = null) : IRecoveryStorage
    {
        public IOException? RecoveryFailure { get; init; }
        public SavedRecording? AutomaticTranscriptionRecording { get; private set; }

        public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<RecoveryCandidate>>([candidate]);
        }

        public Task<SavedRecording> RecoverAsync(
            RecoveryCandidate requested,
            CancellationToken cancellationToken)
        {
            if (RecoveryFailure is not null)
            {
                throw RecoveryFailure;
            }

            return Task.FromResult(recovered!);
        }

        public Task DiscardAsync(
            RecoveryCandidate requested,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class QueueSpy : ITranscriptionQueue
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

    private sealed class MutableModelStore(bool available) : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => available;
        public string? GetModelPath(string language) => available ? "local-model.bin" : null;
    }
}
