using System.Windows;
using System.Windows.Threading;
using SoundRecorder.Desktop;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class AppWpfTests
{
    [Fact]
    public Task InitializeAsync_WorkerAndDesktop_KeepWorkerHeadlessAndAttachFatalErrorPolicy() =>
        WpfApplicationHarness.Instance.InvokeAsync(async harness =>
        {
            harness.Reset();
            var app = harness.Application;
            try
            {
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
}

internal sealed class WpfApplicationHarness
{
    private static readonly Lazy<WpfApplicationHarness> Current = new(() => new());
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static WpfApplicationHarness Instance => Current.Value;
    public App Application { get; private set; } = null!;
    public DesktopDialogs Dialogs { get; private set; } = new();
    public AppLoggerFactory Factory { get; private set; } = new();
    public List<int> Exits { get; } = [];
    public int Configurations { get; private set; }
    public int WorkerCalls { get; private set; }

    private WpfApplicationHarness()
    {
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Application = new HeadlessApplication(_ => new ApplicationLifecycle(
                new ForwardingDialogs(this), Exits.Add,
                (_, _) => { WorkerCalls++; return Task.FromResult(7); },
                () => { Configurations++; return (Factory, "isolated-log.jsonl"); }));
            _ready.SetResult();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
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

    private sealed class ForwardingDialogs(WpfApplicationHarness owner) : IDesktopDialogs
    {
        public string? ChooseDestination() => owner.Dialogs.ChooseDestination();
        public string? ChooseModelPackage() => owner.Dialogs.ChooseModelPackage();
        public MessageBoxResult Show(string message, string title, MessageBoxButton buttons,
            MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
            owner.Dialogs.Show(message, title, buttons, image, defaultResult);
    }

    private sealed class HeadlessApplication(Func<Action<int>, ApplicationLifecycle> lifecycle) : App(lifecycle)
    {
        protected override void OnStartup(StartupEventArgs args) { }
    }
}
