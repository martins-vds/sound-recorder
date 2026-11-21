using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundRecorder.Desktop;

/// <summary>Native effects; all decisions remain in the window's application actions.</summary>
public sealed class WindowsDesktopEnvironment : IDesktopEnvironment
{
    private readonly INotificationArea _icon;
    private readonly Func<ExecutionState, ExecutionState> _setExecutionState;
    private readonly Action<string> _copyTranscript;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public WindowsDesktopEnvironment()
        : this(new WindowsNotificationArea(), SetThreadExecutionState,
            System.Windows.Clipboard.SetText, Process.Start)
    {
    }

    /// <summary>Configures notification status while keeping native process, clipboard, and power effects replaceable.</summary>
    public WindowsDesktopEnvironment(
        INotificationArea icon,
        Func<ExecutionState, ExecutionState> setExecutionState,
        Action<string> copyTranscript,
        Func<ProcessStartInfo, Process?> startProcess)
    {
        _icon = icon;
        _setExecutionState = setExecutionState;
        _copyTranscript = copyTranscript;
        _startProcess = startProcess;
        _icon.Icon = System.Drawing.SystemIcons.Application;
        _icon.Text = "Sound Recorder";
        _icon.Visible = true;
        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.ContextMenuStrip.Items.Add("Open Sound Recorder", null,
            (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip.Items.Add("Exit", null,
            (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public void SetNotificationText(string text) => _icon.Text = text;

    public void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(milliseconds, title, message, icon);

    public void PreventIdleSleep(bool prevent) =>
        _setExecutionState(prevent ? ExecutionState.Continuous | ExecutionState.SystemRequired :
            ExecutionState.Continuous);

    public void CopyTranscript(string text) => _copyTranscript(text);

    public void OpenRecordingFolder(string audioPath) =>
        _startProcess(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{audioPath}\"",
            UseShellExecute = true,
        });

    public void Dispose() => _icon.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [Flags]
    public enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }
}
