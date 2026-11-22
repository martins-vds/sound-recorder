namespace SoundRecorder.Core;

public enum TranscriptionPhase
{
    Preparing,
    Recognizing,
    Writing,
}

/// <summary>Recognition percent is a coarse audio-position estimate, not whole-job completion.</summary>
public sealed record TranscriptionProgress(TranscriptionPhase Phase, int? Percent = null);

public sealed record TranscriptionJobProgress(Guid JobId, TranscriptionProgress Progress);

public sealed record TranscriptionRequest(
    Guid JobId,
    SavedRecording Recording,
    string? ModelId,
    string? ModelPath);

public interface IJobTranscriber : ILocalTranscriber
{
    Task<TranscriptDocument> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}
