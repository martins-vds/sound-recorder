using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SoundRecorder.Desktop;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class DesktopNativeAdapterTests
{
    [Theory]
    [InlineData("power")]
    [InlineData("session")]
    [InlineData("capture")]
    public void LifecycleEventAdapter_SubscribeAndUnsubscribe_OwnsAndReleasesListener(string signal)
    {
        using var context = new DesktopTestContext();
        using var engine = new WindowsRecordingEngine(context.Directory);
        var events = new WindowsDesktopLifecycleEvents(engine);
        Action<NativeListener> subscribe = signal switch
        {
            "power" => listener => events.PowerModeChanged += listener.Power,
            "session" => listener => events.SessionEnding += listener.Session,
            _ => listener => events.RecordingCompleted += listener.Completed,
        };
        Action<NativeListener> unsubscribe = signal switch
        {
            "power" => listener => events.PowerModeChanged -= listener.Power,
            "session" => listener => events.SessionEnding -= listener.Session,
            _ => listener => events.RecordingCompleted -= listener.Completed,
        };
        var reference = Subscribe(subscribe);
        try
        {
            Collect();
            Assert.True(IsAlive(reference));
            Unsubscribe(reference, unsubscribe);
            Collect();
            Assert.False(IsAlive(reference));
        }
        finally
        {
            if (reference.TryGetTarget(out var listener))
            {
                SystemEvents.PowerModeChanged -= listener.Power;
                SystemEvents.SessionEnding -= listener.Session;
                engine.RecordingCompleted -= listener.Completed;
            }
        }
    }

    [Fact]
    public Task NotificationEventAdapter_SubscribeAndUnsubscribe_OwnsAndReleasesListener() =>
        MainWindowTests.OnStaAsync(() =>
        {
            using var native = new NotifyIcon();
            using var adapter = new WindowsNotificationArea(native);
            var reference = Subscribe(listener => adapter.DoubleClick += listener.DoubleClick);
            try
            {
                Collect();
                Assert.True(IsAlive(reference));
                Unsubscribe(reference, listener => adapter.DoubleClick -= listener.DoubleClick);
                Collect();
                Assert.False(IsAlive(reference));
            }
            finally
            {
                if (reference.TryGetTarget(out var listener)) native.DoubleClick -= listener.DoubleClick;
            }
            Assert.False(native.Visible);
            return Task.CompletedTask;
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<NativeListener> Subscribe(Action<NativeListener> subscribe)
    {
        var listener = new NativeListener();
        subscribe(listener);
        return new(listener);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unsubscribe(WeakReference<NativeListener> reference, Action<NativeListener> unsubscribe)
    {
        Assert.True(reference.TryGetTarget(out var listener));
        unsubscribe(listener);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<NativeListener> reference) => reference.TryGetTarget(out _);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class NativeListener
    {
        public void Power(object sender, PowerModeChangedEventArgs args) { }
        public void Session(object sender, SessionEndingEventArgs args) { }
        public void Completed(object? sender, SavedRecording recording) { }
        public void DoubleClick(object? sender, EventArgs args) { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public Task ChooseDestination_DialogResult_ReturnsOnlyConfirmedSingleFolder(bool? answer) =>
        MainWindowTests.OnStaAsync(() =>
        {
            var dialogs = new WindowsDesktopDialogs(dialog =>
            {
                var folder = Assert.IsType<OpenFolderDialog>(dialog);
                Assert.False(folder.Multiselect);
                folder.FolderName = @"C:\Selected destination";
                return answer;
            }, (_, _, _, _, _) => throw new InvalidOperationException());
            Assert.Equal(answer == true ? @"C:\Selected destination" : null, dialogs.ChooseDestination());
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public Task ChooseModelPackage_DialogResult_ReturnsOnlyConfirmedZipPackage(bool? answer) =>
        MainWindowTests.OnStaAsync(() =>
        {
            var dialogs = new WindowsDesktopDialogs(dialog =>
            {
                var file = Assert.IsType<Microsoft.Win32.OpenFileDialog>(dialog);
                Assert.Equal("Sound Recorder model package (*.zip)|*.zip", file.Filter);
                file.FileName = @"C:\Selected model.zip";
                return answer;
            }, (_, _, _, _, _) => throw new InvalidOperationException());
            Assert.Equal(answer == true ? @"C:\Selected model.zip" : null, dialogs.ChooseModelPackage());
            return Task.CompletedTask;
        });

    [Fact]
    public void Show_DialogConfiguration_ForwardsQuestionAndChoiceUnchanged()
    {
        var dialogs = new WindowsDesktopDialogs(_ => throw new InvalidOperationException(),
            (text, title, buttons, image, defaultResult) =>
            {
                Assert.Equal("Stop work?", text);
                Assert.Equal("Confirm exit", title);
                Assert.Equal(MessageBoxButton.YesNoCancel, buttons);
                Assert.Equal(MessageBoxImage.Question, image);
                Assert.Equal(MessageBoxResult.No, defaultResult);
                return MessageBoxResult.Cancel;
            });
        Assert.Equal(MessageBoxResult.Cancel, dialogs.Show("Stop work?", "Confirm exit",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No));
    }

    [Fact]
    public Task Environment_NotificationActions_ConfigureAndRouteWithoutNativeDesktopEffects() =>
        MainWindowTests.OnStaAsync(() =>
        {
            var icon = new DesktopNotificationArea();
            var wake = new List<WindowsDesktopEnvironment.ExecutionState>();
            var copies = new List<string>();
            var processes = new List<ProcessStartInfo>();
            using (var environment = new WindowsDesktopEnvironment(
                icon, flags => { wake.Add(flags); return flags; }, copies.Add,
                info => { processes.Add(info); return null; }))
            {
                Assert.Same(SystemIcons.Application, icon.Icon);
                Assert.Equal("Sound Recorder", icon.Text);
                Assert.True(icon.Visible);
                Assert.NotNull(icon.ContextMenuStrip);
                Assert.Equal(["Open Sound Recorder", "Exit"], icon.ContextMenuStrip.Items
                    .Cast<ToolStripItem>().Select(item => item.Text));
                var opened = 0;
                var exited = 0;
                environment.OpenRequested += (_, _) => opened++;
                environment.ExitRequested += (_, _) => exited++;
                icon.RaiseDoubleClick();
                icon.ContextMenuStrip.Items[0].PerformClick();
                icon.ContextMenuStrip.Items[1].PerformClick();
                Assert.Equal(2, opened);
                Assert.Equal(1, exited);
                environment.SetNotificationText("Sound Recorder - recording");
                Assert.Equal("Sound Recorder - recording", icon.Text);
                environment.ShowBalloonTip(5000, "Limit", "Saved at four hours", ToolTipIcon.Warning);
                Assert.Equal((5000, "Limit", "Saved at four hours", ToolTipIcon.Warning),
                    Assert.Single(icon.Balloons));
                environment.PreventIdleSleep(true);
                environment.PreventIdleSleep(false);
                Assert.Equal(new[]
                {
                    WindowsDesktopEnvironment.ExecutionState.Continuous |
                    WindowsDesktopEnvironment.ExecutionState.SystemRequired,
                    WindowsDesktopEnvironment.ExecutionState.Continuous,
                }, wake);
                environment.CopyTranscript("Local content");
                Assert.Equal("Local content", Assert.Single(copies));
                environment.OpenRecordingFolder(@"C:\Audio\meeting one.wav");
                var process = Assert.Single(processes);
                Assert.Equal("explorer.exe", process.FileName);
                Assert.Equal("/select,\"C:\\Audio\\meeting one.wav\"", process.Arguments);
                Assert.True(process.UseShellExecute);
            }
            Assert.Equal(1, icon.Disposals);
            return Task.CompletedTask;
        });

    [Fact]
    public Task WindowsNotificationArea_UnshownIcon_KeepsNativeStateWithoutPublishingIt() =>
        MainWindowTests.OnStaAsync(() =>
        {
            using var icon = new WindowsNotificationArea();
            using var menu = new ContextMenuStrip();
            Assert.False(icon.Visible);
            icon.Text = "Sound Recorder";
            icon.Icon = SystemIcons.Application;
            icon.ContextMenuStrip = menu;
            Assert.Equal("Sound Recorder", icon.Text);
            Assert.Same(SystemIcons.Application, icon.Icon);
            Assert.Same(menu, icon.ContextMenuStrip);
            icon.Visible = false;
            Assert.False(icon.Visible);
            return Task.CompletedTask;
        });

    [Fact]
    public void ApplicationDiagnostics_IsolatedApplicationRoot_WritesInformationAndFlushesOnExit()
    {
        using var context = new DesktopTestContext();
        var lifecycle = new ApplicationLifecycle(new DesktopDialogs(),
            _ => throw new InvalidOperationException(), context.Directory);
        lifecycle.InitializeDesktop();
        var logger = lifecycle.LoggerFactory!.CreateLogger("diagnostic-test");
        logger.LogDebug("Debug is below the application minimum");
        logger.LogInformation("Local operation completed");
        lifecycle.CompleteExit(0);
        Assert.StartsWith(Path.Combine(context.Directory, "SoundRecorder", "logs"),
            lifecycle.LogFilePath);
        var text = File.ReadAllText(lifecycle.LogFilePath!);
        Assert.Contains("Application starting", text);
        Assert.Contains("Local operation completed", text);
        Assert.Contains("Application exited with code 0", text);
        Assert.DoesNotContain("Debug is below", text);
    }
}

internal sealed class DesktopNotificationArea : INotificationArea
{
    public Icon? Icon { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Visible { get; set; }
    public ContextMenuStrip? ContextMenuStrip { get; set; }
    public event EventHandler? DoubleClick;
    public int Disposals { get; private set; }
    public List<(int, string, string, ToolTipIcon)> Balloons { get; } = [];
    public void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon) =>
        Balloons.Add((milliseconds, title, message, icon));
    public void Dispose() => Disposals++;
    public void RaiseDoubleClick() => DoubleClick?.Invoke(this, EventArgs.Empty);
}
