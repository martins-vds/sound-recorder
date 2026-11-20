namespace SoundRecorder.Core;

public interface ITranscriptionQueue
{
    Task EnqueueAsync(SavedRecording recording, CancellationToken cancellationToken);
}
