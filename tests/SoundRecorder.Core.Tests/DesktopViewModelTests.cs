using Microsoft.Extensions.Logging;
using System.Collections.Specialized;
using SoundRecorder.Core;
using SoundRecorder.Desktop;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Models;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class DesktopViewModelTests
{
    [Fact]
    public void NewRecording_InitialChoices_RequireFreshPermissionWithoutStartingCapture()
    {
        using var context = new DesktopTestContext();
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue,
            context.Models, context.History, new RecoveryManager(context.Recovery), context, context.Logger);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Sound Recorder"),
            viewModel.DestinationFolder);
        Assert.Equal(new[]
        {
            new AudioSourceOption(AudioSource.MicrophoneOnly, "Microphone only"),
            new AudioSourceOption(AudioSource.SystemAudioOnly, "System audio only"),
            new AudioSourceOption(AudioSource.MicrophoneAndSystemAudio, "Microphone and system audio"),
        }, viewModel.AudioSources);
        Assert.Equal(["English", "Brazilian Portuguese"], viewModel.Languages);
        Assert.Equal(AudioSource.MicrophoneOnly, viewModel.AudioSource);
        Assert.Equal("English", viewModel.Language);
        Assert.Equal("Idle", viewModel.Status);
        Assert.Equal("00:00:00", viewModel.CapturedDuration);
        Assert.False(viewModel.PermissionConfirmed);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.Equal(string.Empty, viewModel.ModelUrl);
        Assert.Equal(string.Empty, viewModel.Message);
        Assert.Empty(viewModel.Microphones);
        Assert.Empty(viewModel.PlaybackDevices);
        Assert.Empty(viewModel.Recordings);
        Assert.Empty(viewModel.Jobs);
        Assert.Null(context.Engine.Request);
    }

    [Fact]
    public void BindingChanges_PermissionBusySourceAndRefresh_NotifyAffectedCommandsAndProperties()
    {
        using var context = new DesktopTestContext();
        var viewModel = context.ViewModel;
        var commands = new[] { viewModel.StartCommand, viewModel.PauseCommand,
            viewModel.ResumeCommand, viewModel.StopCommand };
        var changes = new int[commands.Length];
        for (var index = 0; index < commands.Length; index++)
        {
            var captured = index;
            commands[index].CanExecuteChanged += (_, _) => changes[captured]++;
        }
        viewModel.PermissionConfirmed = true;
        Assert.Equal([1, 0, 0, 0], changes);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        viewModel.IsBusy = true;
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.Equal([2, 1, 1, 1], changes);
        viewModel.IsBusy = false;
        Assert.Equal([3, 2, 2, 2], changes);
        var properties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        viewModel.AudioSource = AudioSource.SystemAudioOnly;
        Assert.Contains(nameof(MainViewModel.UsesMicrophone), properties);
        Assert.Contains(nameof(MainViewModel.UsesPlayback), properties);
        Assert.Contains(nameof(MainViewModel.SystemAudioExplanation), properties);
        properties.Clear();
        viewModel.Refresh();
        Assert.Equal([4, 3, 3, 3], changes);
        Assert.Contains(nameof(MainViewModel.IsIdle), properties);
        Assert.Contains(nameof(MainViewModel.UsesMicrophone), properties);
        Assert.Contains(nameof(MainViewModel.UsesPlayback), properties);
        Assert.Contains(nameof(MainViewModel.SystemAudioExplanation), properties);
    }

    [Theory]
    [InlineData("OneDrive")]
    [InlineData("OneDriveConsumer")]
    [InlineData("OneDriveCommercial")]
    [InlineData("Dropbox")]
    public async Task StartRecordingAsync_KnownSynchronizedDestination_WarnsWithoutClaimingControl(string rootName)
    {
        using var context = new DesktopTestContext();
        var previous = Environment.GetEnvironmentVariable(rootName);
        try
        {
            Environment.SetEnvironmentVariable(rootName, context.Directory);
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            Assert.Equal(PrivacyPolicy.CloudSynchronizationWarning + " Recording started.",
                context.ViewModel.Message);
            Assert.False(context.ViewModel.NeedsShortRecordingConsent);
            await context.ViewModel.StopAsync(false);
            context.ViewModel.PermissionConfirmed = false;
            await context.ViewModel.StartRecordingAsync(false);
            Assert.Equal("Confirm that you have permission to record before starting.", context.ViewModel.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(rootName, previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task StartRecordingAsync_EmptyDestination_ReportsPrerequisiteWithoutPathNormalization(string? folder)
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        context.ViewModel.DestinationFolder = folder!;
        await context.ViewModel.StartRecordingAsync(false);
        Assert.Equal("Select a writable local destination folder before starting.", context.ViewModel.Message);
        Assert.False(context.ViewModel.NeedsShortRecordingConsent);
        Assert.True(context.ViewModel.IsIdle);
        Assert.Null(context.Engine.Request);
    }

    [Fact]
    public async Task InitializeCommand_InFlight_BusyUntilAllLocalStateHasLoaded()
    {
        using var context = new DesktopTestContext();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Enumerate = () => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); };
        var loading = context.ViewModel.InitializeCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(context.ViewModel.IsBusy);
        }
        finally
        {
            release.Set();
        }
        await loading;
        Assert.False(context.ViewModel.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelInstallation_InFlight_BusyUntilExplicitOperationCompletes(bool download)
    {
        using var context = new DesktopTestContext();
        context.Models.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.ViewModel.ModelUrl = "https://models.test/package.zip";
        var installing = download ? context.ViewModel.DownloadModelCommand.ExecuteAsync(null) :
            context.ViewModel.ImportModelCommand.ExecuteAsync("selected.zip");
        try
        {
            await context.Models.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(context.ViewModel.IsBusy);
            Assert.StartsWith("No compatible model", context.ViewModel.ModelStatus);
        }
        finally
        {
            context.Models.Gate.SetResult();
        }
        await installing;
        Assert.False(context.ViewModel.IsBusy);
        Assert.StartsWith("Active model", context.ViewModel.ModelStatus);
    }

    [Fact]
    public async Task StopAsync_InFlightHistoryRefresh_RemainsBusyAndDisablesCaptureCommands()
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        context.History.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.ViewModel.StartRecordingAsync(false);
        var stopping = context.ViewModel.StopAsync(false);
        try
        {
            await context.History.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(context.ViewModel.IsBusy);
            Assert.False(context.ViewModel.StartCommand.CanExecute(null));
        }
        finally
        {
            context.History.LoadGate.SetResult();
        }
        await stopping;
        Assert.False(context.ViewModel.IsBusy);
    }

    [Fact]
    public async Task CompletedRecording_HistoryRefresh_PreservesNewestFirstOrderAndClearsUnmatchedJobSelection()
    {
        using var context = new DesktopTestContext();
        var old = context.Recording(Guid.Empty);
        var latest = context.Recording(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        context.History.Items.Add(old);
        await context.ViewModel.AcceptCompletedRecordingAsync(latest);
        Assert.Equal([latest, old], context.ViewModel.Recordings);
        context.ViewModel.SelectedJob = context.Job(old);
        context.ViewModel.Refresh();
        Assert.Null(context.ViewModel.SelectedJob);
        Assert.Empty(context.ViewModel.Jobs);
    }

    [Fact]
    public async Task InitializeCommand_LocalState_RestoresSelectionsHistoryJobsAndModel()
    {
        using var context = new DesktopTestContext();
        var first = context.Recording(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var last = context.Recording(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        context.History.Items.AddRange([first, last]);
        var job = context.Job(first);
        context.Repository.Items.Add(job);
        context.Models.Bundled = context.Models.Package;
        var viewModel = context.ViewModel;

        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.Equal("microphone", viewModel.SelectedMicrophoneId);
        Assert.Equal("playback", viewModel.SelectedPlaybackDeviceId);
        Assert.Equal("Microphone", Assert.Single(viewModel.Microphones).Name);
        Assert.Equal("Playback", Assert.Single(viewModel.PlaybackDevices).Name);
        Assert.Equal([last, first], viewModel.Recordings);
        Assert.Same(job, Assert.Single(viewModel.Jobs));
        Assert.Equal("Bundled model activated: local-model", viewModel.Message);
        Assert.Equal("Active model: local-model (English, Brazilian Portuguese)", viewModel.ModelStatus);
        Assert.EndsWith(Path.Combine("models"), context.Models.BundledDirectory);
        Assert.Equal("Idle", viewModel.Status);
        Assert.Null(context.Engine.Request);
        Assert.Empty(context.Models.Downloads);
    }

    [Fact]
    public async Task InitializeCommand_EmptyDevicesAndNoBundle_KeepsIdleWithoutSelections()
    {
        using var context = new DesktopTestContext();
        context.Microphones = [];
        context.Playback = [];
        await context.ViewModel.InitializeCommand.ExecuteAsync(null);
        Assert.Null(context.ViewModel.SelectedMicrophoneId);
        Assert.Null(context.ViewModel.SelectedPlaybackDeviceId);
        Assert.Empty(context.ViewModel.Recordings);
        Assert.Equal(string.Empty, context.ViewModel.Message);
        Assert.Equal("No compatible model is installed. Recordings remain available for later transcription.",
            context.ViewModel.ModelStatus);
    }

    [Fact]
    public async Task InitializeCommand_DeviceFailure_ReportsAndLogsWithoutRemainingBusy()
    {
        using var context = new DesktopTestContext();
        context.DeviceError = new IOException("enumeration failed");
        await context.ViewModel.InitializeCommand.ExecuteAsync(null);
        Assert.False(context.ViewModel.IsBusy);
        Assert.StartsWith("Some local state could not be loaded. See ", context.ViewModel.Message);
        Assert.Same(context.DeviceError, Assert.Single(context.Logger.Errors));
        Assert.Equal("Main view initialization failed", context.Logger.Entries.Single().Message);
        Assert.Null(context.Models.BundledDirectory);
    }

    [Fact]
    public async Task StartPauseResumeStop_Commands_PreserveRequestAndPermissionBoundaries()
    {
        using var context = new DesktopTestContext();
        var viewModel = context.ViewModel;
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.IsIdle);
        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
        viewModel.PermissionConfirmed = true;
        viewModel.SelectedMicrophoneId = "selected-mic";
        viewModel.SelectedPlaybackDeviceId = "selected-playback";
        viewModel.Title = "Meeting";
        viewModel.AudioSource = AudioSource.MicrophoneAndSystemAudio;
        viewModel.Language = "Brazilian Portuguese";
        Assert.True(viewModel.StartCommand.CanExecute(null));
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(new RecordingRequest(
            AudioSource.MicrophoneAndSystemAudio, "selected-mic", "selected-playback",
            context.Directory, "Meeting", "Brazilian Portuguese"), context.Engine.Request);
        Assert.Equal("Recording started.", viewModel.Message);
        Assert.Equal("Recording", viewModel.Status);
        Assert.False(viewModel.IsIdle);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.True(viewModel.PauseCommand.CanExecute(null));
        Assert.True(viewModel.StopCommand.CanExecute(null));
        await viewModel.PauseCommand.ExecuteAsync(null);
        Assert.Equal("Paused", viewModel.Status);
        Assert.Equal("Recording paused. Paused time is excluded from the saved audio.", viewModel.Message);
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.Equal(1, context.Engine.Pauses);
        await viewModel.ResumeCommand.ExecuteAsync(null);
        Assert.Equal("Recording", viewModel.Status);
        Assert.Equal("Recording resumed.", viewModel.Message);
        Assert.Equal(1, context.Engine.Resumes);
        await viewModel.StopCommand.ExecuteAsync(null);
        Assert.Equal("Idle", viewModel.Status);
        Assert.False(viewModel.PermissionConfirmed);
        Assert.Same(context.Engine.Result, Assert.Single(viewModel.Recordings));
        Assert.Equal($"Recording saved to {context.Engine.Result.AudioPath}. " +
            "Audio was saved. Install a compatible transcription model to transcribe this recording.",
            viewModel.Message);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData(true, "Recording")]
    [InlineData(false, "Paused")]
    public void Commands_Busy_DisableCaptureCommands(bool busy, string status)
    {
        using var context = new DesktopTestContext();
        var viewModel = context.ViewModel;
        viewModel.PermissionConfirmed = true;
        viewModel.Status = status;
        viewModel.IsBusy = busy;
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.PauseCommand.CanExecute(null));
        Assert.Equal(!busy, viewModel.ResumeCommand.CanExecute(null));
        Assert.Equal(!busy, viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartRecordingAsync_InFlight_StaysBusyUntilEngineCompletes()
    {
        using var context = new DesktopTestContext();
        context.Engine.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.PrepareRecording();
        var start = context.ViewModel.StartRecordingAsync(true);
        await context.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(context.ViewModel.IsBusy);
        Assert.False(context.ViewModel.StartCommand.CanExecute(null));
        context.Engine.StartGate.SetResult();
        await start;
        Assert.True(context.Engine.Request!.AllowShortRecordingWhenStorageIsLow);
        Assert.Null(context.Engine.Request.Title);
        Assert.False(context.ViewModel.IsBusy);
    }

    [Fact]
    public async Task StartRecordingAsync_LowSpace_RequiresExplicitShortRecordingConsent()
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        context.Engine.StartError = new RecordingStartException("Insufficient space for a FOUR-HOUR RECORDING.");
        await context.ViewModel.StartCommand.ExecuteAsync(null);
        Assert.True(context.ViewModel.NeedsShortRecordingConsent);
        Assert.Equal(context.Engine.StartError.Message, context.ViewModel.Message);
        Assert.False(context.Engine.Request!.AllowShortRecordingWhenStorageIsLow);
        Assert.Equal("Idle", context.ViewModel.Status);
        Assert.False(context.ViewModel.IsBusy);
    }

    [Fact]
    public async Task StartRecordingAsync_UnexpectedFailure_ResetsBusyAndPropagates()
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        context.Engine.StartError = new IOException("device failed");
        var exception = await Assert.ThrowsAsync<IOException>(() => context.ViewModel.StartRecordingAsync(false));
        Assert.Same(context.Engine.StartError, exception);
        Assert.False(context.ViewModel.IsBusy);
        Assert.True(context.ViewModel.IsIdle);
    }

    [Fact]
    public async Task StopAsync_InterruptedAndFailedFinalization_ReportHonestOutcomes()
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        context.Engine.Result = context.Engine.Result with { Outcome = RecordingOutcome.Interrupted };
        var saved = await context.ViewModel.StopAsync(false);
        Assert.Same(context.Engine.Result, saved);
        Assert.Equal($"Recording was interrupted. Available audio was saved to {saved!.AudioPath}.",
            context.ViewModel.Message);
        Assert.Empty(context.Queue.Jobs);
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        context.Engine.StopError = new IOException("disk failed");
        Assert.Null(await context.ViewModel.StopAsync(false));
        Assert.Equal("The recording could not be finalized. Available partial audio remains for recovery.",
            context.ViewModel.Message);
        Assert.True(context.ViewModel.PermissionConfirmed);
        Assert.False(context.ViewModel.IsBusy);
        Assert.Equal("Idle", context.ViewModel.Status);
    }

    [Fact]
    public async Task StopAsync_Idle_PreservesExistingMessage()
    {
        using var context = new DesktopTestContext();
        context.ViewModel.Message = "Previous notice";
        Assert.Null(await context.ViewModel.StopAsync(false));
        Assert.Equal("Previous notice", context.ViewModel.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ImportModelCommand_NoSelection_DoesNotInstall(string? path)
    {
        using var context = new DesktopTestContext();
        await context.ViewModel.ImportModelCommand.ExecuteAsync(path);
        Assert.Empty(context.Models.Imports);
        Assert.False(context.ViewModel.IsBusy);
        Assert.Equal(string.Empty, context.ViewModel.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportModelCommand_SelectedPackage_ReportsActivationOrFailure(bool fails)
    {
        using var context = new DesktopTestContext();
        context.Models.Error = fails ? new IOException("bad package") : null;
        await context.ViewModel.ImportModelCommand.ExecuteAsync("chosen.zip");
        Assert.Equal("chosen.zip", Assert.Single(context.Models.Imports));
        Assert.Equal(fails ? "The model package was not activated: bad package" :
            "The model package was verified and activated locally.", context.ViewModel.Message);
        Assert.Equal(fails ? "No compatible model is installed. Recordings remain available for later transcription." :
            "Active model: local-model (English, Brazilian Portuguese)", context.ViewModel.ModelStatus);
        Assert.False(context.ViewModel.IsBusy);
    }

    [Theory]
    [InlineData("relative.zip")]
    [InlineData("file:///C:/model.zip")]
    [InlineData("ftp://models.test/model.zip")]
    [InlineData("")]
    public async Task DownloadModelCommand_InvalidUrl_DoesNotContactNetwork(string url)
    {
        using var context = new DesktopTestContext();
        context.ViewModel.ModelUrl = url;
        await context.ViewModel.DownloadModelCommand.ExecuteAsync(null);
        Assert.Empty(context.Models.Downloads);
        Assert.Equal("Enter an HTTP or HTTPS URL for a model package.", context.ViewModel.Message);
        Assert.False(context.ViewModel.IsBusy);
    }

    [Theory]
    [InlineData("http://models.test/package.zip", false)]
    [InlineData("https://models.test/package.zip", false)]
    [InlineData("https://models.test/package.zip", true)]
    public async Task DownloadModelCommand_ExplicitUrl_ReportsActivationOrFailure(string url, bool fails)
    {
        using var context = new DesktopTestContext();
        context.Models.Error = fails ? new IOException("offline") : null;
        context.ViewModel.ModelUrl = url;
        await context.ViewModel.DownloadModelCommand.ExecuteAsync(null);
        Assert.Equal(new Uri(url), Assert.Single(context.Models.Downloads));
        Assert.Equal(fails ? "The model download or update failed: offline" :
            "The model package was downloaded, verified, and activated locally.", context.ViewModel.Message);
        Assert.False(context.ViewModel.IsBusy);
        if (!fails)
        {
            Assert.StartsWith("Active model: local-model", context.ViewModel.ModelStatus);
        }
    }

    [Fact]
    public async Task JobCommands_NoSelection_ReportActionableMessages()
    {
        using var context = new DesktopTestContext();
        await context.ViewModel.CancelJobCommand.ExecuteAsync(null);
        Assert.Equal("Select a transcription job to cancel.", context.ViewModel.Message);
        await context.ViewModel.RetryTranscriptionCommand.ExecuteAsync(null);
        Assert.Equal("Select a recording or job to transcribe again.", context.ViewModel.Message);
        await context.ViewModel.ViewTranscriptCommand.ExecuteAsync(null);
        Assert.Equal("Select a successful transcription job.", context.ViewModel.Message);
        Assert.Empty(context.Queue.Jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryTranscriptionCommand_Selection_UsesJobBeforeRecordingAndChosenLanguage(bool selectJob)
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.NewGuid());
        var job = context.Job(context.Recording(Guid.NewGuid()));
        context.ViewModel.SelectedRecording = recording;
        context.ViewModel.SelectedJob = selectJob ? job : null;
        context.ViewModel.Language = "Brazilian Portuguese";
        await context.ViewModel.RetryTranscriptionCommand.ExecuteAsync(null);
        await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        var retried = Assert.Single(context.Queue.Jobs);
        Assert.Equal((selectJob ? job.Recording : recording) with { Language = "Brazilian Portuguese" },
            retried.Recording);
        Assert.Same(retried, Assert.Single(context.ViewModel.Jobs));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("missing", false)]
    [InlineData("Speech remains local", true)]
    [InlineData("  \r\n", true)]
    public async Task ViewTranscriptCommand_Outputs_ExplainsMissingAndEmptyFiles(string? content, bool exists)
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "transcript.txt");
        if (exists)
        {
            await File.WriteAllTextAsync(path, content);
        }
        var job = context.Job(context.Recording(Guid.NewGuid()), content is null ? null :
            new TranscriptOutputs(path, "unused.srt"));
        context.ViewModel.SelectedJob = job;
        await context.ViewModel.ViewTranscriptCommand.ExecuteAsync(null);
        if (!exists)
        {
            Assert.Null(context.ViewModel.Transcript);
            Assert.Equal("Select a successful transcription job.", context.ViewModel.Message);
            return;
        }
        Assert.Equal(string.IsNullOrWhiteSpace(content) ?
            "No speech was recognized in this recording." : content, context.ViewModel.Transcript);
    }

    [Theory]
    [InlineData(RecordingOutcome.Complete)]
    [InlineData(RecordingOutcome.Interrupted)]
    public async Task AcceptCompletedRecordingAsync_Completion_RefreshesHistoryAndVisibleOutcome(RecordingOutcome outcome)
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        var recording = context.Engine.Result with { Outcome = outcome };
        await context.ViewModel.AcceptCompletedRecordingAsync(recording);
        Assert.Same(recording, Assert.Single(context.ViewModel.Recordings));
        Assert.False(context.ViewModel.PermissionConfirmed);
        Assert.Equal("Idle", context.ViewModel.Status);
        Assert.Equal(outcome == RecordingOutcome.Complete ?
            $"Recording saved at the four-hour limit: {recording.AudioPath}" :
            $"Recording was interrupted. Available audio was saved to {recording.AudioPath}. " +
            "Recording was interrupted. The available captured audio was preserved.", context.ViewModel.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverAsync_Candidate_PersistsOnlyRecoveredAudioAndReportsFailure(bool fails)
    {
        using var context = new DesktopTestContext();
        context.Recovery.Error = fails ? new IOException("lost disk") : null;
        var candidates = await context.ViewModel.FindRecoveryCandidatesAsync();
        Assert.Same(context.Recovery.Candidate, Assert.Single(candidates));
        await context.ViewModel.RecoverAsync(candidates[0]);
        Assert.Same(candidates[0], context.Recovery.Attempted);
        Assert.Empty(context.Queue.Jobs);
        if (fails)
        {
            Assert.Empty(context.ViewModel.Recordings);
            Assert.Equal("The captured audio could not be recovered: lost disk", context.ViewModel.Message);
        }
        else
        {
            Assert.Same(context.Recovery.Result, Assert.Single(context.ViewModel.Recordings));
            Assert.Equal("Recovered audio was saved as a recovered recording. " +
                "Transcription requires an explicit request: " + context.Recovery.Result.AudioPath,
                context.ViewModel.Message);
        }
    }

    [Theory]
    [InlineData(AudioSource.MicrophoneOnly, true, false)]
    [InlineData(AudioSource.SystemAudioOnly, false, true)]
    [InlineData(AudioSource.MicrophoneAndSystemAudio, true, true)]
    public void Refresh_SourceAndLevels_ExposeSavedAudioTimelineAndSourceExplanation(
        AudioSource source, bool microphone, bool playback)
    {
        using var context = new DesktopTestContext();
        var changed = new List<string?>();
        context.ViewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        context.ViewModel.AudioSource = source;
        context.Engine.CapturedDuration = TimeSpan.FromSeconds(3723.5);
        context.Engine.MicrophoneLevel = .25f;
        context.Engine.SystemAudioLevel = .75f;
        context.ViewModel.Refresh();
        Assert.Equal("01:02:03", context.ViewModel.CapturedDuration);
        Assert.Equal(.25, context.ViewModel.MicrophoneLevel);
        Assert.Equal(.75, context.ViewModel.SystemAudioLevel);
        Assert.Equal(microphone, context.ViewModel.UsesMicrophone);
        Assert.Equal(playback, context.ViewModel.UsesPlayback);
        Assert.Equal(playback ? $"{ProductCopy.SystemAudioScope} {ProductCopy.HeadphoneRecommendation} " +
            ProductCopy.EchoCancellationLimitation : "Microphone input is saved as mono.",
            context.ViewModel.SystemAudioExplanation);
        Assert.Contains(nameof(MainViewModel.IsIdle), changed);
        Assert.Contains(nameof(MainViewModel.UsesMicrophone), changed);
        Assert.Contains(nameof(MainViewModel.UsesPlayback), changed);
        Assert.Contains(nameof(MainViewModel.SystemAudioExplanation), changed);
    }

    [Fact]
    public async Task StopActiveWorkAsync_Recording_DoesNotAutomaticallyQueueTranscription()
    {
        using var context = new DesktopTestContext();
        context.Models.CurrentPackage = context.Models.Package;
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        Assert.True(context.ViewModel.HasActiveWork());
        await context.ViewModel.StopActiveWorkAsync();
        Assert.Empty(context.Queue.Jobs);
        Assert.Same(context.Engine.Result, Assert.Single(context.ViewModel.Recordings));
        Assert.False(context.ViewModel.HasActiveWork());
    }

    [Fact]
    public async Task StopCommand_CompatibleModel_AutomaticallyQueuesNormallySavedAudio()
    {
        using var context = new DesktopTestContext();
        context.Models.CurrentPackage = context.Models.Package;
        context.PrepareRecording();
        await context.ViewModel.StartCommand.ExecuteAsync(null);
        await context.ViewModel.StopCommand.ExecuteAsync(null);
        await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(context.Engine.Result, Assert.Single(context.Queue.Jobs).Recording);
        Assert.Equal("Idle", context.ViewModel.Status);
    }

    [Fact]
    public async Task CancelJobCommand_RunningJob_CancelsSelectedWorkAndRefreshesStatus()
    {
        using var context = new DesktopTestContext();
        context.Transcriber.Block = true;
        await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
        await context.Transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        context.ViewModel.Refresh();
        context.ViewModel.SelectedJob = Assert.Single(context.ViewModel.Jobs);
        var refreshes = 0;
        context.ViewModel.Jobs.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) refreshes++;
        };
        Assert.True(context.ViewModel.HasActiveWork());
        await context.ViewModel.CancelJobCommand.ExecuteAsync(null);
        await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, refreshes);
        Assert.Equal(TranscriptionJobStatus.Canceled, context.ViewModel.SelectedJob.Status);
        Assert.False(context.ViewModel.HasActiveWork());
    }

    [Fact]
    public async Task StopActiveWorkAsync_RunningJob_CancelsWithoutStartingNewWork()
    {
        using var context = new DesktopTestContext();
        context.Transcriber.Block = true;
        await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
        await context.Transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        context.ViewModel.Refresh();
        var refreshes = 0;
        context.ViewModel.Jobs.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) refreshes++;
        };
        await context.ViewModel.StopActiveWorkAsync();
        await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, refreshes);
        Assert.Equal(TranscriptionJobStatus.Canceled, Assert.Single(context.ViewModel.Jobs).Status);
    }

    [Fact]
    public async Task StartRecordingAsync_WhitespaceTitle_DoesNotSaveWhitespaceAsAName()
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        context.ViewModel.Title = "  ";
        await context.ViewModel.StartRecordingAsync(false);
        Assert.Null(context.Engine.Request!.Title);
        await context.ViewModel.StopAsync(false);
    }
}

internal sealed class DesktopTestContext : IDisposable, IAudioDeviceCatalog
{
    public string Directory { get; } = Path.Combine(
        Environment.CurrentDirectory, "artifacts", "desktop-tests", Guid.NewGuid().ToString("N"));
    public DesktopEngine Engine { get; } = new();
    public DesktopModels Models { get; } = new();
    public DesktopHistory History { get; } = new();
    public DesktopRecovery Recovery { get; } = new();
    public DesktopRepository Repository { get; } = new();
    public DesktopTranscriber Transcriber { get; } = new();
    public DesktopLogger<MainViewModel> Logger { get; } = new();
    public IReadOnlyList<AudioDeviceDescriptor> Microphones { get; set; } = [new("microphone", "Microphone")];
    public IReadOnlyList<AudioDeviceDescriptor> Playback { get; set; } = [new("playback", "Playback")];
    public Exception? DeviceError { get; set; }
    public Action? Enumerate { get; set; }
    public TranscriptionQueue Queue { get; }
    public RecorderApplication Recorder { get; }
    public MainViewModel ViewModel { get; }

    public DesktopTestContext()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Engine.Result = Recording(Guid.NewGuid());
        Recovery.Result = Engine.Result with { Outcome = RecordingOutcome.Recovered };
        Queue = new(Transcriber, new DesktopWriter(), Repository);
        Recorder = new(Engine, Queue, Models, History);
        ViewModel = new(Recorder, Engine, Queue, Models, History,
            new RecoveryManager(Recovery), this, Logger);
        ViewModel.DestinationFolder = Directory;
    }

    public SavedRecording Recording(Guid id) =>
        new(id, Path.Combine(Directory, id + ".wav"), "English",
            RecordingOutcome.Complete, TimeSpan.FromSeconds(5));

    public TranscriptionJob Job(SavedRecording recording, TranscriptOutputs? outputs = null) =>
        new(Guid.NewGuid(), recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded, outputs, null);

    public void PrepareRecording()
    {
        ViewModel.PermissionConfirmed = true;
        ViewModel.SelectedMicrophoneId = "microphone";
    }

    public void Dispose()
    {
        Transcriber.Release.TrySetResult();
        Transcriber.CancellationGate?.TrySetResult();
        System.IO.Directory.Delete(Directory, true);
    }

    public IReadOnlyList<AudioDeviceDescriptor> GetMicrophones()
    {
        Enumerate?.Invoke();
        return DeviceError is null ? Microphones : throw DeviceError;
    }

    public IReadOnlyList<AudioDeviceDescriptor> GetPlaybackDevices() => Playback;
}

internal sealed class DesktopEngine : IRecordingEngine, IRecordingMonitor
{
    public string? LastFailure => null;
    public TimeSpan CapturedDuration { get; set; }
    public float MicrophoneLevel { get; set; }
    public float SystemAudioLevel { get; set; }
    public RecordingRequest? Request { get; private set; }
    public SavedRecording Result { get; set; } = null!;
    public Exception? StartError { get; set; }
    public Exception? StopError { get; set; }
    public int Pauses { get; private set; }
    public int Resumes { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? StartGate { get; set; }
    public async Task StartAsync(RecordingRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        Started.TrySetResult();
        if (StartGate is not null) await StartGate.Task;
        if (StartError is not null) throw StartError;
    }
    public Task PauseAsync(CancellationToken cancellationToken) { Pauses++; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken cancellationToken) { Resumes++; return Task.CompletedTask; }
    public Task<SavedRecording> StopAsync(CancellationToken cancellationToken) =>
        StopError is null ? Task.FromResult(Result) : Task.FromException<SavedRecording>(StopError);
}

internal sealed class DesktopModels : ILocalTranscriptionModelManager
{
    public TranscriptionModelPackage Package { get; } = new(
        1, "local-model", "whisper", "model.bin", "hash", "win-x64", ["English", "Brazilian Portuguese"]);
    public TranscriptionModelPackage? CurrentPackage { get; set; }
    public TranscriptionModelPackage? Bundled { get; set; }
    public string? BundledDirectory { get; private set; }
    public Exception? Error { get; set; }
    public List<string> Imports { get; } = [];
    public List<Uri> Downloads { get; } = [];
    public TaskCompletionSource? Gate { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HasCompatibleModel(string language) => CurrentPackage is not null;
    public string? GetModelPath(string language) => CurrentPackage?.ModelFile;
    public Task<TranscriptionModelPackage> ImportAsync(string packagePath, CancellationToken cancellationToken)
    {
        Imports.Add(packagePath);
        return Activate();
    }
    public Task<TranscriptionModelPackage?> ImportBundledAsync(string path, CancellationToken cancellationToken)
    {
        BundledDirectory = path;
        CurrentPackage = Bundled;
        return Task.FromResult(Bundled);
    }
    public Task<TranscriptionModelPackage> DownloadAndImportAsync(Uri packageUri, CancellationToken cancellationToken)
    {
        Downloads.Add(packageUri);
        return Activate();
    }
    private async Task<TranscriptionModelPackage> Activate()
    {
        Started.TrySetResult();
        if (Gate is not null) await Gate.Task;
        if (Error is not null) throw Error;
        CurrentPackage = Package;
        return Package;
    }
}

internal sealed class DesktopHistory : IRecordingHistory
{
    public List<SavedRecording> Items { get; } = [];
    public TaskCompletionSource? LoadGate { get; set; }
    public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? LoadError { get; set; }
    public async Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken cancellationToken)
    {
        LoadStarted.TrySetResult();
        if (LoadGate is not null) await LoadGate.Task;
        if (LoadError is not null) throw LoadError;
        return Items.ToArray();
    }
    public Task AddAsync(SavedRecording recording, CancellationToken cancellationToken)
    {
        Items.Add(recording);
        return Task.CompletedTask;
    }
}

internal sealed class DesktopRecovery : IRecoveryStorage
{
    public RecoveryCandidate Candidate { get; } = new(
        Guid.NewGuid(), "partial.wav", "English", TimeSpan.FromSeconds(5), DateTimeOffset.UnixEpoch);
    public RecoveryCandidate? Attempted { get; private set; }
    public SavedRecording Result { get; set; } = null!;
    public Exception? Error { get; set; }
    public Exception? FindError { get; set; }
    public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(CancellationToken cancellationToken) =>
        FindError is null ? Task.FromResult<IReadOnlyList<RecoveryCandidate>>([Candidate]) :
            Task.FromException<IReadOnlyList<RecoveryCandidate>>(FindError);
    public Task<SavedRecording> RecoverAsync(RecoveryCandidate candidate, CancellationToken cancellationToken)
    {
        Attempted = candidate;
        return Error is null ? Task.FromResult(Result) : Task.FromException<SavedRecording>(Error);
    }
    public Task DiscardAsync(RecoveryCandidate candidate, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class DesktopRepository : ITranscriptionJobRepository
{
    public List<TranscriptionJob> Items { get; } = [];
    public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptionJob>>(Items.ToArray());
    public Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class DesktopTranscriber : ILocalTranscriber
{
    public bool Block { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? CancellationGate { get; set; }
    public async Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        if (Block)
        {
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationRequested.TrySetResult();
                if (CancellationGate is not null) await CancellationGate.Task;
                throw;
            }
        }
        return new(recording, recording.Language, []);
    }
}

internal sealed class DesktopWriter : ITranscriptWriter
{
    public Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken cancellationToken) =>
        Task.FromResult(new TranscriptOutputs("local.txt", "local.srt"));
}

internal sealed class DesktopLogger<T> : ILogger<T>
{
    public List<Exception?> Errors { get; } = [];
    public List<(LogLevel Level, int Id, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
        if (logLevel == LogLevel.Error) Errors.Add(exception);
    }

    [CollectionDefinition("Desktop environment", DisableParallelization = true)]
    public sealed class DesktopEnvironmentCollection
    {
    }
}
