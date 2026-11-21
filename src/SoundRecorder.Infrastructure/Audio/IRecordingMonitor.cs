namespace SoundRecorder.Infrastructure.Audio;

/// <summary>Exposes the live recording timeline and selected-source levels.</summary>
public interface IRecordingMonitor
{
    TimeSpan CapturedDuration { get; }
    float MicrophoneLevel { get; }
    float SystemAudioLevel { get; }
    string? LastFailure { get; }
}
