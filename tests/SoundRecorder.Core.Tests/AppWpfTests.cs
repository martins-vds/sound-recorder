using System.Windows;
using System.Windows.Threading;
using SoundRecorder.Desktop;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class AppWpfTests
{
    [Fact]
    public async Task Run_WorkerAndDesktop_PublishStartupAndFlushActualExitWithScopedFailurePolicies()
    {
        var applicationHarness = WpfApplicationHarness.Instance;
        try
        {
            await applicationHarness.InvokeAsync(async harness =>
            {
                Assert.Equal(1, harness.StartupEvents);
                Assert.Equal(1, harness.StartupConfigurations);
                Assert.Equal(new Uri("MainWindow.xaml", UriKind.Relative), harness.StartupUriAtPublication);
                Assert.IsType<Window>(harness.Application.MainWindow);
                Assert.False(harness.Application.MainWindow.IsVisible);
                harness.Reset();
                var app = harness.Application;
                try
                {
                    harness.ProcessFailures.Raise(new IOException("Before desktop initialization."));
                    Assert.Empty(harness.Factory.Logger.Exceptions);
                    Assert.False(await app.InitializeAsync(["--transcribe-worker"]));
                    Assert.Equal(ShutdownMode.OnExplicitShutdown, app.ShutdownMode);
                    Assert.Equal(7, Assert.Single(harness.Exits));
                    Assert.Equal(0, harness.Configurations);
                    Assert.Equal(1, harness.WorkerCalls);
                    harness.Exits.Clear();
                    Assert.True(await app.InitializeAsync([]));
                    Assert.Equal(new Uri("MainWindow.xaml", UriKind.Relative), app.StartupUri);
                    Assert.Equal(1, harness.Configurations);
                    Assert.Equal(1, harness.WorkerCalls);
                    var fatal = new IOException("Controlled fatal diagnostic.");
                    harness.ProcessFailures.Raise(fatal);
                    Assert.Same(fatal, Assert.Single(harness.Factory.Logger.Exceptions.OfType<Exception>()));
                    Assert.Empty(harness.Exits);
                    Assert.Empty(harness.Dialogs.Prompts);
                    var error = new IOException("dispatcher failure");
                    var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    DispatcherUnhandledExceptionEventHandler guard = (_, args) =>
                    {
                        handled.TrySetResult(args.Handled);
                        args.Handled = true;
                    };
                    app.Dispatcher.UnhandledException += guard;
                    try
                    {
                        _ = app.Dispatcher.BeginInvoke(new Action(() => throw error));
                        Assert.True(await handled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
                        Assert.Equal(1, Assert.Single(harness.Exits));
                        Assert.Same(error, harness.Factory.Logger.Exceptions.Last());
                        Assert.Equal("Sound Recorder encountered an unexpected error.\n\nDiagnostics: isolated-log.jsonl",
                            Assert.Single(harness.Dialogs.Prompts).Message);
                        app.CompleteExit(13);
                        Assert.Equal(1, harness.Factory.Disposals);
                        Assert.Equal("Application exited with code 13", harness.Factory.Logger.Entries.Last().Message);
                        var logged = harness.Factory.Logger.Exceptions.Count;
                        harness.ProcessFailures.Raise(new IOException("A failure after the desktop lifetime."));
                        Assert.Equal(logged, harness.Factory.Logger.Exceptions.Count);
                        harness.Exits.Clear();
                        handled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _ = app.Dispatcher.BeginInvoke(new Action(() => throw new IOException("after exit")));
                        Assert.False(await handled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
                        Assert.Empty(harness.Exits);
                    }
                    finally
                    {
                        app.Dispatcher.UnhandledException -= guard;
                    }
                }
                finally
                {
                    app.CompleteExit(0);
                }
            });
            await DesktopNativeAdapterTests.VerifyApplicationTaskFailureLifetimeAsync(applicationHarness);
        }
        finally
        {
            applicationHarness.CompleteApplicationRun();
        }
    }
}

internal sealed class WpfApplicationHarness
{
    private static readonly Lazy<WpfApplicationHarness> Current = new(() => new());
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    public static WpfApplicationHarness Instance => Current.Value;
    public App Application { get; private set; } = null!;
    public DesktopDialogs Dialogs { get; private set; } = new();
    public AppLoggerFactory Factory { get; private set; } = new();
    public List<int> Exits { get; } = [];
    public AppProcessFailureEvents ProcessFailures { get; } = new();
    public int Configurations { get; private set; }
    public int WorkerCalls { get; private set; }
    public int StartupEvents { get; private set; }
    public int StartupConfigurations { get; private set; }
    public Uri? StartupUriAtPublication { get; private set; }

    private WpfApplicationHarness()
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Application = new HeadlessApplication(_ => new ApplicationLifecycle(
                new ForwardingDialogs(this), Exits.Add,
                (_, _) => { WorkerCalls++; return Task.FromResult(7); },
                () => { Configurations++; return (Factory, "isolated-log.jsonl"); }), ProcessFailures);
            Application.Startup += (_, _) =>
            {
                StartupEvents++;
                StartupConfigurations = Configurations;
                StartupUriAtPublication = Application.StartupUri;
            };
            _ = Application.Dispatcher.BeginInvoke(new Action(() =>
            {
                Application.MainWindow.Hide();
                Application.CompleteExit(0);
                _ready.TrySetResult();
            }), DispatcherPriority.ApplicationIdle);
            Application.Run(new Window
            {
                Title = "Owned application lifecycle",
                Width = 1,
                Height = 1,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            });
        })
        { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Reset()
    {
        Dialogs = new();
        Factory = new();
        Configurations = 0;
        WorkerCalls = 0;
        Exits.Clear();
    }

    public async Task InvokeAsync(Func<WpfApplicationHarness, Task> action)
    {
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Application.Dispatcher.InvokeAsync(() => action(this)).Task.Unwrap()
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void CompleteApplicationRun()
    {
        Exception? initializationFailure = null;
        InvokeAsync(async harness =>
        {
            try
            {
                harness.Application.CompleteExit(0);
                harness.Reset();
                Assert.True(await harness.Application.InitializeAsync([]));
            }
            catch (Exception exception)
            {
                initializationFailure = exception;
            }
            finally
            {
                harness.Application.Shutdown(23);
            }
        }).GetAwaiter().GetResult();
        Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The owned WPF application did not exit.");
        Assert.Null(initializationFailure);
        Assert.Equal(1, Factory.Disposals);
        Assert.Contains(Factory.Logger.Entries, entry => entry.Message == "Application exited with code 23");
    }

    private sealed class ForwardingDialogs(WpfApplicationHarness owner) : IDesktopDialogs
    {
        public string? ChooseDestination() => owner.Dialogs.ChooseDestination();
        public string? ChooseModelPackage() => owner.Dialogs.ChooseModelPackage();
        public MessageBoxResult Show(string message, string title, MessageBoxButton buttons,
            MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
            owner.Dialogs.Show(message, title, buttons, image, defaultResult);
    }

    private sealed class HeadlessApplication(Func<Action<int>, ApplicationLifecycle> lifecycle,
        IProcessFailureEvents processFailures) : App(lifecycle, processFailures)
    {
        protected override void OnStartup(StartupEventArgs args)
        {
            try
            {
                base.OnStartup(args);
            }
            finally
            {
                // Application.Run still exercises App.OnStartup; only automatic production-window creation is prevented.
                StartupUri = null;
            }
        }
    }
}

internal sealed class AppProcessFailureEvents : IProcessFailureEvents
{
    public event UnhandledExceptionEventHandler? UnhandledException;
    public void Raise(Exception exception) =>
        UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(exception, isTerminating: true));
}
