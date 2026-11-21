using System.Windows.Forms;

namespace SoundRecorder.Desktop;

/// <summary>Windows notification-area, clipboard, Explorer, and idle-sleep effects.</summary>
public interface IDesktopEnvironment : IDisposable
{
    event EventHandler OpenRequested;

    event EventHandler ExitRequested;

    void SetNotificationText(string text);

    void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon);

    void PreventIdleSleep(bool prevent);

    void CopyTranscript(string text);

    void OpenRecordingFolder(string audioPath);
}
