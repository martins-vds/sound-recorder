namespace SoundRecorder.Core;

public interface ITranscriptWriter
{
    Task<TranscriptOutputs> WriteAsync(
        TranscriptDocument document,
        CancellationToken cancellationToken);
}
