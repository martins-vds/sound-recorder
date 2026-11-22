using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace SoundRecorder.Desktop;

public partial class App : System.Windows.Application
{
    private readonly ApplicationLifecycle _lifecycle;
    private readonly IProcessFailureEvents _processFailures;

    public App()
        : this(shutdown => new ApplicationLifecycle(new WindowsDesktopDialogs(), shutdown))
    {
    }

    /// <summary>Connects the WPF application to its process lifecycle without starting a window.</summary>
    public App(Func<Action<int>, ApplicationLifecycle> createLifecycle)
        : this(createLifecycle, null)
    {
    }

    /// <summary>Connects WPF to its lifecycle and optional process-failure source without starting a window.</summary>
    public App(Func<Action<int>, ApplicationLifecycle> createLifecycle, IProcessFailureEvents? processFailures)
    {
        _lifecycle = createLifecycle(Shutdown);
        _processFailures = processFailures ?? new WindowsProcessFailureEvents();
    }

    internal static ILoggerFactory? LoggerFactory { get; private set; }

    internal static string? LogFilePath { get; private set; }

    protected override async void OnStartup(System.Windows.StartupEventArgs args)
    {
        if (await InitializeAsync(args.Args))
        {
            base.OnStartup(args);
        }
    }

    /// <summary>Routes process startup before the WPF startup event is raised.</summary>
    public async Task<bool> InitializeAsync(string[] arguments)
    {
        if (ApplicationLifecycle.IsWorkerInvocation(arguments))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            await _lifecycle.RunWorkerAsync();
            return false;
        }

        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        _lifecycle.InitializeDesktop();
        LoggerFactory = _lifecycle.LoggerFactory;
        LogFilePath = _lifecycle.LogFilePath;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _processFailures.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Exit += OnExit;
        return true;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        _lifecycle.LogDispatcherException(args.Exception);
        args.Handled = true;
        _lifecycle.ShutdownAfterDispatcherException();
    }

    private void OnDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs args) =>
        _lifecycle.ReportDomainException(args.ExceptionObject);

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args) =>
        _lifecycle.ObserveTaskException(args);

    private void OnExit(object sender, System.Windows.ExitEventArgs args) =>
        CompleteExit(args.ApplicationExitCode);

    /// <summary>Releases process event subscriptions and flushes diagnostics on application exit.</summary>
    public void CompleteExit(int exitCode)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        _processFailures.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Exit -= OnExit;
        _lifecycle.CompleteExit(exitCode);
    }
}

/// <summary>Process-failure notifications without changing the runtime's fatal-error policy.</summary>
public interface IProcessFailureEvents
{
    event UnhandledExceptionEventHandler? UnhandledException;
}

/// <summary>Connects process-failure notifications to the current CLR application domain.</summary>
public sealed class WindowsProcessFailureEvents : IProcessFailureEvents
{
    public event UnhandledExceptionEventHandler? UnhandledException
    {
        add => AppDomain.CurrentDomain.UnhandledException += value;
        remove => AppDomain.CurrentDomain.UnhandledException -= value;
    }
}
