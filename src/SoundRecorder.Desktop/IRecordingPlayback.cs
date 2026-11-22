using SoundRecorder.Core;

namespace SoundRecorder.Desktop;

public interface IRecordingPlayback : IDisposable
{
    Guid? RecordingId { get; }
    string? SourcePath { get; }
    bool IsPlaying { get; }
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }
    double Volume { get; set; }
    string? LastError { get; }
    void Open(SavedRecording recording);
    void Play();
    void Pause();
    void Close();
}
