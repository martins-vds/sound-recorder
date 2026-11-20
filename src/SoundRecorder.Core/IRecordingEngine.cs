namespace SoundRecorder.Core;

public interface IRecordingEngine
{
    string? LastFailure { get; }

    Task StartAsync(RecordingRequest request, CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task<SavedRecording> StopAsync(CancellationToken cancellationToken);
}
