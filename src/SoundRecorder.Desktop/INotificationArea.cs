using System.Drawing;
using System.Windows.Forms;

namespace SoundRecorder.Desktop;

/// <summary>Owns the native notification icon independently of recording and window policy.</summary>
public interface INotificationArea : IDisposable
{
    Icon? Icon { get; set; }
    string Text { get; set; }
    bool Visible { get; set; }
    ContextMenuStrip? ContextMenuStrip { get; set; }
    event EventHandler DoubleClick;
    void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon);
}
