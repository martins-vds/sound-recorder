using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
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
    private readonly IRecordingMonitor _recordingEngine;
    private readonly TranscriptionQueue _queue;
    private readonly ILocalTranscriptionModelManager _modelManager;
    private readonly IRecordingHistory _history;
    private readonly RecoveryManager _recovery;
    private readonly IAudioDeviceCatalog _devices;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IRecordingLibrary? _library;
    private readonly IRecordingPlayback? _playback;
    private CancellationTokenSource? _searchCancellation;
    private bool _updatingPlayback;
    private bool _updatingModels;
    private CancellationTokenSource? _modelDownloadCancellation;
    private Task? _recordingDeletionTask;
    private long _transcriptReadVersion;
    private readonly HashSet<Task> _viewReads = [];
    private bool _closingView;
    private Task<bool>? _modelSelection;
    private CancellationTokenSource? _modelSelectionCancellation;

    public MainViewModel(
        RecorderApplication recorder,
        WindowsRecordingEngine recordingEngine,
        TranscriptionQueue queue,
        LocalTranscriptionModelManager modelManager,
        JsonRecordingHistory history,
        RecoveryManager recovery,
        WindowsAudioDeviceCatalog devices,
        ILogger<MainViewModel> logger,
        IRecordingLibrary? library = null,
        IRecordingPlayback? playback = null)
        : this(
            recorder,
            (IRecordingMonitor)recordingEngine,
            queue,
            modelManager,
            history,
            recovery,
            devices,
            logger,
            library,
            playback)
    {
    }

    /// <summary>Connects application commands to capture monitoring and local adapters.</summary>
    public MainViewModel(
        RecorderApplication recorder,
        IRecordingMonitor recordingEngine,
        TranscriptionQueue queue,
        ILocalTranscriptionModelManager modelManager,
        IRecordingHistory history,
        RecoveryManager recovery,
        IAudioDeviceCatalog devices,
        ILogger<MainViewModel> logger,
        IRecordingLibrary? library = null,
        IRecordingPlayback? playback = null)
    {
        _recorder = recorder;
        _recordingEngine = recordingEngine;
        _queue = queue;
        _modelManager = modelManager;
        _history = history;
        _recovery = recovery;
        _devices = devices;
        _logger = logger;
        _library = library;
        _playback = playback;
        destinationFolder = Path.Combine(
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
        language = Languages[0];
        Recordings.CollectionChanged += (_, _) => RefreshUnfilteredLibrary();
    }

    public IReadOnlyList<AudioSourceOption> AudioSources { get; }

    public IReadOnlyList<string> Languages { get; }

    public ObservableCollection<AudioDeviceDescriptor> Microphones { get; } = [];

    public ObservableCollection<AudioDeviceDescriptor> PlaybackDevices { get; } = [];

    public ObservableCollection<SavedRecording> Recordings { get; } = [];

    public ObservableCollection<SavedRecording> FilteredRecordings { get; } = [];

    public ObservableCollection<TranscriptionJob> Jobs { get; } = [];
    public ObservableCollection<TranscriptionJobViewModel> JobRows { get; } = [];
    public ObservableCollection<DownloadedTranscriptionModel> DownloadedModels { get; } = [];
    public ObservableCollection<ModelFamilyViewModel> CatalogFamilies { get; } = [];
    public IReadOnlyList<string> ModelCatalogFilters { get; } = ["All models", "Multilingual", "English only"];

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
    private string language;

    [ObservableProperty]
    private string? title;

    [ObservableProperty]
    private string destinationFolder;

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
    private TranscriptionJobViewModel? selectedJobRow;

    [ObservableProperty]
    private string? transcript;

    [ObservableProperty]
    private bool canCopyTranscript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptReviewLabel))]
    private Guid? completedTranscriptJobId;

    [ObservableProperty]
    private bool needsShortRecordingConsent;

    [ObservableProperty]
    private bool recordingSetupExpanded;

    [ObservableProperty]
    private bool initializationCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteRecording))]
    private bool isDeletingRecording;

    [ObservableProperty]
    private double playbackPositionSeconds;

    [ObservableProperty]
    private double playbackDurationSeconds;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private double playbackVolume = 1;

    [ObservableProperty]
    private DownloadedTranscriptionModel? selectedModel;

    [ObservableProperty]
    private string modelCatalogFilter = "All models";

    [ObservableProperty]
    private bool isDownloadingModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetranscribeSelectedRecording))]
    [NotifyPropertyChangedFor(nameof(CanRestartSelectedJob))]
    [NotifyPropertyChangedFor(nameof(IsModelSelectorEnabled))]
    private bool isSelectingModel;

    [ObservableProperty]
    private double modelDownloadPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDownloadAccessibilityState))]
    private bool isModelDownloadIndeterminate;

    [ObservableProperty]
    private string modelDownloadStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelHeaderButtonText))]
    private string modelHeader = "Choose a local model";

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string libraryMessage = "No recordings yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryView))]
    [NotifyPropertyChangedFor(nameof(IsModelsView))]
    [NotifyPropertyChangedFor(nameof(IsJobsView))]
    private WorkspaceSection workspaceSection;

    public bool IsLibraryView => WorkspaceSection == WorkspaceSection.Library;
    public string ModelHeaderButtonText => ModelHeader.Replace("_", "__", StringComparison.Ordinal);
    public string ModelDownloadAccessibilityState => IsModelDownloadIndeterminate ? "Indeterminate" : "Transferred bytes estimate";
    public bool IsModelsView => WorkspaceSection == WorkspaceSection.Models;
    public bool IsJobsView => WorkspaceSection == WorkspaceSection.Jobs;
    public string JobsMessage => JobRows.Count == 0 ? "No transcription jobs yet." : string.Empty;
    public TranscriptionJobViewModel? SelectedRecordingJobProgress => JobRows.LastOrDefault(row =>
        row.Job.Recording.Id == SelectedRecording?.Id && row.HasActiveProgress);
    public string TranscriptReviewLabel => CompletedTranscriptJobId is null ? string.Empty :
        Jobs.Any(job => job.Recording.Id == SelectedRecording?.Id && job.Status == TranscriptionJobStatus.Running)
            ? "Previously completed transcript" : "Completed transcript";
    public bool IsRecording => Status == "Recording";
    public bool IsPaused => Status == "Paused";
    public bool HasCapture => !IsIdle;
    public bool UseSystemColors => System.Windows.SystemParameters.HighContrast;
    public bool HasPlayableRecording => _playback is not null && SelectedRecording is { AudioDeleted: false };
    public bool CanDeleteRecording => _library is not null && SelectedRecording is not null && !IsDeletingRecording;
    public bool CanDeleteSelectedJob => _library is not null && SelectedJobRow?.CanDelete == true;
    public bool HasPendingDeletion => SelectedRecording?.PendingDeletion is not null;
    public bool CanRetranscribeSelectedRecording =>
        !IsSelectingModel && SelectedRecording is { AudioDeleted: false, PendingDeletion: null };
    public bool CanRestartSelectedJob => !IsSelectingModel && SelectedJobRow?.CanRestart == true;
    public bool IsModelSelectorEnabled => !IsSelectingModel;
    public string? PlaybackSourcePath => _playback?.SourcePath;
    public string SelectedRecordingTitle => SelectedRecording is null
        ? "Select a recording"
        : SelectedRecording.Title ?? Path.GetFileNameWithoutExtension(SelectedRecording.AudioPath);
    public string SelectedRecordingInformation => SelectedRecording is null
        ? "Your saved meetings and transcripts will appear here."
        : $"{SelectedRecording.Language} / {SelectedRecording.CapturedDuration:hh\\:mm\\:ss}" +
          (SelectedRecording.AudioDeleted ? " / Audio deleted. Transcripts retained." : string.Empty) +
          (SelectedRecording.PendingDeletion is not null ? " / Deletion incomplete. Retry is available." : string.Empty);

    public Task DeleteRecordingAsync(bool deleteAllArtifacts)
    {
        if (!CanDeleteRecording || SelectedRecording is not { } recording)
        {
            Message = "Select a recording, or wait for its current deletion to finish.";
            _logger.LogWarning("Recording deletion requested without an available library entry");
            return Task.CompletedTask;
        }
        _recordingDeletionTask = DeleteRecordingCoreAsync(recording,
            deleteAllArtifacts ? RecordingDeletionScope.AllArtifacts : RecordingDeletionScope.AudioOnly);
        return _recordingDeletionTask;
    }

    private async Task DeleteRecordingCoreAsync(SavedRecording recording, RecordingDeletionScope scope)
    {
        IsDeletingRecording = true;
        try
        {
            var library = _library!;
            var result = await Task.Run(() =>
                library.DeleteRecordingAsync(recording.Id, scope, CancellationToken.None));
            await RefreshHistoryAsync();
            RefreshJobs();
            RestoreReviewAfterDeletion(recording.Id);
            RefreshPlayback();
            ShowRecordingDeletionResult(result, scope, recording.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError("Recording deletion failed for {RecordingId} ({ErrorType})",
                recording.Id, exception.GetType().Name);
            Message = "Deletion could not finish. Refresh the library and retry: " + exception.Message;
        }
        finally
        {
            IsDeletingRecording = false;
        }
    }

    [RelayCommand]
    private Task RetryRecordingDeletionAsync()
    {
        if (SelectedRecording?.PendingDeletion is not { } pending)
        {
            Message = "Select a recording with an unfinished deletion to retry.";
            _logger.LogWarning("Deletion retry requested without pending recording deletion");
            return Task.CompletedTask;
        }
        return DeleteRecordingAsync(pending.Scope == RecordingDeletionScope.AllArtifacts);
    }

    private void RestoreReviewAfterDeletion(Guid recordingId)
    {
        if (SelectedRecording?.Id == recordingId)
        {
            SelectedRecording = Recordings.FirstOrDefault(item => item.Id == recordingId) ?? Recordings.FirstOrDefault();
        }
    }

    private void ShowRecordingDeletionResult(LibraryDeletionResult result, RecordingDeletionScope scope, Guid recordingId)
    {
        if (result.Status == LibraryDeletionStatus.Completed)
        {
            Message = scope == RecordingDeletionScope.AudioOnly
                ? "Audio deleted. Transcripts, subtitles, and history were kept."
                : "The recording and all its generated artifacts were deleted.";
            return;
        }
        _logger.LogWarning("Recording deletion {Status} for {RecordingId}", result.Status, recordingId);
        Message = "Deletion was not completed. " + string.Join(" ", result.Issues.Select(DescribeLibraryIssue));
        if (result.PendingDeletion is not null)
        {
            Message += " Retry deletion to remove the remaining artifacts.";
        }
    }

    private static string DescribeLibraryIssue(LibraryIssue issue) =>
        issue.Path is { } path ? $"{issue.Message} ({path})" : issue.Message;

    [RelayCommand]
    private void ShowLibrary() => WorkspaceSection = WorkspaceSection.Library;

    [RelayCommand]
    private void ShowModels() => WorkspaceSection = WorkspaceSection.Models;

    [RelayCommand]
    private void ShowJobs() => WorkspaceSection = WorkspaceSection.Jobs;

    [RelayCommand]
    private void PlayRecording()
    {
        if (_playback is null || SelectedRecording is null)
        {
            Message = "Select an available recording for playback.";
            _logger.LogWarning("Playback requested without a recording or playback adapter");
            return;
        }
        try
        {
            if (_playback.RecordingId != SelectedRecording.Id)
            {
                _playback.Open(SelectedRecording);
            }
            _playback.Volume = PlaybackVolume;
            _playback.Play();
            RefreshPlayback();
        }
        catch (Exception exception) when (IsPlaybackError(exception))
        {
            _logger.LogError("Recording playback failed ({ErrorType})", exception.GetType().Name);
            Message = "The recording could not be played: " + exception.Message;
        }
    }

    [RelayCommand]
    private void PausePlayback()
    {
        RunPlaybackAction(() => _playback?.Pause());
        RefreshPlayback();
    }

    private void RunPlaybackAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (IsPlaybackError(exception))
        {
            _logger.LogError("Recording playback failed ({ErrorType})", exception.GetType().Name);
            Message = "The recording could not be played: " + exception.Message;
            try
            {
                _playback?.Close();
            }
            catch (Exception releaseError) when (IsPlaybackError(releaseError))
            {
                _logger.LogError("Playback handle release failed ({ErrorType})", releaseError.GetType().Name);
                Message += " The audio handle could not be released: " + releaseError.Message;
            }
            IsPlaying = false;
        }
    }

    private static bool IsPlaybackError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or ArgumentException or FormatException or NAudio.MmException;

    private void RefreshPlayback()
    {
        if (_playback is null)
        {
            return;
        }
        _updatingPlayback = true;
        try
        {
            RunPlaybackAction(() =>
            {
                PlaybackDurationSeconds = _playback.Duration.TotalSeconds;
                PlaybackPositionSeconds = _playback.Position.TotalSeconds;
                IsPlaying = _playback.IsPlaying;
                OnPropertyChanged(nameof(PlaybackSourcePath));
                if (_playback.LastError is { } error && Message != error)
                {
                    Message = error;
                    _logger.LogError("Recording playback device reported an error");
                }
            });
        }
        finally
        {
            _updatingPlayback = false;
        }
    }

    partial void OnPlaybackPositionSecondsChanged(double value)
    {
        if (!_updatingPlayback && _playback?.RecordingId is not null)
        {
            RunPlaybackAction(() => _playback.Position = TimeSpan.FromSeconds(value));
        }
    }

    partial void OnPlaybackVolumeChanged(double value)
    {
        if (_playback is not null)
        {
            RunPlaybackAction(() => _playback.Volume = value);
        }
    }

    public void ReleaseViewResources()
    {
        var search = _searchCancellation;
        _searchCancellation = null;
        search?.Cancel();
        search?.Dispose();
        _playback?.Dispose();
    }

    public bool HasPendingViewWork => _viewReads.Count != 0 || IsDownloadingModel || IsSelectingModel;

    public async Task PrepareViewCloseAsync()
    {
        _closingView = true;
        _transcriptReadVersion++;
        _searchCancellation?.Cancel();
        _modelDownloadCancellation?.Cancel();
        _modelSelectionCancellation?.Cancel();
        await Task.WhenAll(_viewReads.ToArray());
        if (_modelSelection is { } selection)
        {
            await selection;
        }
        if (DownloadCatalogModelCommand.ExecutionTask is { } download)
        {
            await download;
        }
    }

    private async Task ObserveViewReadAsync(Task read)
    {
        _viewReads.Add(read);
        try
        {
            await read;
        }
        finally
        {
            _viewReads.Remove(read);
        }
    }

    partial void OnWorkspaceSectionChanged(WorkspaceSection value)
    {
        if (value != WorkspaceSection.Library)
        {
            PausePlayback();
        }
    }

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
            var microphones = await Task.Run(_devices.GetMicrophones);
            var playbackDevices = await Task.Run(_devices.GetPlaybackDevices);
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

            var history = await LoadLibraryRecordingsAsync(CancellationToken.None);
            ReplaceItems(Recordings, history.OrderByDescending(item => item.Id));
            await Task.Run(() => _queue.RestoreAsync(CancellationToken.None));
            RefreshJobs();
            await Task.Run(() => _modelManager.RefreshInventoryAsync(CancellationToken.None));
            SelectedRecording = Recordings.FirstOrDefault();
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
            InitializationCompleted = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!PermissionConfirmed)
        {
            RecordingSetupExpanded = true;
            Message = "Confirm recording permission before starting.";
            _logger.LogInformation("Recording setup opened to request permission confirmation");
            return;
        }
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
        !IsBusy && IsIdle;

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
            var selection = IsSelectingModel ? _modelSelection : null;
            var recording = await Task.Run(() =>
                _recorder.StopAsync(
                    CancellationToken.None,
                    enqueueTranscription && selection is null));
            if (recording is not null)
            {
                await PresentStoppedRecordingAsync(recording, enqueueTranscription, selection);
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

    private async Task PresentStoppedRecordingAsync(
        SavedRecording recording, bool enqueueTranscription, Task<bool>? selection)
    {
        Message = recording.Outcome == RecordingOutcome.Complete
            ? $"Recording saved to {recording.AudioPath}. {_recorder.TranscriptionNotice}"
            : $"Recording was interrupted. Available audio was saved to {recording.AudioPath}.";
        await RefreshHistoryAsync();
        PermissionConfirmed = false;
        if (enqueueTranscription && selection is not null && recording.Outcome == RecordingOutcome.Complete)
        {
            Message += " Transcription waits for the pending model selection.";
            _ = ObserveViewReadAsync(EnqueueAfterSelectionAsync(recording, selection));
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
                _modelManager.RegisterAsync(packagePath, CancellationToken.None));
            RefreshModelStatus();
            Message = "The model package was verified and registered locally. Choose it from Downloaded models.";
        }
        catch (Exception exception)
        {
            _logger.LogError("Model package registration failed ({ErrorType})", exception.GetType().Name);
            Message = $"The model package was not registered: {exception.Message}";
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
        var job = SelectedJob;
        if (job is null)
        {
            Message = "Select a transcription job to cancel.";
            return;
        }

        try
        {
            var retryingCheckpoint = job.HasTerminalCheckpointFailure;
            await Task.Run(() => _queue.CancelAsync(job.Id, CancellationToken.None));
            RefreshJobs();
            if (retryingCheckpoint)
            {
                Message = "The job history checkpoint was saved.";
            }
        }
        catch (Exception exception) when (IsWorkspaceOperationError(exception))
        {
            LogJobCancellationFailed(_logger, job.Id, exception.GetType().Name);
            Message = "The transcription job could not be cancelled: " + exception.Message;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcription cancellation failed for job {JobId} ({ErrorType})")]
    private static partial void LogJobCancellationFailed(ILogger logger, Guid jobId, string errorType);

    [RelayCommand]
    private async Task StopJobAsync()
    {
        if (SelectedJob is not { Status: TranscriptionJobStatus.Running, IsTerminationPending: false } job)
        {
            Message = "Select a running transcription job to stop.";
            _logger.LogWarning("Stop requested without an available running transcription job");
            return;
        }
        try
        {
            await Task.Run(() => _queue.StopAsync(job.Id, CancellationToken.None));
            RefreshJobs();
        }
        catch (Exception exception) when (IsWorkspaceOperationError(exception))
        {
            _logger.LogError("Transcription Stop failed for job {JobId} ({ErrorType})", job.Id, exception.GetType().Name);
            Message = "The transcription job could not be stopped: " + exception.Message;
        }
    }

    public async Task DeleteSelectedJobHistoryAsync()
    {
        var job = DeletableJob();
        if (job is null || _library is not { } library)
        {
            Message = "Select a terminal transcription job whose worker has been released.";
            _logger.LogWarning("Job-history deletion requested without an available terminal job");
            return;
        }
        try
        {
            var result = await Task.Run(() => library.DeleteJobHistoryAsync(job.Id, CancellationToken.None));
            RefreshJobs();
            ShowJobHistoryDeletionResult(result, job.Id);
        }
        catch (Exception exception) when (IsWorkspaceOperationError(exception))
        {
            _logger.LogError("Job-history deletion failed for {JobId} ({ErrorType})", job.Id, exception.GetType().Name);
            Message = "Job history could not be deleted. Retry the deletion: " + exception.Message;
        }
    }

    [RelayCommand]
    private Task RetryTranscriptionAsync() =>
        RetryRecordingAsync(SelectedJob?.Recording ?? SelectedRecording);

    private static bool IsWorkspaceOperationError(Exception exception) =>
        IsWorkspaceReadError(exception) || exception is InvalidOperationException ||
        exception is AggregateException { InnerExceptions.Count: > 0 } aggregate &&
        aggregate.InnerExceptions.All(IsWorkspaceOperationError);

    private static bool IsWorkspaceReadError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private TranscriptionJob? DeletableJob() => CanDeleteSelectedJob ? SelectedJob : null;

    private void ShowJobHistoryDeletionResult(LibraryDeletionResult result, Guid jobId)
    {
        Message = result.Status == LibraryDeletionStatus.Completed
            ? "Job history deleted. Audio and completed outputs were kept."
            : "Job history was not deleted. " + string.Join(" ", result.Issues.Select(issue => issue.Message));
        if (result.Status != LibraryDeletionStatus.Completed)
        {
            _logger.LogWarning("Job-history deletion {Status} for {JobId}", result.Status, jobId);
        }
    }

    [RelayCommand]
    private Task RetranscribeSelectedRecordingAsync() =>
        RetryRecordingAsync(SelectedRecording);

    private async Task RetryRecordingAsync(SavedRecording? recording)
    {
        if (!await WaitForPendingModelSelectionAsync())
        {
            return;
        }
        if (recording is null)
        {
            Message = "Select a recording or job to transcribe again.";
            return;
        }
        recording = Recordings.FirstOrDefault(item => item.Id == recording.Id) ?? recording;
        if (recording.AudioDeleted || recording.PendingDeletion is not null)
        {
            Message = "Audio is deleted or its deletion is unfinished. Retained transcripts remain available.";
            _logger.LogWarning("Transcription requested for unavailable recording {RecordingId}", recording.Id);
            return;
        }

        await Task.Run(() =>
            _queue.RetryAsync(
                recording with { Language = Language },
                CancellationToken.None));
        RefreshJobs();
    }

    private async Task<bool> WaitForPendingModelSelectionAsync() =>
        _modelSelection is not { IsCompleted: false } selection || await selection;

    private async Task EnqueueAfterSelectionAsync(SavedRecording recording, Task<bool> selection)
    {
        if (!await selection)
        {
            _logger.LogWarning("Saved audio retained without transcription because model selection did not complete");
            Message = "Audio was saved. Transcription requires a completed model selection and an explicit retry.";
            return;
        }
        if (_closingView)
        {
            _logger.LogInformation("Saved audio retained for later transcription while the workspace is closing");
            Message = "Audio was saved for later transcription while the workspace is closing.";
            return;
        }
        try
        {
            await Task.Run(() => _queue.EnqueueAsync(recording, CancellationToken.None));
            RefreshJobs();
            Message = "Saved audio was queued for transcription after model selection completed.";
        }
        catch (Exception exception) when (IsWorkspaceOperationError(exception))
        {
            _logger.LogError("Deferred transcription enqueue failed for recording {RecordingId} ({ErrorType})",
                recording.Id, exception.GetType().Name);
            Message = "Audio was saved, but transcription could not be queued: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ViewTranscriptAsync()
    {
        var job = SelectedJob;
        var path = job?.Outputs?.TextPath;
        if (job is not { Status: TranscriptionJobStatus.Succeeded } || path is null || !File.Exists(path))
        {
            Message = "Select a successful transcription job.";
            Transcript = null;
            CanCopyTranscript = false;
            CompletedTranscriptJobId = null;
            _logger.LogWarning("Transcript view requested without an available completed output");
            return;
        }
        await LoadViewedJobTranscriptAsync(job, path);
    }

    private async Task LoadViewedJobTranscriptAsync(TranscriptionJob job, string path)
    {
        var version = PrepareJobReview(job);
        if (_library is { } library)
        {
            await LoadRetainedTranscriptAsync(library, SelectedRecording, job.Recording.Id, job.Id, version);
            return;
        }
        try
        {
            var text = await File.ReadAllTextAsync(path);
            if (IsCurrentTranscriptRead(job.Recording.Id, version))
            {
                ApplyCompletedTranscript(text, job.Id);
            }
        }
        catch (Exception exception) when (IsWorkspaceReadError(exception))
        {
            _logger.LogError("Selected transcript read failed for job {JobId} ({ErrorType})",
                job.Id, exception.GetType().Name);
            if (IsCurrentTranscriptRead(job.Recording.Id, version))
            {
                Transcript = null;
                CanCopyTranscript = false;
                Message = "The completed transcript could not be read. Check its output folder and retry.";
            }
        }
    }

    private long PrepareJobReview(TranscriptionJob job)
    {
        SelectedRecording = Recordings.FirstOrDefault(recording => recording.Id == job.Recording.Id) ?? job.Recording;
        CanCopyTranscript = false;
        CompletedTranscriptJobId = null;
        return ++_transcriptReadVersion;
    }

    private void ApplyCompletedTranscript(string text, Guid? jobId)
    {
        Transcript = string.IsNullOrWhiteSpace(text) ? "No speech was recognized in this recording." : text;
        CanCopyTranscript = !string.IsNullOrWhiteSpace(text);
        CompletedTranscriptJobId = jobId;
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
        OnPropertyChanged(nameof(UseSystemColors));
        RefreshPlayback();
        CapturedDuration = _recordingEngine.CapturedDuration.ToString(@"hh\:mm\:ss");
        MicrophoneLevel = _recordingEngine.MicrophoneLevel;
        SystemAudioLevel = _recordingEngine.SystemAudioLevel;
        RefreshState();
        RefreshJobs();
    }

    public bool HasActiveWork() =>
        !IsIdle || IsDeletingRecording || Jobs.Any(job =>
            job.Status == TranscriptionJobStatus.Running || job.IsTerminationPending);

    public async Task StopActiveWorkAsync()
    {
        if (_recordingDeletionTask is { } deletion)
        {
            await deletion;
        }
        if (!IsIdle)
        {
            await StopAsync(enqueueTranscription: false);
        }

        foreach (var job in Jobs.Where(
                     item => item.Status == TranscriptionJobStatus.Running || item.IsTerminationPending))
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
        var recordings = await LoadLibraryRecordingsAsync(CancellationToken.None);
        ReplaceItems(Recordings, recordings.OrderByDescending(item => item.Id));
        if (SearchText.Length != 0)
        {
            await FilterLibraryAsync(SearchText, CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<SavedRecording>> LoadLibraryRecordingsAsync(CancellationToken token)
    {
        if (_library is null)
        {
            return await _history.LoadAsync(token);
        }
        var entries = await _library.LoadAsync(token);
        if (entries.Any(entry => entry.Issues.Count > 0))
        {
            Message = "Some recording artifacts are unavailable. Retained transcripts can still be reviewed.";
            _logger.LogWarning("Library contains unavailable recording artifacts");
        }
        return entries.Select(entry => entry.Recording).ToArray();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_closingView) return;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = ObserveViewReadAsync(FilterLibraryAsync(value, _searchCancellation.Token));
    }

    private async Task FilterLibraryAsync(string query, CancellationToken token)
    {
        if (_library is null)
        {
            Message = "Library search is unavailable in this application configuration.";
            _logger.LogWarning("Library search requested without a library adapter");
            return;
        }
        try
        {
            var entries = await _library.SearchAsync(query, token);
            ApplyLibrarySearch(entries, query, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("Superseded library search cancelled");
        }
        catch (Exception exception) when (IsWorkspaceReadError(exception))
        {
            _logger.LogError("Library search failed ({ErrorType})", exception.GetType().Name);
            Message = "Library search could not read a local artifact. Check the output files and retry.";
        }
    }

    private string LibrarySearchMessage(IReadOnlyList<RecordingLibraryEntry> entries)
    {
        if (entries.Any(entry => entry.Issues.Count > 0))
        {
            return "Some library files are unavailable; search results may be incomplete.";
        }
        if (entries.Count != 0)
        {
            return string.Empty;
        }
        return Recordings.Count == 0 ? "No recordings yet." : "No matching meetings. Try another title or transcript phrase.";
    }

    private void ApplyLibrarySearch(IReadOnlyList<RecordingLibraryEntry> entries, string query, CancellationToken token)
    {
        if (!token.IsCancellationRequested && SearchText == query)
        {
            ReplaceItems(FilteredRecordings, entries.Select(entry => entry.Recording));
            LibraryMessage = LibrarySearchMessage(entries);
        }
    }

    private void RefreshUnfilteredLibrary()
    {
        if (SearchText.Length == 0)
        {
            ReplaceItems(FilteredRecordings, Recordings);
            LibraryMessage = Recordings.Count == 0 ? "No recordings yet." : string.Empty;
        }
    }

    private void RefreshJobs()
    {
        var selectedJobId = SelectedJob?.Id;
        var current = _queue.Jobs;
        if (!Jobs.SequenceEqual(current))
        {
            ReplaceItems(Jobs, current);
        }
        SynchronizeJobRows(current);
        RestoreJobSelection(selectedJobId);
        OnPropertyChanged(nameof(CanDeleteSelectedJob));
        OnPropertyChanged(nameof(JobsMessage));
        OnPropertyChanged(nameof(TranscriptReviewLabel));
        OnPropertyChanged(nameof(SelectedRecordingJobProgress));
        OnPropertyChanged(nameof(CanRestartSelectedJob));
    }

    private void SynchronizeJobRows(IReadOnlyList<TranscriptionJob> current)
    {
        foreach (var obsolete in JobRows.Where(row => current.All(job => job.Id != row.Id)).ToArray())
        {
            JobRows.Remove(obsolete);
        }
        var presented = current.OrderBy(job => job.IsTerminationPending ||
            job.Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running ? 0 : 1)
            .ThenBy(job => job.CreatedAt).ToArray();
        for (var index = 0; index < presented.Length; index++)
        {
            var job = presented[index];
            RefreshJobRow(job, index);
        }
    }

    private void RefreshJobRow(TranscriptionJob job, int index)
    {
        var row = PlaceJobRow(job, index);
            var recording = Recordings.FirstOrDefault(item => item.Id == job.Recording.Id) ?? job.Recording;
            row.Refresh(job, _modelManager.DownloadedModels.FirstOrDefault(model => model.Id == job.ModelId)?.DisplayName ??
                job.ModelId ?? "Model choice required", !recording.AudioDeleted && recording.PendingDeletion is null);
    }

    private TranscriptionJobViewModel PlaceJobRow(TranscriptionJob job, int index)
    {
        var row = JobRows.FirstOrDefault(item => item.Id == job.Id);
        if (row is null)
        {
            row = new TranscriptionJobViewModel(job);
            JobRows.Insert(index, row);
        }
        else if (JobRows.IndexOf(row) != index)
        {
            JobRows.Move(JobRows.IndexOf(row), index);
        }
        return row;
    }

    private void RestoreJobSelection(Guid? selectedJobId)
    {
        if (selectedJobId is not null)
        {
            SelectedJob = Jobs.FirstOrDefault(job => job.Id == selectedJobId);
        }
        SelectedJobRow = JobRows.FirstOrDefault(row => row.Id == SelectedJob?.Id);
    }

    partial void OnSelectedJobRowChanged(TranscriptionJobViewModel? value)
    {
        SelectedJob = value?.Job;
        OnPropertyChanged(nameof(CanDeleteSelectedJob));
        OnPropertyChanged(nameof(CanRestartSelectedJob));
    }

    private void RefreshModelStatus()
    {
        var package = _modelManager.CurrentPackage;
        ModelStatus = package is null
            ? "No compatible model is installed. Recordings remain available for later transcription."
            : $"Active model: {package.Id} ({string.Join(", ", package.Languages)})";
        _updatingModels = true;
        try
        {
            ReplaceItems(DownloadedModels, _modelManager.DownloadedModels);
            SelectedModel = DownloadedModels.FirstOrDefault(model => model.Id == _modelManager.SelectedModelId);
            ModelHeader = SelectedModel?.DisplayName ?? (_modelManager.SelectedModelId is null
                ? "Choose a local model" : "Selected model unavailable");
        }
        finally
        {
            _updatingModels = false;
        }
        RefreshModelCatalog();
    }

    private void RefreshModelCatalog()
    {
        var entries = TranscriptionModelCatalog.Entries.Where(entry =>
            ModelCatalogFilter == "All models" ||
            (entry.Languages.Contains("Brazilian Portuguese") == (ModelCatalogFilter == "Multilingual")));
        ReplaceItems(CatalogFamilies, entries.GroupBy(entry => entry.Family)
            .Select(group => new ModelFamilyViewModel(group.ToArray())));
    }

    partial void OnModelCatalogFilterChanged(string value) => RefreshModelCatalog();

    partial void OnSelectedModelChanged(DownloadedTranscriptionModel? value)
    {
        if (!_updatingModels && value is not null)
        {
            _modelSelection = SelectDownloadedModelAsync(value);
        }
    }

    private async Task<bool> SelectDownloadedModelAsync(DownloadedTranscriptionModel model)
    {
        IsSelectingModel = true;
        _modelSelectionCancellation = new CancellationTokenSource();
        ModelHeader = "Selecting " + model.DisplayName;
        try
        {
            await Task.Run(() => _modelManager.SelectAsync(model.Id, _modelSelectionCancellation.Token));
            RefreshModelStatus();
            Message = "The downloaded model is selected for new transcription jobs.";
            ShowModelCompatibilityNotice();
            return true;
        }
        catch (OperationCanceledException) when (_modelSelectionCancellation.IsCancellationRequested)
        {
            _logger.LogInformation("Model selection cancelled while closing the workspace");
            return false;
        }
        catch (Exception exception) when (IsWorkspaceOperationError(exception) || exception is ArgumentException)
        {
            _logger.LogError("Downloaded model selection failed ({ErrorType})", exception.GetType().Name);
            Message = "The model could not be selected: " + exception.Message;
            RefreshModelStatus();
            return false;
        }
        finally
        {
            IsSelectingModel = false;
            _modelSelectionCancellation.Dispose();
            _modelSelectionCancellation = null;
        }
    }

    [RelayCommand]
    private async Task DownloadCatalogModelAsync(ModelCatalogEntry? entry)
    {
        if (entry is null || IsDownloadingModel)
        {
            Message = "Select a catalog model, or finish the current download first.";
            _logger.LogWarning("Model download requested without an available catalog selection");
            return;
        }
        _modelDownloadCancellation = new CancellationTokenSource();
        IsDownloadingModel = true;
        ModelDownloadStatus = "Resolving model metadata";
        IsModelDownloadIndeterminate = true;
        var progress = new Progress<ModelDownloadProgress>(update => UpdateCatalogDownloadProgress(entry, update));
        try
        {
            await Task.Run(() => _modelManager.DownloadAsync(entry, progress, _modelDownloadCancellation.Token));
            RefreshModelStatus();
            ModelDownloadStatus = "Verified download complete. Choose it from Downloaded models.";
        }
        catch (OperationCanceledException) when (_modelDownloadCancellation.IsCancellationRequested)
        {
            ModelDownloadStatus = "Download cancelled. The selected model is unchanged.";
            _logger.LogInformation("Catalog model download cancelled");
        }
        catch (Exception exception) when (IsModelDownloadError(exception))
        {
            _logger.LogError("Catalog model download failed ({ErrorType})", exception.GetType().Name);
            ModelDownloadStatus = "Download failed. Retry the catalog model download.";
            Message = ModelDownloadStatus + " " + exception.Message;
        }
        finally
        {
            IsDownloadingModel = false;
            _modelDownloadCancellation.Dispose();
            _modelDownloadCancellation = null;
            RefreshModelStatus();
        }
    }

    [RelayCommand]
    private void CancelModelDownload() => _modelDownloadCancellation?.Cancel();

    private void UpdateCatalogDownloadProgress(ModelCatalogEntry entry, ModelDownloadProgress update)
    {
        if (!IsDownloadingModel)
        {
            return;
        }
        ModelDownloadStatus = update.Phase.ToString();
        IsModelDownloadIndeterminate = update.Percent is null || update.Phase != ModelDownloadPhase.Downloading;
        ModelDownloadPercent = update.Percent ?? 0;
        ModelHeader = $"Downloading {entry.DisplayName}: " +
            (update.Percent is { } percent ? $"{percent:0}%" : update.Phase.ToString());
    }

    private static bool IsModelDownloadError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or ArgumentException;

    private void RefreshState()
    {
        Status = _recorder.VisibleStatus;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(HasCapture));
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

    partial void OnLanguageChanged(string value) => ShowModelCompatibilityNotice();

    private void ShowModelCompatibilityNotice()
    {
        if (_modelManager.SelectedModelId is not null && !_modelManager.HasCompatibleModel(Language))
        {
            Message = Language == "Brazilian Portuguese"
                ? "Choose a compatible multilingual downloaded model for Brazilian Portuguese. Recording remains available."
                : $"Choose a compatible downloaded model for {Language}. Recording remains available.";
            _logger.LogWarning("Selected transcription model is incompatible with new-job language {Language}", Language);
        }
        else if (Message.StartsWith("Choose a compatible ", StringComparison.Ordinal))
        {
            Message = "The selected downloaded model supports the new-job language.";
        }
    }

    partial void OnAudioSourceChanged(AudioSource value)
    {
        OnPropertyChanged(nameof(UsesMicrophone));
        OnPropertyChanged(nameof(UsesPlayback));
        OnPropertyChanged(nameof(SystemAudioExplanation));
    }

    partial void OnSelectedRecordingChanged(SavedRecording? value)
    {
        if (_closingView) return;
        _transcriptReadVersion++;
        Transcript = null;
        CanCopyTranscript = false;
        CompletedTranscriptJobId = null;
        if (_playback is not null && _playback.RecordingId != value?.Id)
        {
            RunPlaybackAction(_playback.Close);
            RefreshPlayback();
        }
        OnPropertyChanged(nameof(HasPlayableRecording));
        OnPropertyChanged(nameof(CanDeleteRecording));
        OnPropertyChanged(nameof(HasPendingDeletion));
        OnPropertyChanged(nameof(CanRetranscribeSelectedRecording));
        OnPropertyChanged(nameof(SelectedRecordingJobProgress));
        OnPropertyChanged(nameof(SelectedRecordingTitle));
        OnPropertyChanged(nameof(SelectedRecordingInformation));
        if (value is not null)
        {
            _ = ObserveViewReadAsync(LoadRecordingTranscriptAsync(value.Id));
        }
        else
        {
            Transcript = null;
            SelectedJob = null;
            SelectedJobRow = null;
        }
    }

    private async Task LoadRecordingTranscriptAsync(Guid recordingId)
    {
        var version = ++_transcriptReadVersion;
        var recording = Recordings.FirstOrDefault(item => item.Id == recordingId) ?? SelectedRecording;
        var job = FindCompletedRecordingJob(recordingId);
        var jobId = FindRetainedTranscriptJob(recording, job);
        if (_library is { } library && jobId is { } retainedJobId)
        {
            await LoadRetainedTranscriptAsync(library, recording, recordingId, retainedJobId, version);
            return;
        }
        var path = job?.Outputs?.TextPath;
        if (path is null)
        {
            Transcript = "No completed transcript is available for this recording.";
            return;
        }
        await LoadTranscriptFileAsync(recordingId, job?.Id, path, version);
    }

    private TranscriptionJob? FindCompletedRecordingJob(Guid recordingId) =>
        Jobs.LastOrDefault(item => item.Recording.Id == recordingId && item.Status == TranscriptionJobStatus.Succeeded);

    private static Guid? FindRetainedTranscriptJob(SavedRecording? recording, TranscriptionJob? job) =>
        recording?.TranscriptOutputs.LastOrDefault()?.JobId ?? job?.Id;

    private async Task LoadRetainedTranscriptAsync(IRecordingLibrary library, SavedRecording? recording,
        Guid recordingId, Guid jobId, long version)
    {
        try
        {
            var result = await library.ReadTranscriptAsync(recordingId, jobId, CancellationToken.None);
            if (IsCurrentTranscriptRead(recordingId, version))
            {
                ApplyRetainedTranscript(result, recording, recordingId, jobId);
            }
        }
        catch (Exception exception) when (IsWorkspaceReadError(exception))
        {
            _logger.LogError("Retained transcript read failed for recording {RecordingId} ({ErrorType})",
                recordingId, exception.GetType().Name);
            if (IsCurrentTranscriptRead(recordingId, version))
            {
                Transcript = null;
                Message = "The retained transcript could not be read. Check its output folder.";
            }
        }
    }

    private void ApplyRetainedTranscript(TranscriptReadResult result, SavedRecording? recording, Guid recordingId, Guid jobId)
    {
        if (result.Issues.Count > 0)
        {
            _logger.LogWarning("Retained transcript has unavailable artifacts for recording {RecordingId}", recordingId);
            if (recording?.PendingDeletion is null)
            {
                Message = "A retained transcript artifact is unavailable. Check its output folder.";
            }
        }
        Transcript = result.Text;
        CanCopyTranscript = !string.IsNullOrWhiteSpace(result.Text);
        CompletedTranscriptJobId = result.Text is null ? null : jobId;
    }

    private async Task LoadTranscriptFileAsync(Guid recordingId, Guid? jobId, string path, long version)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path);
            if (IsCurrentTranscriptRead(recordingId, version))
            {
                ApplyCompletedTranscript(text, jobId);
            }
        }
        catch (Exception exception) when (IsWorkspaceReadError(exception))
        {
            _logger.LogError("Completed transcript could not be read for recording {RecordingId} ({ErrorType})",
                recordingId, exception.GetType().Name);
            if (IsCurrentTranscriptRead(recordingId, version))
            {
                Transcript = null;
                Message = "The completed transcript could not be read. Open its output folder or retry.";
            }

        }
    }

    private bool IsCurrentTranscriptRead(Guid recordingId, long version) =>
        SelectedRecording?.Id == recordingId && _transcriptReadVersion == version;

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

public enum WorkspaceSection
{
    Library,
    Models,
    Jobs,
}
