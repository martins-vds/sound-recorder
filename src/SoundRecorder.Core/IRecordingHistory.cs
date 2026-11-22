namespace SoundRecorder.Core;

public interface IRecordingHistory
{
    Task<IReadOnlyList<SavedRecording>> LoadAsync(
        CancellationToken cancellationToken);

    Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken);

    /// <summary>Updates metadata, retaining output ownership, intentional audio removal, and unfinished deletion intent.</summary>
    Task UpdateAsync(SavedRecording recording, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This recording history does not support updates.");

    /// <summary>Removes metadata only; it does not remove recording artifacts.</summary>
    Task RemoveAsync(Guid recordingId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This recording history does not support removal.");

    /// <summary>Associates completed pairs idempotently; new publication is rejected during deletion or after audio removal.</summary>
    Task RegisterOutputsAsync(
        Guid recordingId,
        OwnedTranscriptOutputs outputs,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This recording history does not support output registration.");
}
