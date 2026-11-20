namespace SoundRecorder.Core;

public interface ILocalTranscriber
{
    Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        CancellationToken cancellationToken);
}
