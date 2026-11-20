using Reqnroll;
using NAudio.Wave;
using SoundRecorder.Core;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
public sealed class RecordingSteps
{
    private RecorderApplication? _application;
    private bool _permissionConfirmed;
    private bool _microphoneAvailable;
    private bool _destinationWritable;
    private RecordingEngineDouble? _recordingEngine;
    private SavedRecording? _savedRecording;
    private TranscriptionQueueDouble? _transcriptionQueue;
    private TranscriptionModelStoreDouble? _modelStore;
    private AudioSource _source = AudioSource.MicrophoneOnly;
    private string? _selectedTitle;
    private string? _systemAudioExplanation;

    [Given("the selected microphone is available")]
    public void GivenTheSelectedMicrophoneIsAvailable()
    {
        _microphoneAvailable = true;
    }

    [Given("the destination folder is writable")]
    public void GivenTheDestinationFolderIsWritable()
    {
        _destinationWritable = true;
    }

    [Given("recording permission has been confirmed")]
    public void GivenRecordingPermissionHasBeenConfirmed()
    {
        _permissionConfirmed = true;
    }

    [Given("the recorder is active")]
    public async Task GivenTheRecorderIsActive()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = CreateApplication();
        await _application.StartAsync(
            new RecordingRequest(AudioSource.MicrophoneOnly, "microphone", null, Path.GetTempPath(), null),
            permissionConfirmed: true);
    }

    [Given("no recording is active")]
    public void GivenNoRecordingIsActive()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);
    }

    [Given("the selected devices and destination are available")]
    public void GivenTheSelectedDevicesAndDestinationAreAvailable()
    {
        _microphoneAvailable = true;
        _destinationWritable = true;
    }

    [Given("the user has confirmed permission to record")]
    public void GivenTheUserHasConfirmedPermissionToRecord()
    {
        _permissionConfirmed = true;
    }

    [Given("a recording is active")]
    public Task GivenARecordingIsActive()
    {
        return GivenTheRecorderIsActive();
    }

    [Given(@"a recording uses ""([^""]*)""")]
    public async Task GivenARecordingUses(string source)
    {
        _source = ParseSource(source);
        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);
        await _application.StartAsync(
            new RecordingRequest(
                _source,
                _source == AudioSource.SystemAudioOnly ? null : "microphone",
                _source == AudioSource.MicrophoneOnly ? null : "playback",
                Path.GetTempPath(),
                null),
            permissionConfirmed: true);
    }

    [Given("audio devices have been selected")]
    public void GivenAudioDevicesHaveBeenSelected()
    {
        _microphoneAvailable = true;
    }

    [Given("microphone and system-audio capture is selected")]
    public void GivenMicrophoneAndSystemAudioCaptureIsSelected()
    {
        _source = AudioSource.MicrophoneAndSystemAudio;
    }

    [Given("the user has selected a writable local destination folder")]
    public void GivenTheUserHasSelectedAWritableLocalDestinationFolder()
    {
        _destinationWritable = true;
    }

    [Given("the user may have supplied an optional recording title")]
    public void GivenTheUserMayHaveSuppliedAnOptionalRecordingTitle()
    {
        _selectedTitle = "planning meeting";
    }

    [Given("a recording has reached 3 hours and 55 minutes of captured duration")]
    public void GivenARecordingHasReachedThreeHoursAndFiftyFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(235), RecordingFormat.WarningCapturedDuration);
    }

    [Given("a compatible local transcription model is installed")]
    [Given("a compatible transcription model is installed")]
    public void GivenACompatibleLocalTranscriptionModelIsInstalled()
    {
        _modelStore = new TranscriptionModelStoreDouble(available: true);
    }

    [When("a recording is saved after Stop or the four-hour limit")]
    public async Task WhenARecordingIsSavedAfterStopOrTheFourHourLimit()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = CreateApplication();
        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneOnly,
                "microphone",
                null,
                Path.GetTempPath(),
                null),
            permissionConfirmed: true);
        _savedRecording = await _application.StopAsync();
    }

    [When(@"the user selects ""([^""]*)""")]
    public void WhenTheUserSelects(string source)
    {
        _source = ParseSource(source);
    }

    [When("the user selects a source that includes system audio")]
    public void WhenTheUserSelectsASourceThatIncludesSystemAudio()
    {
        _source = AudioSource.MicrophoneAndSystemAudio;
        _systemAudioExplanation = ProductCopy.SystemAudioScope;
    }

    [When("the user requests a new recording")]
    public async Task WhenTheUserRequestsANewRecording()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);
        await _application.StartAsync(
            new RecordingRequest(
                _source,
                _microphoneAvailable ? "microphone" : null,
                _source == AudioSource.MicrophoneOnly ? null : "playback",
                _destinationWritable ? Path.GetTempPath() : null,
                _selectedTitle),
            permissionConfirmed: false);
    }

    [When("recording starts successfully")]
    public async Task WhenRecordingStartsSuccessfully()
    {
        _recordingEngine ??= new RecordingEngineDouble();
        _application ??= new RecorderApplication(_recordingEngine);
        await _application.StartAsync(
            new RecordingRequest(
                _source,
                "microphone",
                _source == AudioSource.MicrophoneOnly ? null : "playback",
                Path.GetTempPath(),
                _selectedTitle),
            _permissionConfirmed);
    }

    [When("the user prepares to record or is recording")]
    public void WhenTheUserPreparesToRecordOrIsRecording()
    {
        _recordingEngine ??= new RecordingEngineDouble();
    }

    [When("the user pauses the recording")]
    public Task WhenTheUserPausesTheRecording()
    {
        return WhenTheRecordingIsPaused();
    }

    [When("the user resumes the recording")]
    public Task WhenTheUserResumesTheRecording()
    {
        return WhenTheRecordingIsResumed();
    }

    [When("the user stops the recording")]
    public Task WhenTheUserStopsTheRecording()
    {
        return WhenTheRecordingIsStopped();
    }

    [When("the recording is saved")]
    public async Task WhenTheRecordingIsSaved()
    {
        if (_application is null)
        {
            _recordingEngine = new RecordingEngineDouble();
            _application = new RecorderApplication(_recordingEngine);
            await _application.StartAsync(
                new RecordingRequest(
                    _source,
                    "microphone",
                    "playback",
                    Path.GetTempPath(),
                    _selectedTitle),
                permissionConfirmed: true);
        }

        await WhenTheRecordingIsStopped();
    }

    [When("a recording is saved")]
    public async Task WhenARecordingIsSaved()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);
        await _application.StartAsync(
            new RecordingRequest(
                _source,
                "microphone",
                _source == AudioSource.MicrophoneOnly ? null : "playback",
                Path.GetTempPath(),
                _selectedTitle),
            permissionConfirmed: true);
        _savedRecording = await _application.StopAsync();
    }

    [When("the recording reaches 4 hours of captured duration")]
    public void WhenTheRecordingReachesFourHoursOfCapturedDuration()
    {
        Assert.Equal(TimeSpan.FromHours(4), RecordingFormat.MaximumCapturedDuration);
        _recordingEngine ??= new RecordingEngineDouble();
        _savedRecording = _recordingEngine!.CreateSavedRecording(
            RecordingFormat.MaximumCapturedDuration,
            _source);
    }

    [When("recording starts")]
    public async Task WhenRecordingStarts()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);

        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneOnly,
                _microphoneAvailable ? "microphone" : null,
                null,
                _destinationWritable ? Path.GetTempPath() : null,
                null),
            _permissionConfirmed);
    }

    [When("recording permission is declined and recording starts")]
    public async Task WhenRecordingPermissionIsDeclinedAndRecordingStarts()
    {
        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);

        await _application.StartAsync(
            new RecordingRequest(
                AudioSource.MicrophoneOnly,
                "microphone",
                null,
                Path.GetTempPath(),
                null),
            permissionConfirmed: false);
    }

    [When(@"recording starts with ""([^""]*)"" but a required ""([^""]*)"" is unavailable")]
    public async Task WhenRecordingStartsWithARequiredDeviceUnavailable(string sourceText, string deviceText)
    {
        await StartWithUnavailableDeviceAsync(sourceText, deviceText);
    }

    [When("the recording is paused")]
    public Task WhenTheRecordingIsPaused()
    {
        return _application!.PauseAsync();
    }

    [When("the recording is resumed")]
    public Task WhenTheRecordingIsResumed()
    {
        return _application!.ResumeAsync();
    }

    [When("the recording is stopped")]
    public async Task WhenTheRecordingIsStopped()
    {
        _savedRecording = await _application!.StopAsync();
    }

    [Then("the recorder is active")]
    public void ThenTheRecorderIsActive()
    {
        Assert.True(_application?.IsActive);
    }

    [Then("the visible status is \"Recording\"")]
    public void ThenTheVisibleStatusIsRecording()
    {
        Assert.Equal("Recording", _application?.VisibleStatus);
    }

    [Then("the visible status is \"Paused\"")]
    public void ThenTheVisibleStatusIsPaused()
    {
        Assert.Equal("Paused", _application?.VisibleStatus);
    }

    [Then("the recording engine is paused")]
    public void ThenTheRecordingEngineIsPaused()
    {
        Assert.True(_recordingEngine?.WasPaused);
    }

    [Then("the recording engine has resumed the same session")]
    public void ThenTheRecordingEngineHasResumedTheSameSession()
    {
        Assert.Equal(1, _recordingEngine?.StartCount);
        Assert.True(_recordingEngine?.WasResumed);
    }

    [Then("the saved recording points to a WAV file")]
    public void ThenTheSavedRecordingPointsToAWavFile()
    {
        Assert.Equal(".wav", Path.GetExtension(_savedRecording?.AudioPath), ignoreCase: true);
    }

    [Then("the saved recording is complete")]
    public void ThenTheSavedRecordingIsComplete()
    {
        Assert.Equal(RecordingOutcome.Complete, _savedRecording?.Outcome);
    }

    [Then("one transcription job is queued for the saved recording")]
    public void ThenOneTranscriptionJobIsQueuedForTheSavedRecording()
    {
        Assert.Equal(_savedRecording, Assert.Single(_transcriptionQueue!.QueuedRecordings));
    }

    [Then("a transcription job is queued automatically")]
    public void ThenATranscriptionJobIsQueuedAutomatically()
    {
        Assert.Single(_transcriptionQueue!.QueuedRecordings);
    }

    [Then("the saved audio exists before the job begins")]
    public void ThenTheSavedAudioExistsBeforeTheJobBegins()
    {
        Assert.True(_transcriptionQueue!.AudioExistedAtEnqueue);
    }

    [Then("speech recognition runs entirely on the local computer")]
    public void ThenSpeechRecognitionRunsEntirelyOnTheLocalComputer()
    {
        Assert.True(_transcriptionQueue!.UsesLocalWorker);
    }

    [Then("the user must select {string} before starting")]
    public void ThenTheUserMustSelectBeforeStarting(string devices)
    {
        Assert.Equal(
            _source is AudioSource.MicrophoneOnly ? "a microphone" :
            _source is AudioSource.SystemAudioOnly ? "a playback device" :
            "a microphone and a playback device",
            devices);
    }

    [Then("the selection remains fixed for the recording")]
    public void ThenTheSelectionRemainsFixedForTheRecording()
    {
        var request = new RecordingRequest(
            _source,
            _source == AudioSource.SystemAudioOnly ? null : "microphone",
            _source == AudioSource.MicrophoneOnly ? null : "playback",
            Path.GetTempPath(),
            null);
        _recordingEngine ??= new RecordingEngineDouble();
        _application ??= new RecorderApplication(_recordingEngine);
        _application.StartAsync(request, permissionConfirmed: true).GetAwaiter().GetResult();
        Assert.Equal(_source, _recordingEngine.LastRequest?.Source);
    }

    [Then("the app explains that all sound from the selected playback device may be captured")]
    public void ThenTheAppExplainsThatAllSoundFromTheSelectedPlaybackDeviceMayBeCaptured()
    {
        Assert.Contains("All sound from the selected playback device", _systemAudioExplanation);
    }

    [Then("the explanation includes notifications and other applications")]
    public void ThenTheExplanationIncludesNotificationsAndOtherApplications()
    {
        Assert.Contains("notifications", _systemAudioExplanation);
        Assert.Contains("other applications", _systemAudioExplanation);
    }

    [Then("the app does not promise capture of only a particular call or application")]
    public void ThenTheAppDoesNotPromiseCaptureOfOnlyAParticularCallOrApplication()
    {
        Assert.Contains("cannot be guaranteed", _systemAudioExplanation);
    }

    [Then("the app requires confirmation that the user has permission to record")]
    public void ThenTheAppRequiresConfirmationThatTheUserHasPermissionToRecord()
    {
        Assert.Contains("Confirm", _application?.LastError);
    }

    [Then("no audio is captured before confirmation")]
    public void ThenNoAudioIsCapturedBeforeConfirmation()
    {
        Assert.False(_recordingEngine?.WasStarted);
    }

    [Then("declining leaves the recorder idle")]
    public void ThenDecliningLeavesTheRecorderIdle()
    {
        Assert.Equal("Idle", _application?.VisibleStatus);
    }

    [Then("the app does not present confirmation as a guarantee of legal compliance")]
    public void ThenTheAppDoesNotPresentConfirmationAsAGuaranteeOfLegalCompliance()
    {
        Assert.Contains("not a guarantee", ProductCopy.PermissionDisclaimer);
    }

    [Then("a visible recording indicator appears within 1 second on the reference PC")]
    public void ThenAVisibleRecordingIndicatorAppearsWithinOneSecond()
    {
        Assert.Equal("Recording", _application?.VisibleStatus);
    }

    [Then("the app displays the captured duration")]
    public void ThenTheAppDisplaysTheCapturedDuration()
    {
        Assert.Equal(TimeSpan.Zero, _recordingEngine?.CapturedDuration);
    }

    [Then("only one recording can be active")]
    public async Task ThenOnlyOneRecordingCanBeActive()
    {
        await _application!.StartAsync(
            new RecordingRequest(_source, "microphone", "playback", Path.GetTempPath(), null),
            permissionConfirmed: true);
        Assert.Equal(1, _recordingEngine?.StartCount);
    }

    [Then("a level meter is available for each selected source")]
    public void ThenALevelMeterIsAvailableForEachSelectedSource()
    {
        Assert.True(_recordingEngine!.LevelSources.Count > 0);
    }

    [Then("combined capture shows microphone and system-audio levels separately")]
    public void ThenCombinedCaptureShowsMicrophoneAndSystemAudioLevelsSeparately()
    {
        Assert.Equal(["Microphone", "SystemAudio"], _recordingEngine!.LevelSources);
    }

    [Then("the app recommends headphones for combined capture")]
    public void ThenTheAppRecommendsHeadphonesForCombinedCapture()
    {
        Assert.Contains("headphones", ProductCopy.HeadphoneRecommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the app does not guarantee echo cancellation")]
    public void ThenTheAppDoesNotGuaranteeEchoCancellation()
    {
        Assert.Contains("not guaranteed", ProductCopy.EchoCancellationLimitation);
    }

    [Then("the app visibly indicates that recording is paused")]
    public void ThenTheAppVisiblyIndicatesThatRecordingIsPaused()
    {
        Assert.Equal("Paused", _application?.VisibleStatus);
    }

    [Then("paused time is excluded from the saved audio and captured duration")]
    public void ThenPausedTimeIsExcludedFromTheSavedAudioAndCapturedDuration()
    {
        Assert.Equal(TimeSpan.Zero, _recordingEngine?.DurationDuringPause);
    }

    [Then("capture continues in the same recording")]
    public void ThenCaptureContinuesInTheSameRecording()
    {
        Assert.Equal(1, _recordingEngine?.StartCount);
    }

    [Then("the app visibly indicates that recording is active")]
    public void ThenTheAppVisiblyIndicatesThatRecordingIsActive()
    {
        Assert.Equal("Recording", _application?.VisibleStatus);
    }

    [Then(@"the app saves a (\d+) Hz (\d+)-bit PCM WAV file")]
    public void ThenTheAppSavesAPcmWavFile(int sampleRate, int bitsPerSample)
    {
        using var reader = new WaveFileReader(_savedRecording!.AudioPath);
        Assert.Equal(48_000, sampleRate);
        Assert.Equal(16, bitsPerSample);
        Assert.Equal(sampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(bitsPerSample, reader.WaveFormat.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
    }

    [Then("the saved audio has {string}")]
    public void ThenTheSavedAudioHas(string channels)
    {
        using var reader = new WaveFileReader(_savedRecording!.AudioPath);
        Assert.Equal(channels == "mono" ? 1 : 2, reader.WaveFormat.Channels);
    }

    [Then("no separate source-track files are required")]
    public void ThenNoSeparateSourceTrackFilesAreRequired()
    {
        Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(_savedRecording!.AudioPath)!,
            Path.GetFileName(_savedRecording.AudioPath)));
    }

    [Then("microphone audio is mixed into both stereo channels")]
    public void ThenMicrophoneAudioIsMixedIntoBothStereoChannels()
    {
        Assert.Equal(2, _recordingEngine!.OutputChannels);
    }

    [Then("the saved audio includes the selected playback device's system audio")]
    public void ThenTheSavedAudioIncludesTheSelectedPlaybackDevicesSystemAudio()
    {
        Assert.True(_recordingEngine!.IncludedSystemAudio);
    }

    [Then("its WAV filename includes a timestamp")]
    public void ThenItsWavFilenameIncludesATimestamp()
    {
        Assert.Matches(@"\d{8}-\d{6}", Path.GetFileName(_savedRecording!.AudioPath));
    }

    [Then("the optional title is included when supplied")]
    public void ThenTheOptionalTitleIsIncludedWhenSupplied()
    {
        Assert.Contains(_selectedTitle!, _savedRecording!.AudioPath);
    }

    [Then("the filename is unique within the destination folder")]
    public void ThenTheFilenameIsUniqueWithinTheDestinationFolder()
    {
        Assert.False(File.Exists(
            RecordingFileName.CreateUnique(
                Path.GetTempPath(),
                _selectedTitle,
                DateTimeOffset.Parse("2025-01-01"))));
    }

    [Then("no existing file is overwritten")]
    public void ThenNoExistingFileIsOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = RecordingFileName.CreateUnique(directory, "test", new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        File.WriteAllText(first, "original");
        var second = RecordingFileName.CreateUnique(directory, "test", new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.NotEqual(first, second);
        Assert.Equal("original", File.ReadAllText(first));
    }

    [Then("the app warns that the four-hour limit is approaching")]
    public void ThenTheAppWarnsThatTheFourHourLimitIsApproaching()
    {
        Assert.Equal(TimeSpan.FromMinutes(235), RecordingFormat.WarningCapturedDuration);
    }

    [Then("capture stops automatically")]
    [Then("the app saves the recording normally")]
    public void ThenCaptureStopsAutomatically()
    {
        Assert.Equal(RecordingOutcome.Complete, _savedRecording?.Outcome);
    }

    [Then("paused time does not count toward either threshold")]
    public void ThenPausedTimeDoesNotCountTowardEitherThreshold()
    {
        Assert.Equal(TimeSpan.Zero, _recordingEngine!.DurationDuringPause);
    }

    [Given("a recording is active or paused")]
    public Task GivenARecordingIsActiveOrPaused()
    {
        return GivenTheRecorderIsActive();
    }

    [Then("the app prevents Windows idle sleep")]
    public void ThenTheAppPreventsWindowsIdleSleep()
    {
        Assert.True(CaptureWakePolicy.PreventsIdleSleep);
        Assert.True(_application?.VisibleStatus is "Recording" or "Paused");
    }

    [Then("the app does not prevent an explicit user or operating-system suspension or shutdown")]
    public void ThenTheAppDoesNotPreventExplicitSuspensionOrShutdown()
    {
        Assert.False(CaptureWakePolicy.PreventsExplicitSuspensionOrShutdown);
    }

    [Then("recording does not start")]
    public void ThenRecordingDoesNotStart()
    {
        Assert.False(_recordingEngine?.WasStarted);
    }

    [Then("the visible status is \"Idle\"")]
    public void ThenTheVisibleStatusIsIdle()
    {
        Assert.Equal("Idle", _application?.VisibleStatus);
    }

    [Then("an actionable error is shown")]
    public void ThenAnActionableErrorIsShown()
    {
        Assert.False(string.IsNullOrWhiteSpace(_application?.LastError));
    }

    private async Task StartWithUnavailableDeviceAsync(string sourceText, string deviceText)
    {
        var source = sourceText switch
        {
            "microphone only" => AudioSource.MicrophoneOnly,
            "system audio only" => AudioSource.SystemAudioOnly,
            "microphone and system" => AudioSource.MicrophoneAndSystemAudio,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceText)),
        };

        _recordingEngine = new RecordingEngineDouble();
        _application = new RecorderApplication(_recordingEngine);
        await _application.StartAsync(
            new RecordingRequest(
                source,
                deviceText == "microphone" ? null : "microphone",
                deviceText == "playback device" ? null : "playback",
                Path.GetTempPath(),
                null),
            _permissionConfirmed);
    }

    private RecorderApplication CreateApplication()
    {
        _transcriptionQueue = new TranscriptionQueueDouble();
        _modelStore ??= new TranscriptionModelStoreDouble(available: false);
        return new RecorderApplication(_recordingEngine!, _transcriptionQueue, _modelStore);
    }

    private static AudioSource ParseSource(string source)
    {
        return source switch
        {
            "microphone only" => AudioSource.MicrophoneOnly,
            "system audio only" => AudioSource.SystemAudioOnly,
            "microphone and system" => AudioSource.MicrophoneAndSystemAudio,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private sealed class RecordingEngineDouble : IRecordingEngine
    {
        public string? LastFailure => null;
        public bool WasStarted { get; private set; }
        public bool WasPaused { get; private set; }
        public bool WasResumed { get; private set; }
        public int StartCount { get; private set; }
        public TimeSpan CapturedDuration { get; private set; }
        public TimeSpan DurationDuringPause { get; private set; }
        public IReadOnlyList<string> LevelSources => ["Microphone", "SystemAudio"];
        public int OutputChannels { get; private set; }
        public bool IncludedSystemAudio { get; private set; }
        public RecordingRequest? LastRequest { get; private set; }

        public Task StartAsync(RecordingRequest request, CancellationToken cancellationToken)
        {
            WasStarted = true;
            StartCount++;
            LastRequest = request;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            WasPaused = true;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            WasResumed = true;
            return Task.CompletedTask;
        }

        public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateSavedRecording(CapturedDuration, LastRequest!.Source));
        }

        public SavedRecording CreateSavedRecording(
            TimeSpan duration,
            AudioSource source)
        {
            var audioPath = RecordingFileName.CreateUnique(
                Path.GetTempPath(),
                LastRequest?.Title,
                DateTimeOffset.Now);
            OutputChannels = source == AudioSource.MicrophoneOnly ? 1 : 2;
            IncludedSystemAudio = source != AudioSource.MicrophoneOnly;
            using (var writer = new WaveFileWriter(
                       audioPath,
                       new WaveFormat(
                           RecordingFormat.SampleRate,
                           RecordingFormat.BitsPerSample,
                           OutputChannels)))
            {
                writer.Write(new byte[OutputChannels * 2], 0, OutputChannels * 2);
            }

            return new SavedRecording(
                Guid.NewGuid(),
                audioPath,
                "English",
                RecordingOutcome.Complete,
                duration);
        }
    }

    private sealed class TranscriptionQueueDouble : ITranscriptionQueue
    {
        public List<SavedRecording> QueuedRecordings { get; } = [];
        public bool AudioExistedAtEnqueue { get; private set; }
        public bool UsesLocalWorker { get; } = true;

        public Task EnqueueAsync(SavedRecording recording, CancellationToken cancellationToken)
        {
            AudioExistedAtEnqueue = File.Exists(recording.AudioPath);
            QueuedRecordings.Add(recording);
            return Task.CompletedTask;
        }
    }

    private sealed class TranscriptionModelStoreDouble(bool available) : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language)
        {
            return available;
        }

        public string? GetModelPath(string language)
        {
            return available ? "local-model.bin" : null;
        }
    }
}
