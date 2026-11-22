namespace SoundRecorder.Core;

/// <summary>Local recording review and explicit management of retained artifacts.</summary>
public interface IRecordingLibrary
{
    /// <summary>Loads metadata and legacy successful outputs, without resuming pending deletions.</summary>
    Task<IReadOnlyList<RecordingLibraryEntry>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Searches titles and retained completed TXT content locally; blank queries return all entries.</summary>
    Task<IReadOnlyList<RecordingLibraryEntry>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>Reads an exact owned job's pair independently; unavailable artifacts produce explicit issues.</summary>
    Task<TranscriptReadResult> ReadTranscriptAsync(
        Guid recordingId, Guid jobId, CancellationToken cancellationToken);

    /// <summary>Validates ownership, awaits scoped worker/playback release, and durably checkpoints deletion for explicit retry.</summary>
    Task<LibraryDeletionResult> DeleteRecordingAsync(
        Guid recordingId, RecordingDeletionScope scope, CancellationToken cancellationToken);

    /// <summary>Removes terminal job history only after retaining its independently owned completed outputs.</summary>
    Task<LibraryDeletionResult> DeleteJobHistoryAsync(Guid jobId, CancellationToken cancellationToken);
}
