namespace SoundRecorder.Core;

public interface ITranscriptionQueue
{
    Task EnqueueAsync(SavedRecording recording, CancellationToken cancellationToken);

    Task CancelRecordingAsync(Guid recordingId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This queue does not support recording cancellation.");

    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This queue does not support job-history deletion.");
}
