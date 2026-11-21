using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
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
    private IRecordingMonitor _recordingEngine = null!;
    private IDisposable? _captureLifetime;
    private MainViewModel _viewModel = null!;
    private IDesktopEnvironment _environment = null!;
    private IDesktopDialogs _dialogs = null!;
    private DispatcherTimer _timer = null!;
    private ILogger<MainWindow> _logger = null!;
    private IDesktopLifecycleEvents? _lifecycleEvents;
    private WindowPresentationActions _presentation = null!;
    private bool _allowClose;
    private bool _limitWarningShown;
    private string? _previousStatus;

    public MainWindow()
        : this(
            ApplicationLifecycle.StateDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            App.LoggerFactory!,
            () => new WindowsDesktopDialogs(),
            () => new WindowsDesktopEnvironment(),
            engine => new WindowsDesktopLifecycleEvents(engine))
    {
    }

    /// <summary>Composes real local application services in a chosen state directory.</summary>
    public MainWindow(
        string stateDirectory,
        ILoggerFactory loggerFactory,
        Func<IDesktopDialogs> dialogs,
        Func<IDesktopEnvironment> environment,
        Func<WindowsRecordingEngine, IDesktopLifecycleEvents> lifecycleEvents,
        IAudioDeviceCatalog? devices = null,
        Func<string, ITranscriptionModelStore, ILoggerFactory, ILocalTranscriber>? transcriber = null)
    {
        try
        {
            InitializeComponent();
            _logger = loggerFactory.CreateLogger<MainWindow>();
            DesktopLog.MainWindowInitializing(_logger);
            InitializeServices(stateDirectory, loggerFactory, dialogs, environment, lifecycleEvents,
                devices ?? new WindowsAudioDeviceCatalog(), transcriber ?? CreateTranscriber);
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

    /// <summary>Creates a window with local application services and explicit native effect adapters.</summary>
    public MainWindow(
        MainViewModel viewModel,
        IRecordingMonitor recordingMonitor,
        IDesktopDialogs dialogs,
        IDesktopEnvironment environment,
        ILogger<MainWindow> logger,
        IDisposable? captureLifetime = null,
        IDesktopLifecycleEvents? lifecycleEvents = null,
        WindowPresentationActions? presentation = null,
        DispatcherTimer? timer = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _recordingEngine = recordingMonitor;
        _dialogs = dialogs;
        _environment = environment;
        _logger = logger;
        _captureLifetime = captureLifetime;
        _lifecycleEvents = lifecycleEvents;
        _presentation = presentation ?? new(Show, Hide, Activate);
        _timer = timer!;
        ConnectWindowServices();
    }

    private void InitializeServices(
        string root,
        ILoggerFactory loggerFactory,
        Func<IDesktopDialogs> dialogs,
        Func<IDesktopEnvironment> environment,
        Func<WindowsRecordingEngine, IDesktopLifecycleEvents> lifecycleEvents,
        IAudioDeviceCatalog devices,
        Func<string, ITranscriptionModelStore, ILoggerFactory, ILocalTranscriber> createTranscriber)
    {
        var recordingEngine = new WindowsRecordingEngine(
            Path.Combine(root, "capture"),
            loggerFactory.CreateLogger<WindowsRecordingEngine>());
        _recordingEngine = recordingEngine;
        _captureLifetime = recordingEngine;
        var modelManager = new LocalTranscriptionModelManager(
            Path.Combine(root, "models"),
            new HttpClient(),
            loggerFactory.CreateLogger<LocalTranscriptionModelManager>());
        var writer = new AtomicTranscriptWriter(
            loggerFactory.CreateLogger<AtomicTranscriptWriter>());
        var transcriber = createTranscriber(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.Desktop.exe"),
            modelManager,
            loggerFactory);
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
            recordingEngine,
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
            recordingEngine,
            queue,
            modelManager,
            history,
            recovery,
            devices,
            loggerFactory.CreateLogger<MainViewModel>());
        _dialogs = dialogs();
        _environment = environment();
        _lifecycleEvents = lifecycleEvents(recordingEngine);
        _presentation = new(Show, Hide, Activate);
        ConnectWindowServices();
    }

    private static ILocalTranscriber CreateTranscriber(
        string workerPath, ITranscriptionModelStore models, ILoggerFactory loggerFactory) =>
        new WhisperProcessTranscriber(workerPath, models,
            loggerFactory.CreateLogger<WhisperProcessTranscriber>());

    private void ConnectWindowServices()
    {
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _environment.OpenRequested += EnvironmentOnOpenRequested;
        _environment.ExitRequested += EnvironmentOnExitRequested;
        _previousStatus = _viewModel.Status;
        _timer ??= new DispatcherTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        if (_lifecycleEvents is not null)
        {
            _lifecycleEvents.RecordingCompleted += RecordingEngineOnRecordingCompleted;
            _lifecycleEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
            _lifecycleEvents.SessionEnding += SystemEventsOnSessionEnding;
        }
        _timer.Tick += TimerOnTick;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs args)
    {
        await LoadAsync();
    }

    /// <summary>Restores local state and offers recovery before starting periodic status refresh.</summary>
    public async Task LoadAsync()
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
        ChooseDestination();
    }

    /// <summary>Changes the output destination only after a folder has been selected.</summary>
    public void ChooseDestination()
    {
        var folder = _dialogs.ChooseDestination();
        if (folder is not null)
        {
            _viewModel.DestinationFolder = folder;
            WarnIfSynchronizedFolder();
        }
    }

    private async void ImportModel_Click(object sender, RoutedEventArgs args)
    {
        await ImportModelAsync();
    }

    /// <summary>Installs a model only after the user selects its local package.</summary>
    public async Task ImportModelAsync()
    {
        var package = _dialogs.ChooseModelPackage();
        if (package is null)
        {
            return;
        }

        await _viewModel.ImportModelCommand.ExecuteAsync(package);
    }

    private async void ViewTranscript_Click(object sender, RoutedEventArgs args)
    {
        await ViewTranscriptAsync();
    }

    /// <summary>Offers the selected transcript for viewing and explicit copying.</summary>
    public async Task ViewTranscriptAsync()
    {
        await _viewModel.ViewTranscriptCommand.ExecuteAsync(null);
        var text = _viewModel.Transcript;
        if (text is null)
        {
            return;
        }
        var copy = _dialogs.Show(
            text,
            "Transcript",
            MessageBoxButton.OKCancel);
        if (copy == MessageBoxResult.OK)
        {
            _environment.CopyTranscript(text);
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs args)
    {
        OpenOutputFolder(HistoryList.SelectedItem as SavedRecording);
    }

    /// <summary>Reveals an existing recording, reporting an externally missing file instead of recreating it.</summary>
    public void OpenOutputFolder(SavedRecording? recording)
    {
        if (recording is not null && File.Exists(recording.AudioPath))
        {
            _environment.OpenRecordingFolder(recording.AudioPath);
            return;
        }

        if (recording is not null)
        {
            ShowMessage($"The recording file is missing: {recording.AudioPath}");
        }
    }

    private void TimerOnTick(object? sender, EventArgs args)
    {
        RefreshStatus();
    }

    /// <summary>Refreshes capture status, one-shot duration warnings, notification status, and idle-sleep policy.</summary>
    public void RefreshStatus()
    {
        _viewModel.Refresh();
        if (_viewModel.Status == "Recording" && _previousStatus == "Idle")
        {
            _limitWarningShown = false;
        }

        _previousStatus = _viewModel.Status;
        WarnAboutDurationLimit();
        _environment.PreventIdleSleep(!_viewModel.IsIdle);
        _environment.SetNotificationText(_viewModel.IsIdle
            ? "Sound Recorder"
            : "Sound Recorder - recording");
    }

    private void WarnAboutDurationLimit()
    {
        if (_limitWarningShown ||
            _recordingEngine.CapturedDuration < RecordingFormat.WarningCapturedDuration ||
            _viewModel.Status is not ("Recording" or "Paused"))
        {
            return;
        }

        _limitWarningShown = true;
        ShowMessage("The four-hour captured-duration limit is approaching.");
        _environment.ShowBalloonTip(
            5000,
            "Recording limit approaching",
            "The recording will save normally at four hours of captured audio.",
            ToolTipIcon.Warning);
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

        await ConfirmShortRecordingAsync();
    }

    /// <summary>Consumes the low-space prompt and starts a shortened recording only after explicit approval.</summary>
    public async Task ConfirmShortRecordingAsync()
    {
        _viewModel.NeedsShortRecordingConsent = false;
        var approved = _dialogs.Show(
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
            await AcceptCompletedRecordingAsync(recording);
        });
    }

    /// <summary>Publishes a capture-engine completion on the window's application thread.</summary>
    public async Task AcceptCompletedRecordingAsync(SavedRecording recording)
    {
        await _viewModel.AcceptCompletedRecordingAsync(recording);
    }

    private async Task OfferRecoveryAsync()
    {
        foreach (var candidate in await _viewModel.FindRecoveryCandidatesAsync())
        {
            var answer = _dialogs.Show(
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

    private void EnvironmentOnOpenRequested(object? sender, EventArgs args) =>
        RestoreFromNotificationArea();

    private async void EnvironmentOnExitRequested(object? sender, EventArgs args) =>
        await RequestExitAsync();

    private void RestoreFromNotificationArea()
    {
        _presentation.Show();
        WindowState = WindowState.Normal;
        _presentation.Activate();
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

    /// <summary>Confirms stopping active work before closing, without queuing a new transcription.</summary>
    public async Task RequestExitAsync()
    {
        if (_viewModel.HasActiveWork())
        {
            var answer = _dialogs.Show(
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
        }
        _allowClose = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs args)
    {
        if (_allowClose || !_viewModel.HasActiveWork())
        {
            _allowClose = true;
            DisposeWindowServices();
            base.OnClosing(args);
            return;
        }

        args.Cancel = true;
        var answer = _dialogs.Show(
            "Keep running in the notification area, or stop active work and exit?",
            "Active work",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes)
        {
            _presentation.Hide();
        }
        else if (answer == MessageBoxResult.No)
        {
            await StopAndCloseAsync();
        }
    }

    private async Task StopAndCloseAsync()
    {
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
    }

    protected override void OnStateChanged(EventArgs args) => UpdateWindowState(args);

    /// <summary>Publishes minimization status without stopping capture or transcription.</summary>
    public void UpdateWindowState() => UpdateWindowState(EventArgs.Empty);

    /// <summary>Handles the window-state notification and preserves the WPF state-changed event.</summary>
    public void UpdateWindowState(EventArgs args)
    {
        if (WindowState == WindowState.Minimized)
        {
            _presentation.Hide();
            _environment.ShowBalloonTip(
                3000,
                "Sound Recorder is still running",
                _viewModel.Status,
                ToolTipIcon.Info);
        }
        base.OnStateChanged(args);
    }

    private void DisposeWindowServices()
    {
        _environment.Dispose();
        _captureLifetime?.Dispose();
        if (_lifecycleEvents is not null)
        {
            _lifecycleEvents.PowerModeChanged -= SystemEventsOnPowerModeChanged;
            _lifecycleEvents.SessionEnding -= SystemEventsOnSessionEnding;
        }
    }

    private async void SystemEventsOnPowerModeChanged(
        object sender,
        Microsoft.Win32.PowerModeChangedEventArgs args)
    {
        if (args.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            await SuspendAsync();
        }
    }

    /// <summary>Finalizes available audio and stops transcription on explicit Windows suspension.</summary>
    public async Task SuspendAsync()
    {
        await _viewModel.StopActiveWorkAsync();
        ShowMessage("Windows is suspending. Available audio was finalized and work was interrupted.");
    }

    private async void SystemEventsOnSessionEnding(
        object sender,
        Microsoft.Win32.SessionEndingEventArgs args)
    {
        await _viewModel.StopActiveWorkAsync();
    }

}
