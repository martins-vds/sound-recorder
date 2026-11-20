using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
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
    private WindowsRecordingEngine _recordingEngine = null!;
    private MainViewModel _viewModel = null!;
    private NotifyIcon _notifyIcon = null!;
    private DispatcherTimer _timer = null!;
    private ILogger<MainWindow> _logger = null!;
    private bool _allowClose;
    private bool _limitWarningShown;
    private string _previousStatus = "Idle";

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            _logger = App.LoggerFactory!.CreateLogger<MainWindow>();
            DesktopLog.MainWindowInitializing(_logger);
            InitializeServices();
        }
        catch (Exception exception)
        {
            if (_logger is not null)
            {
                DesktopLog.MainWindowInitializationFailed(_logger, exception);
            }
            throw;
        }
    }

    private void InitializeServices()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundRecorder");
        Directory.CreateDirectory(root);
        var loggerFactory = App.LoggerFactory!;
        _recordingEngine = new WindowsRecordingEngine(
            Path.Combine(root, "capture"),
            loggerFactory.CreateLogger<WindowsRecordingEngine>());
        var modelManager = new LocalTranscriptionModelManager(
            Path.Combine(root, "models"),
            new HttpClient(),
            loggerFactory.CreateLogger<LocalTranscriptionModelManager>());
        var writer = new AtomicTranscriptWriter(
            loggerFactory.CreateLogger<AtomicTranscriptWriter>());
        var transcriber = new WhisperProcessTranscriber(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.Desktop.exe"),
            modelManager,
            loggerFactory.CreateLogger<WhisperProcessTranscriber>());
        var jobRepository = new JsonTranscriptionJobRepository(
            Path.Combine(root, "jobs.json"),
            loggerFactory.CreateLogger<JsonTranscriptionJobRepository>());
        var queue = new TranscriptionQueue(
            transcriber,
            writer,
            jobRepository,
            loggerFactory.CreateLogger<TranscriptionQueue>());
        var history = new JsonRecordingHistory(
            Path.Combine(root, "history.json"),
            loggerFactory.CreateLogger<JsonRecordingHistory>());
        var recorder = new RecorderApplication(
            _recordingEngine,
            queue,
            modelManager,
            history,
            loggerFactory.CreateLogger<RecorderApplication>());
        var recovery = new RecoveryManager(
            new LocalRecoveryStorage(
                Path.Combine(root, "capture"),
                loggerFactory.CreateLogger<LocalRecoveryStorage>()),
            loggerFactory.CreateLogger<RecoveryManager>());
        _viewModel = new MainViewModel(
            recorder,
            _recordingEngine,
            queue,
            modelManager,
            history,
            recovery,
            new WindowsAudioDeviceCatalog(),
            loggerFactory.CreateLogger<MainViewModel>());
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
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
        try
        {
            await _viewModel.InitializeCommand.ExecuteAsync(null);
            await OfferRecoveryAsync();
            _timer.Start();
            DesktopLog.MainWindowReady(_logger);
        }
        catch (Exception exception)
        {
            DesktopLog.MainWindowLoadFailed(_logger, exception);
            _timer.Start();
            ShowMessage(
                $"Some local state could not be loaded. The app remains available; see {App.LogFilePath}");
        }
    }

    private void ChooseDestination_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.DestinationFolder = dialog.FolderName;
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

        await _viewModel.ImportModelCommand.ExecuteAsync(dialog.FileName);
    }

    private async void ViewTranscript_Click(object sender, RoutedEventArgs args)
    {
        await _viewModel.ViewTranscriptCommand.ExecuteAsync(null);
        var text = _viewModel.Transcript;
        if (text is null)
        {
            return;
        }
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
        _viewModel.Refresh();
        if (_viewModel.Status == "Recording" && _previousStatus == "Idle")
        {
            _limitWarningShown = false;
        }

        _previousStatus = _viewModel.Status;
        if (!_limitWarningShown &&
            _recordingEngine.CapturedDuration >= RecordingFormat.WarningCapturedDuration &&
            _viewModel.Status is "Recording" or "Paused")
        {
            _limitWarningShown = true;
            ShowMessage("The four-hour captured-duration limit is approaching.");
            _notifyIcon.ShowBalloonTip(
                5000,
                "Recording limit approaching",
                "The recording will save normally at four hours of captured audio.",
                ToolTipIcon.Warning);
        }

        SetThreadExecutionState(
            _viewModel.IsIdle
                ? ExecutionState.Continuous
                : ExecutionState.Continuous | ExecutionState.SystemRequired);
        _notifyIcon.Text = _viewModel.IsIdle
            ? "Sound Recorder"
            : "Sound Recorder - recording";
    }

    private async void ViewModelOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.NeedsShortRecordingConsent) ||
            !_viewModel.NeedsShortRecordingConsent)
        {
            return;
        }

        _viewModel.NeedsShortRecordingConsent = false;
        var approved = System.Windows.MessageBox.Show(
            _viewModel.Message +
            "\n\nProceed with a shorter recording when free space runs out?",
            "Insufficient space for maximum duration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (approved)
        {
            await _viewModel.StartRecordingAsync(allowShortRecording: true);
        }
    }

    private async void RecordingEngineOnRecordingCompleted(
        object? sender,
        SavedRecording recording)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            await _viewModel.AcceptCompletedRecordingAsync(recording);
            ShowMessage(_viewModel.Message);
        });
    }

    private async Task OfferRecoveryAsync()
    {
        foreach (var candidate in await _viewModel.FindRecoveryCandidatesAsync())
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

            await _viewModel.RecoverAsync(candidate);
        }
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
        var path = Path.GetFullPath(_viewModel.DestinationFolder);
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
        _viewModel.Message = message;
    }

    private async Task RequestExitAsync()
    {
        if (!_viewModel.HasActiveWork())
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

        await _viewModel.StopActiveWorkAsync();

        _allowClose = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs args)
    {
        if (_allowClose)
        {
            DisposeWindowServices();
            base.OnClosing(args);
            return;
        }

        if (_viewModel.HasActiveWork())
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

            args.Cancel = true;
            try
            {
                await _viewModel.StopActiveWorkAsync();
            }
            catch (Exception exception)
            {
                DesktopLog.RecordingStopFailed(_logger, exception);
            }
            finally
            {
                _allowClose = true;
                Close();
            }
            return;
        }

        _allowClose = true;
        DisposeWindowServices();
        base.OnClosing(args);
    }

    protected override void OnStateChanged(EventArgs args)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _notifyIcon.ShowBalloonTip(
                3000,
                "Sound Recorder is still running",
                _viewModel.Status,
                ToolTipIcon.Info);
        }

        base.OnStateChanged(args);
    }

    private void DisposeWindowServices()
    {
        _notifyIcon.Dispose();
        _recordingEngine.Dispose();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionEnding -= SystemEventsOnSessionEnding;
    }

    private async void SystemEventsOnPowerModeChanged(
        object sender,
        Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        if (args.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            await _viewModel.StopActiveWorkAsync();
            ShowMessage("Windows is suspending. Available audio was finalized and work was interrupted.");
        }
    }

    private async void SystemEventsOnSessionEnding(
        object sender,
        Microsoft.Win32.SessionEndingEventArgs args)
    {
        await _viewModel.StopActiveWorkAsync();
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
