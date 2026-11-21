using System.Drawing;
using System.Windows.Forms;

namespace SoundRecorder.Desktop;

/// <summary>Direct notification-icon calls; configuration belongs to the desktop environment.</summary>
public sealed class WindowsNotificationArea : INotificationArea
{
    private readonly NotifyIcon _icon;
    public WindowsNotificationArea() : this(new NotifyIcon()) { }
    public WindowsNotificationArea(NotifyIcon icon) => _icon = icon;
    public Icon? Icon { get => _icon.Icon; set => _icon.Icon = value; }
    public string Text { get => _icon.Text; set => _icon.Text = value; }
    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }
    public ContextMenuStrip? ContextMenuStrip
    {
        get => _icon.ContextMenuStrip;
        set => _icon.ContextMenuStrip = value;
    }
    public event EventHandler DoubleClick
    {
        add => _icon.DoubleClick += value;
        remove => _icon.DoubleClick -= value;
    }
    public void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(milliseconds, title, message, icon);
    public void Dispose() => _icon.Dispose();
}
