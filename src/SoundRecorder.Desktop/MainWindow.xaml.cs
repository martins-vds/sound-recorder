using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Privacy;
using SoundRecorder.Infrastructure.Recovery;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Desktop;

public partial class MainWindow : Window
{
    private readonly RecorderApplication _recorder;
    private readonly WindowsRecordingEngine _recordingEngine;
    private readonly TranscriptionQueue _queue;
    private readonly LocalTranscriptionModelManager _modelManager;
    private readonly JsonRecordingHistory _history;
    private readonly RecoveryManager _recovery;
    private readonly NotifyIcon _notifyIcon;
    private readonly DispatcherTimer _timer;
    private bool _allowClose;
    private bool _limitWarningShown;

    public MainWindow()
    {
        InitializeComponent();
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundRecorder");
        Directory.CreateDirectory(root);
        _recordingEngine = new WindowsRecordingEngine(Path.Combine(root, "capture"));
        _modelManager = new LocalTranscriptionModelManager(
            Path.Combine(root, "models"),
            new HttpClient());
        var writer = new AtomicTranscriptWriter();
        var transcriber = new WhisperLocalTranscriber(_modelManager);
        var jobRepository = new JsonTranscriptionJobRepository(
            Path.Combine(root, "jobs.json"));
        _queue = new TranscriptionQueue(transcriber, writer, jobRepository);
        _history = new JsonRecordingHistory(Path.Combine(root, "history.json"));
        _recorder = new RecorderApplication(
            _recordingEngine,
            _queue,
            _modelManager,
            _history);
        _recovery = new RecoveryManager(
            new LocalRecoveryStorage(Path.Combine(root, "capture")));
        _notifyIcon = CreateNotifyIcon();
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += TimerOnTick;
        Loaded += MainWindow_Loaded;
        _recordingEngine.RecordingCompleted += RecordingEngineOnRecordingCompleted;
        Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionEnding += SystemEventsOnSessionEnding;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs args)
    {
        LoadDevices();
        DestinationBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Sound Recorder");
        SourceCombo.SelectedIndex = 0;
        LanguageCombo.SelectedIndex = 0;
        UpdateModelStatus();
        await RefreshHistoryAsync();
        await _queue.RestoreAsync(CancellationToken.None);
        await OfferRecoveryAsync();
        RefreshJobs();
        _timer.Start();
    }

    private void LoadDevices()
    {
        var catalog = new WindowsAudioDeviceCatalog();
        MicrophoneCombo.ItemsSource = catalog.GetMicrophones();
        PlaybackCombo.ItemsSource = catalog.GetPlaybackDevices();
        MicrophoneCombo.SelectedIndex = MicrophoneCombo.Items.Count > 0 ? 0 : -1;
        PlaybackCombo.SelectedIndex = PlaybackCombo.Items.Count > 0 ? 0 : -1;
    }

    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        if (PermissionCheckBox.IsChecked != true)
        {
            ShowMessage("Confirm that you have permission to record before starting.");
            return;
        }

        WarnIfSynchronizedFolder();
        var request = BuildRequest();
        SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
        await _recorder.StartAsync(request, permissionConfirmed: true);
        if (_recorder.LastError is null)
        {
            ShowMessage("Recording started.");
            _notifyIcon.Text = "Sound Recorder - recording";
        }
        else
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            var canRetryShorter = _recorder.LastError.Contains(
                                      "four-hour recording",
                                      StringComparison.OrdinalIgnoreCase) &&
                                  System.Windows.MessageBox.Show(
                                      _recorder.LastError +
                                      "\n\nProceed with a shorter recording when free space runs out?",
                                      "Insufficient space for maximum duration",
                                      MessageBoxButton.YesNo,
                                      MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (canRetryShorter)
            {
                await _recorder.StartAsync(
                    request with { AllowShortRecordingWhenStorageIsLow = true },
                    permissionConfirmed: true);
                if (_recorder.LastError is null)
                {
                    SetThreadExecutionState(
                        ExecutionState.Continuous | ExecutionState.SystemRequired);
                }
            }

            ShowMessage(_recorder.LastError ?? "Recording started.");
        }

        UpdateControls();
    }

    private async void Pause_Click(object sender, RoutedEventArgs args)
    {
        await _recorder.PauseAsync();
        ShowMessage("Recording paused. Paused time is excluded from the saved audio.");
        UpdateControls();
    }

    private async void Resume_Click(object sender, RoutedEventArgs args)
    {
        await _recorder.ResumeAsync();
        ShowMessage("Recording resumed.");
        UpdateControls();
    }

    private async void Stop_Click(object sender, RoutedEventArgs args)
    {
        await StopRecordingAsync(enqueueTranscription: true);
    }

    private async Task StopRecordingAsync(bool enqueueTranscription)
    {
        var recording = await _recorder.StopAsync(
            CancellationToken.None,
            enqueueTranscription);
        SetThreadExecutionState(ExecutionState.Continuous);
        _limitWarningShown = false;
        if (recording is not null)
        {
            ShowMessage(
                recording.Outcome == RecordingOutcome.Complete
                    ? $"Recording saved to {recording.AudioPath}. {_recorder.TranscriptionNotice}"
                    : $"Recording was interrupted. Available audio was saved to {recording.AudioPath}.");
            await RefreshHistoryAsync();
            RefreshJobs();
            PermissionCheckBox.IsChecked = false;
        }

        _notifyIcon.Text = "Sound Recorder";
        UpdateControls();
    }

    private RecordingRequest BuildRequest()
    {
        var source = SourceCombo.SelectedItem is ComboBoxItem item &&
                     Enum.TryParse<AudioSource>((string)item.Tag, out var parsed)
            ? parsed
            : AudioSource.MicrophoneOnly;
        return new RecordingRequest(
            source,
            (string?)MicrophoneCombo.SelectedValue,
            (string?)PlaybackCombo.SelectedValue,
            DestinationBox.Text.Trim(),
            string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim(),
            ((ComboBoxItem)LanguageCombo.SelectedItem).Content.ToString()!,
            AllowShortRecordingWhenStorageIsLow: false);
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var source = BuildRequest().Source;
        MicrophoneCombo.IsEnabled = source != AudioSource.SystemAudioOnly;
        PlaybackCombo.IsEnabled = source != AudioSource.MicrophoneOnly;
        SystemAudioScopeText.Text = source == AudioSource.MicrophoneOnly
            ? "Microphone input is saved as mono."
            : $"{ProductCopy.SystemAudioScope} {ProductCopy.HeadphoneRecommendation} " +
              ProductCopy.EchoCancellationLimitation;
    }

    private void ChooseDestination_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            DestinationBox.Text = dialog.FolderName;
            WarnIfSynchronizedFolder();
        }
    }

    private async void ImportModel_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sound Recorder model package (*.zip)|*.zip",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _modelManager.ImportAsync(dialog.FileName, CancellationToken.None);
            UpdateModelStatus();
            ShowMessage("The model package was verified and activated locally.");
        }
        catch (Exception exception)
        {
            ShowMessage($"The model package was not activated: {exception.Message}");
        }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs args)
    {
        if (!Uri.TryCreate(ModelUrlBox.Text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            ShowMessage("Enter an HTTP or HTTPS URL for a model package.");
            return;
        }

        try
        {
            await _modelManager.DownloadAndImportAsync(uri, CancellationToken.None);
            UpdateModelStatus();
            ShowMessage("The model package was downloaded, verified, and activated locally.");
        }
        catch (Exception exception)
        {
            ShowMessage($"The model download or update failed: {exception.Message}");
        }
    }

    private async void CancelJob_Click(object sender, RoutedEventArgs args)
    {
        if (JobsList.SelectedItem is not TranscriptionJob job)
        {
            ShowMessage("Select a transcription job to cancel.");
            return;
        }

        await _queue.CancelAsync(job.Id, CancellationToken.None);
        RefreshJobs();
    }

    private async void RetryJob_Click(object sender, RoutedEventArgs args)
    {
        var recording = (JobsList.SelectedItem as TranscriptionJob)?.Recording
                        ?? HistoryList.SelectedItem as SavedRecording;
        if (recording is null)
        {
            ShowMessage("Select a recording or job to transcribe again.");
            return;
        }

        var language = ((ComboBoxItem)LanguageCombo.SelectedItem).Content.ToString()!;
        await _queue.RetryAsync(recording with { Language = language }, CancellationToken.None);
        RefreshJobs();
    }

    private void RetryHistory_Click(object sender, RoutedEventArgs args)
    {
        RetryJob_Click(sender, args);
    }

    private async void ViewTranscript_Click(object sender, RoutedEventArgs args)
    {
        var job = JobsList.SelectedItem as TranscriptionJob;
        var path = job?.Outputs?.TextPath;
        if (path is null || !File.Exists(path))
        {
            ShowMessage("Select a successful transcription job.");
            return;
        }

        var text = await File.ReadAllTextAsync(path);
        var copy = System.Windows.MessageBox.Show(
            text,
            "Transcript",
            MessageBoxButton.OKCancel);
        if (copy == MessageBoxResult.OK)
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs args)
    {
        var recording = HistoryList.SelectedItem as SavedRecording;
        if (recording is not null && File.Exists(recording.AudioPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{recording.AudioPath}\"",
                UseShellExecute = true,
            });
            return;
        }

        if (recording is not null)
        {
            ShowMessage($"The recording file is missing: {recording.AudioPath}");
        }
    }

    private void TimerOnTick(object? sender, EventArgs args)
    {
        var duration = _recordingEngine.CapturedDuration;
        DurationText.Text = duration.ToString(@"hh\:mm\:ss");
        MicrophoneLevel.Value = _recordingEngine.MicrophoneLevel;
        SystemLevel.Value = _recordingEngine.SystemAudioLevel;
        if (!_limitWarningShown &&
            duration >= RecordingFormat.WarningCapturedDuration &&
            _recorder.VisibleStatus is "Recording" or "Paused")
        {
            _limitWarningShown = true;
            ShowMessage("The four-hour captured-duration limit is approaching.");
            _notifyIcon.ShowBalloonTip(
                5000,
                "Recording limit approaching",
                "The recording will save normally at four hours of captured audio.",
                ToolTipIcon.Warning);
        }

        StatusText.Text = _recorder.VisibleStatus;
        RefreshJobs();
        UpdateControls();
    }

    private async void RecordingEngineOnRecordingCompleted(
        object? sender,
        SavedRecording recording)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            await _recorder.AcceptCompletedRecordingAsync(recording);
            PermissionCheckBox.IsChecked = false;
            await RefreshHistoryAsync();
            ShowMessage($"Recording saved at the four-hour limit: {recording.AudioPath}");
        });
    }

    private async Task OfferRecoveryAsync()
    {
        foreach (var candidate in await _recovery.FindAsync())
        {
            var answer = System.Windows.MessageBox.Show(
                $"Recover available audio from the recording last saved at {candidate.LastSavedAt}?",
                "Recover interrupted recording",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                continue;
            }

            var result = await _recovery.RecoverAsync(candidate);
            if (result.Recording is not null)
            {
                await _history.AddAsync(result.Recording, CancellationToken.None);
                ShowMessage(
                    $"Recovered audio was saved as a recovered recording. Transcription requires an explicit request: {result.Recording.AudioPath}");
            }
            else
            {
                ShowMessage(result.Error!);
            }
        }

        await RefreshHistoryAsync();
    }

    private async Task RefreshHistoryAsync()
    {
        var recordings = await _history.LoadAsync(CancellationToken.None);
        HistoryList.ItemsSource = recordings
            .OrderByDescending(recording => recording.Id)
            .ToArray();
        var missing = recordings.FirstOrDefault(record => !File.Exists(record.AudioPath));
        if (missing is not null)
        {
            ShowMessage($"The recorded audio file is missing: {missing.AudioPath}");
        }
    }

    private void RefreshJobs()
    {
        JobsList.ItemsSource = _queue.Jobs.ToArray();
    }

    private void UpdateModelStatus()
    {
        var package = _modelManager.CurrentPackage;
        ModelStatusText.Text = package is null
            ? "No compatible model is installed. Recordings remain available for later transcription."
            : $"Active model: {package.Id} ({string.Join(", ", package.Languages)})";
    }

    private void UpdateControls()
    {
        var idle = _recorder.VisibleStatus == "Idle";
        StartButton.IsEnabled = idle;
        PauseButton.IsEnabled = _recorder.VisibleStatus == "Recording";
        ResumeButton.IsEnabled = _recorder.VisibleStatus == "Paused";
        StopButton.IsEnabled = !idle;
        SourceCombo.IsEnabled = idle;
        MicrophoneCombo.IsEnabled = idle && BuildRequest().Source != AudioSource.SystemAudioOnly;
        PlaybackCombo.IsEnabled = idle && BuildRequest().Source != AudioSource.MicrophoneOnly;
        PermissionCheckBox.IsEnabled = idle;
    }

    private NotifyIcon CreateNotifyIcon()
    {
        var icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Sound Recorder",
            Visible = true,
        };
        icon.DoubleClick += (_, _) => RestoreFromNotificationArea();
        icon.ContextMenuStrip = new ContextMenuStrip();
        icon.ContextMenuStrip.Items.Add("Open Sound Recorder", null, (_, _) => RestoreFromNotificationArea());
        icon.ContextMenuStrip.Items.Add("Exit", null, async (_, _) => await RequestExitAsync());
        return icon;
    }

    private void RestoreFromNotificationArea()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void WarnIfSynchronizedFolder()
    {
        var path = Path.GetFullPath(DestinationBox.Text.Trim());
        var synchronizedRoots = new[]
            {
                Environment.GetEnvironmentVariable("OneDrive"),
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Environment.GetEnvironmentVariable("Dropbox"),
            }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToArray();
        if (LocalPathPolicy.IsKnownSynchronizedFolder(path, synchronizedRoots))
        {
            ShowMessage(PrivacyPolicy.CloudSynchronizationWarning);
        }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
    }

    private async Task RequestExitAsync()
    {
        if (_recorder.VisibleStatus == "Idle" &&
            _queue.Jobs.All(job => job.Status != TranscriptionJobStatus.Running))
        {
            _allowClose = true;
            Close();
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            "Stop active recording or transcription work and exit? " +
            "Queued jobs and available captured audio will be preserved.",
            "Exit Sound Recorder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (_recorder.VisibleStatus != "Idle")
        {
            await StopRecordingAsync(enqueueTranscription: false);
        }

        foreach (var running in _queue.Jobs.Where(
                     job => job.Status == TranscriptionJobStatus.Running))
        {
            await _queue.CancelAsync(running.Id, CancellationToken.None);
        }

        _allowClose = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs args)
    {
        if (_allowClose)
        {
            _notifyIcon.Dispose();
            _recordingEngine.Dispose();
            Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionEnding -= SystemEventsOnSessionEnding;
            base.OnClosing(args);
            return;
        }

        if (_recorder.VisibleStatus is "Recording" or "Paused" ||
            _queue.Jobs.Any(job => job.Status == TranscriptionJobStatus.Running))
        {
            args.Cancel = true;
            var answer = System.Windows.MessageBox.Show(
                "Keep running in the notification area, or stop active work and exit?",
                "Active work",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
            {
                Hide();
                return;
            }

            if (answer != MessageBoxResult.No)
            {
                return;
            }

            await StopActiveWorkAsync();
        }

        _allowClose = true;
        Close();
    }

    protected override void OnStateChanged(EventArgs args)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _notifyIcon.ShowBalloonTip(
                3000,
                "Sound Recorder is still running",
                _recorder.VisibleStatus,
                ToolTipIcon.Info);
        }

        base.OnStateChanged(args);
    }

    private async Task StopActiveWorkAsync()
    {
        if (_recorder.VisibleStatus != "Idle")
        {
            await StopRecordingAsync(enqueueTranscription: false);
        }

        foreach (var job in _queue.Jobs.Where(
                     job => job.Status == TranscriptionJobStatus.Running))
        {
            await _queue.CancelAsync(job.Id, CancellationToken.None);
        }
    }

    private async void SystemEventsOnPowerModeChanged(
        object sender,
        Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        if (args.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            await StopActiveWorkAsync();
            ShowMessage("Windows is suspending. Available audio was finalized and work was interrupted.");
        }
    }

    private async void SystemEventsOnSessionEnding(
        object sender,
        Microsoft.Win32.SessionEndingEventArgs args)
    {
        await StopActiveWorkAsync();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(
        ExecutionState esFlags);

    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }
}
