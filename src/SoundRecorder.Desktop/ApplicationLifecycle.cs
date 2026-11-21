using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using SoundRecorder.Infrastructure.Diagnostics;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Desktop;

/// <summary>Application startup, worker routing, diagnostic failure handling, and logging lifetime.</summary>
public sealed class ApplicationLifecycle
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<App>? _logger;
    private readonly IDesktopDialogs _dialogs;
    private readonly Action<int> _shutdown;
    private readonly Func<TextReader, TextWriter, Task<int>> _runWorker;
    private readonly Func<(ILoggerFactory Factory, string FilePath)> _configureLogging;

    public ApplicationLifecycle(IDesktopDialogs dialogs, Action<int> shutdown)
        : this(dialogs, shutdown,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    /// <summary>Uses the chosen local application-data root for process diagnostics.</summary>
    public ApplicationLifecycle(IDesktopDialogs dialogs, Action<int> shutdown, string localApplicationData)
        : this(dialogs, shutdown, WhisperProcessTranscriber.RunWorkerAsync,
            () => ConfigureLogging(localApplicationData))
    {
    }

    /// <summary>Connects application lifecycle to local worker, diagnostics, and process effects.</summary>
    public ApplicationLifecycle(
        IDesktopDialogs dialogs,
        Action<int> shutdown,
        Func<TextReader, TextWriter, Task<int>> runWorker,
        Func<(ILoggerFactory Factory, string FilePath)> configureLogging)
    {
        _dialogs = dialogs;
        _shutdown = shutdown;
        _runWorker = runWorker;
        _configureLogging = configureLogging;
    }

    public ILoggerFactory? LoggerFactory => _loggerFactory;

    public string? LogFilePath { get; private set; }

    public static bool IsWorkerInvocation(string[] arguments) =>
        arguments is ["--transcribe-worker"];

    /// <summary>Shares the local state root between diagnostics and desktop application services.</summary>
    public static string StateDirectory(string localApplicationData) =>
        Path.Combine(localApplicationData, "SoundRecorder");

    /// <summary>Runs a worker with process standard streams and propagates its exit code.</summary>
    public async Task RunWorkerAsync()
    {
        var exitCode = await _runWorker(Console.In, Console.Out);
        _shutdown(exitCode);
    }

    /// <summary>Configures local diagnostics before normal desktop startup.</summary>
    public void InitializeDesktop()
    {
        var logging = _configureLogging();
        _loggerFactory = logging.Factory;
        LogFilePath = logging.FilePath;
        _logger = _loggerFactory.CreateLogger<App>();
        DesktopLog.ApplicationStarting(_logger);
    }

    private static (ILoggerFactory Factory, string FilePath) ConfigureLogging(string localApplicationData)
    {
        var root = StateDirectory(localApplicationData);
        var provider = new JsonFileLoggerProvider(Path.Combine(root, "logs"));
        var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddProvider(provider);
        });
        return (factory, provider.FilePath);
    }

    public void LogDispatcherException(Exception exception)
    {
        if (_logger is not null)
        {
            DesktopLog.DispatcherUnhandledException(_logger, exception);
        }
    }

    /// <summary>Displays diagnostics guidance and terminates with a failure exit code.</summary>
    public void ShutdownAfterDispatcherException()
    {
        _dialogs.Show(
            $"Sound Recorder encountered an unexpected error.\n\nDiagnostics: {LogFilePath}",
            "Sound Recorder",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        _shutdown(1);
    }

    /// <summary>Records a fatal process exception without changing its runtime handling policy.</summary>
    public void ReportDomainException(object exceptionObject)
    {
        if (exceptionObject is Exception exception)
        {
            if (_logger is not null)
            {
                DesktopLog.ProcessUnhandledException(_logger, exception);
            }
        }
    }

    /// <summary>Logs and observes an otherwise unobserved background task exception.</summary>
    public void ObserveTaskException(UnobservedTaskExceptionEventArgs args)
    {
        if (_logger is not null)
        {
            DesktopLog.UnobservedTaskException(_logger, args.Exception);
        }
        args.SetObserved();
    }

    /// <summary>Records the process exit code and flushes the owned logging factory.</summary>
    public void CompleteExit(int exitCode)
    {
        if (_logger is not null)
        {
            DesktopLog.ApplicationExited(_logger, exitCode);
        }
        _loggerFactory?.Dispose();
    }
}
