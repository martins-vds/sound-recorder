using Microsoft.Win32;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Desktop;

/// <summary>Capture and operating-system lifecycle signals consumed by the main window.</summary>
public interface IDesktopLifecycleEvents
{
    event EventHandler<SavedRecording> RecordingCompleted;
    event PowerModeChangedEventHandler PowerModeChanged;
    event SessionEndingEventHandler SessionEnding;
}

/// <summary>Direct forwarding of Windows and capture-engine notifications.</summary>
public sealed class WindowsDesktopLifecycleEvents(WindowsRecordingEngine engine) : IDesktopLifecycleEvents
{
    public event EventHandler<SavedRecording> RecordingCompleted
    {
        add => engine.RecordingCompleted += value;
        remove => engine.RecordingCompleted -= value;
    }
    public event PowerModeChangedEventHandler PowerModeChanged
    {
        add => Microsoft.Win32.SystemEvents.PowerModeChanged += value;
        remove => Microsoft.Win32.SystemEvents.PowerModeChanged -= value;
    }
    public event SessionEndingEventHandler SessionEnding
    {
        add => Microsoft.Win32.SystemEvents.SessionEnding += value;
        remove => Microsoft.Win32.SystemEvents.SessionEnding -= value;
    }
}
