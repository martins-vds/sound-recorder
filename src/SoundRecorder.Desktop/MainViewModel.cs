using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Recovery;
using SoundRecorder.Infrastructure.Transcription;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Desktop;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly RecorderApplication _recorder;
    private readonly WindowsRecordingEngine _recordingEngine;
    private readonly TranscriptionQueue _queue;
    private readonly LocalTranscriptionModelManager _modelManager;
    private readonly JsonRecordingHistory _history;
    private readonly RecoveryManager _recovery;
    private readonly WindowsAudioDeviceCatalog _devices;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        RecorderApplication recorder,
        WindowsRecordingEngine recordingEngine,
        TranscriptionQueue queue,
        LocalTranscriptionModelManager modelManager,
        JsonRecordingHistory history,
        RecoveryManager recovery,
        WindowsAudioDeviceCatalog devices,
        ILogger<MainViewModel> logger)
    {
        _recorder = recorder;
        _recordingEngine = recordingEngine;
        _queue = queue;
        _modelManager = modelManager;
        _history = history;
        _recovery = recovery;
        _devices = devices;
        _logger = logger;
        DestinationFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Sound Recorder");
        AudioSources =
        [
            new AudioSourceOption(AudioSource.MicrophoneOnly, "Microphone only"),
            new AudioSourceOption(AudioSource.SystemAudioOnly, "System audio only"),
            new AudioSourceOption(
                AudioSource.MicrophoneAndSystemAudio,
                "Microphone and system audio"),
        ];
        Languages = ["English", "Brazilian Portuguese"];
        AudioSource = AudioSource.MicrophoneOnly;
        Language = Languages[0];
    }

    public IReadOnlyList<AudioSourceOption> AudioSources { get; }

    public IReadOnlyList<string> Languages { get; }

    public ObservableCollection<AudioDeviceDescriptor> Microphones { get; } = [];

    public ObservableCollection<AudioDeviceDescriptor> PlaybackDevices { get; } = [];

    public ObservableCollection<SavedRecording> Recordings { get; } = [];

    public ObservableCollection<TranscriptionJob> Jobs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool permissionConfirmed;

    [ObservableProperty]
    private AudioSource audioSource;

    [ObservableProperty]
    private string? selectedMicrophoneId;

    [ObservableProperty]
    private string? selectedPlaybackDeviceId;

    [ObservableProperty]
    private string language = "English";

    [ObservableProperty]
    private string? title;

    [ObservableProperty]
    private string destinationFolder = string.Empty;

    [ObservableProperty]
    private string modelUrl = string.Empty;

    [ObservableProperty]
    private string modelStatus =
        "No compatible model is installed. Recordings remain available for later transcription.";

    [ObservableProperty]
    private string status = "Idle";

    [ObservableProperty]
    private string capturedDuration = "00:00:00";

    [ObservableProperty]
    private double microphoneLevel;

    [ObservableProperty]
    private double systemAudioLevel;

    [ObservableProperty]
    private string message = string.Empty;

    [ObservableProperty]
    private SavedRecording? selectedRecording;

    [ObservableProperty]
    private TranscriptionJob? selectedJob;

    [ObservableProperty]
    private string? transcript;

    [ObservableProperty]
    private bool needsShortRecordingConsent;

    public bool IsIdle => Status == "Idle";

    public bool UsesMicrophone => AudioSource != AudioSource.SystemAudioOnly;

    public bool UsesPlayback => AudioSource != AudioSource.MicrophoneOnly;

    public string SystemAudioExplanation => AudioSource == AudioSource.MicrophoneOnly
        ? "Microphone input is saved as mono."
        : $"{ProductCopy.SystemAudioScope} {ProductCopy.HeadphoneRecommendation} " +
          ProductCopy.EchoCancellationLimitation;

    [RelayCommand]
    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var microphones = await Task.Run(() => _devices.GetMicrophones());
            var playbackDevices = await Task.Run(() => _devices.GetPlaybackDevices());
            ReplaceItems(Microphones, microphones);
            ReplaceItems(PlaybackDevices, playbackDevices);
            SelectedMicrophoneId = Microphones.FirstOrDefault()?.Id;
            SelectedPlaybackDeviceId = PlaybackDevices.FirstOrDefault()?.Id;

            var bundledModel = await Task.Run(() =>
                _modelManager.ImportBundledAsync(
                    Path.Combine(AppContext.BaseDirectory, "models"),
                    CancellationToken.None));
            if (bundledModel is not null)
            {
                Message = $"Bundled model activated: {bundledModel.Id}";
            }

            var history = await _history.LoadAsync(CancellationToken.None);
            ReplaceItems(Recordings, history.OrderByDescending(item => item.Id));
            await Task.Run(() => _queue.RestoreAsync(CancellationToken.None));
            RefreshJobs();
            RefreshModelStatus();
            RefreshState();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Main view initialization failed");
            Message = $"Some local state could not be loaded. See {App.LogFilePath}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        await StartRecordingAsync(allowShortRecording: false);
    }

    public async Task StartRecordingAsync(bool allowShortRecording)
    {
        IsBusy = true;
        try
        {
            var privacyWarning = GetSynchronizedFolderWarning();
            var request = BuildRequest() with
            {
                AllowShortRecordingWhenStorageIsLow = allowShortRecording,
            };
            await Task.Run(() => _recorder.StartAsync(request, PermissionConfirmed));
            Message = _recorder.LastError ?? "Recording started.";
            if (_recorder.LastError is null && privacyWarning is not null)
            {
                Message = $"{privacyWarning} {Message}";
            }
            NeedsShortRecordingConsent =
                _recorder.LastError?.Contains(
                    "four-hour recording",
                    StringComparison.OrdinalIgnoreCase) == true;
        }
        finally
        {
            RefreshState();
            IsBusy = false;
        }
    }

    private bool CanStart() =>
        !IsBusy && IsIdle && PermissionConfirmed;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        await Task.Run(() => _recorder.PauseAsync());
        Message = "Recording paused. Paused time is excluded from the saved audio.";
        RefreshState();
    }

    private bool CanPause() => !IsBusy && Status == "Recording";

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        await Task.Run(() => _recorder.ResumeAsync());
        Message = "Recording resumed.";
        RefreshState();
    }

    private bool CanResume() => !IsBusy && Status == "Paused";

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await StopAsync(enqueueTranscription: true);
    }

    private bool CanStop() => !IsBusy && !IsIdle;

    public async Task<SavedRecording?> StopAsync(bool enqueueTranscription)
    {
        IsBusy = true;
        try
        {
            var recording = await Task.Run(() =>
                _recorder.StopAsync(
                    CancellationToken.None,
                    enqueueTranscription));
            if (recording is not null)
            {
                Message = recording.Outcome == RecordingOutcome.Complete
                    ? $"Recording saved to {recording.AudioPath}. {_recorder.TranscriptionNotice}"
                    : $"Recording was interrupted. Available audio was saved to {recording.AudioPath}.";
                await RefreshHistoryAsync();
                PermissionConfirmed = false;
            }
            else if (_recorder.LastError is not null)
            {
                Message = _recorder.LastError;
            }

            return recording;
        }
        finally
        {
            RefreshState();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportModelAsync(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() =>
                _modelManager.ImportAsync(packagePath, CancellationToken.None));
            RefreshModelStatus();
            Message = "The model package was verified and activated locally.";
        }
        catch (Exception exception)
        {
            Message = $"The model package was not activated: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (!Uri.TryCreate(ModelUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            Message = "Enter an HTTP or HTTPS URL for a model package.";
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() =>
                _modelManager.DownloadAndImportAsync(uri, CancellationToken.None));
            RefreshModelStatus();
            Message = "The model package was downloaded, verified, and activated locally.";
        }
        catch (Exception exception)
        {
            Message = $"The model download or update failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelJobAsync()
    {
        if (SelectedJob is null)
        {
            Message = "Select a transcription job to cancel.";
            return;
        }

        await Task.Run(() =>
            _queue.CancelAsync(SelectedJob.Id, CancellationToken.None));
        RefreshJobs();
    }

    [RelayCommand]
    private async Task RetryTranscriptionAsync()
    {
        var recording = SelectedJob?.Recording ?? SelectedRecording;
        if (recording is null)
        {
            Message = "Select a recording or job to transcribe again.";
            return;
        }

        await Task.Run(() =>
            _queue.RetryAsync(
                recording with { Language = Language },
                CancellationToken.None));
        RefreshJobs();
    }

    [RelayCommand]
    private async Task ViewTranscriptAsync()
    {
        var path = SelectedJob?.Outputs?.TextPath;
        if (path is null || !File.Exists(path))
        {
            Message = "Select a successful transcription job.";
            return;
        }

        var text = await File.ReadAllTextAsync(path);
        Transcript = string.IsNullOrWhiteSpace(text)
            ? "No speech was recognized in this recording."
            : text;
    }

    public async Task AcceptCompletedRecordingAsync(SavedRecording recording)
    {
        await _recorder.AcceptCompletedRecordingAsync(recording);
        PermissionConfirmed = false;
        await RefreshHistoryAsync();
        Message = recording.Outcome == RecordingOutcome.Interrupted
            ? $"Recording was interrupted. Available audio was saved to {recording.AudioPath}. " +
              $"{_recorder.LastError}"
            : $"Recording saved at the four-hour limit: {recording.AudioPath}";
        RefreshState();
    }

    public async Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync()
    {
        return await Task.Run(() => _recovery.FindAsync());
    }

    public async Task RecoverAsync(RecoveryCandidate candidate)
    {
        var result = await Task.Run(() =>
            _recovery.RecoverAsync(candidate, CancellationToken.None));
        if (result.Recording is null)
        {
            Message = result.Error!;
            return;
        }

        await _history.AddAsync(result.Recording, CancellationToken.None);
        await RefreshHistoryAsync();
        Message =
            $"Recovered audio was saved as a recovered recording. Transcription requires an explicit request: {result.Recording.AudioPath}";
    }

    public void Refresh()
    {
        var duration = _recordingEngine.CapturedDuration;
        CapturedDuration = duration.ToString(@"hh\:mm\:ss");
        MicrophoneLevel = _recordingEngine.MicrophoneLevel;
        SystemAudioLevel = _recordingEngine.SystemAudioLevel;
        RefreshState();
        RefreshJobs();
    }

    public bool HasActiveWork() =>
        !IsIdle || Jobs.Any(job => job.Status == TranscriptionJobStatus.Running);

    public async Task StopActiveWorkAsync()
    {
        if (!IsIdle)
        {
            await StopAsync(enqueueTranscription: false);
        }

        foreach (var job in Jobs.Where(
                     item => item.Status == TranscriptionJobStatus.Running))
        {
            await Task.Run(() =>
                _queue.CancelAsync(job.Id, CancellationToken.None));
        }

        RefreshJobs();
    }

    private RecordingRequest BuildRequest()
    {
        return new RecordingRequest(
            AudioSource,
            SelectedMicrophoneId,
            SelectedPlaybackDeviceId,
            DestinationFolder,
            string.IsNullOrWhiteSpace(Title) ? null : Title,
            Language);
    }

    private string? GetSynchronizedFolderWarning()
    {
        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            return null;
        }

        var knownRoots = new[]
            {
                Environment.GetEnvironmentVariable("OneDrive"),
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Environment.GetEnvironmentVariable("Dropbox"),
            }
            .Where(root => !string.IsNullOrWhiteSpace(root));
        return LocalPathPolicy.IsKnownSynchronizedFolder(
            Path.GetFullPath(DestinationFolder),
            knownRoots)
            ? PrivacyPolicy.CloudSynchronizationWarning
            : null;
    }

    private async Task RefreshHistoryAsync()
    {
        var recordings = await _history.LoadAsync(CancellationToken.None);
        ReplaceItems(Recordings, recordings.OrderByDescending(item => item.Id));
    }

    private void RefreshJobs()
    {
        var selectedJobId = SelectedJob?.Id;
        ReplaceItems(Jobs, _queue.Jobs);
        if (selectedJobId is not null)
        {
            SelectedJob = Jobs.FirstOrDefault(job => job.Id == selectedJobId);
        }
    }

    private void RefreshModelStatus()
    {
        var package = _modelManager.CurrentPackage;
        ModelStatus = package is null
            ? "No compatible model is installed. Recordings remain available for later transcription."
            : $"Active model: {package.Id} ({string.Join(", ", package.Languages)})";
    }

    private void RefreshState()
    {
        Status = _recorder.VisibleStatus;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(UsesMicrophone));
        OnPropertyChanged(nameof(UsesPlayback));
        OnPropertyChanged(nameof(SystemAudioExplanation));
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnPermissionConfirmedChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnAudioSourceChanged(AudioSource value)
    {
        OnPropertyChanged(nameof(UsesMicrophone));
        OnPropertyChanged(nameof(UsesPlayback));
        OnPropertyChanged(nameof(SystemAudioExplanation));
    }

    private static void ReplaceItems<T>(
        ObservableCollection<T> target,
        IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}

public sealed record AudioSourceOption(AudioSource Value, string DisplayName);
