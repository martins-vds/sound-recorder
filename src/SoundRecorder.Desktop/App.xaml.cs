using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SoundRecorder.Infrastructure.Diagnostics;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Desktop;

public partial class App : System.Windows.Application
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;

    internal static ILoggerFactory? LoggerFactory { get; private set; }

    internal static string? LogFilePath { get; private set; }

    protected override async void OnStartup(System.Windows.StartupEventArgs args)
    {
        if (args.Args is ["--transcribe-worker"])
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            var exitCode = await WhisperProcessTranscriber.RunWorkerAsync(
                Console.In,
                Console.Out);
            Shutdown(exitCode);
            return;
        }

        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundRecorder");
        var provider = new JsonFileLoggerProvider(Path.Combine(root, "logs"));
        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(provider);
        });
        LoggerFactory = _loggerFactory;
        LogFilePath = provider.FilePath;
        _logger = _loggerFactory.CreateLogger<App>();
        DesktopLog.ApplicationStarting(_logger);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Exit += OnExit;
        base.OnStartup(args);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        if (_logger is not null)
        {
            DesktopLog.DispatcherUnhandledException(_logger, args.Exception);
        }
        args.Handled = true;
        System.Windows.MessageBox.Show(
            $"Sound Recorder encountered an unexpected error.\n\nDiagnostics: {LogFilePath}",
            "Sound Recorder",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            if (_logger is not null)
            {
                DesktopLog.ProcessUnhandledException(_logger, exception);
            }
        }
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        if (_logger is not null)
        {
            DesktopLog.UnobservedTaskException(_logger, args.Exception);
        }
        args.SetObserved();
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs args)
    {
        if (_logger is not null)
        {
            DesktopLog.ApplicationExited(_logger, args.ApplicationExitCode);
        }
        _loggerFactory?.Dispose();
    }
}
