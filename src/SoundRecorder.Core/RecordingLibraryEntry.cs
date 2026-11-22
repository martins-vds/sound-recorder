namespace SoundRecorder.Core;

public enum AudioAvailability { Available, Missing, Deleted }

public enum LibraryFailureCode
{
    OwnershipInvalid,
    ArtifactMissing,
    ArtifactReadFailed,
    OrchestrationUnavailable,
    ArtifactDeleteFailed,
    WorkerReleaseFailed,
    PlaybackReleaseFailed,
    PersistenceFailed,
    HistoryDeleteFailed,
    DeletionConflict,
    DeletionCanceled,
}

public sealed record LibraryIssue(
    LibraryFailureCode Code, string Message, string? Path = null, Guid? JobId = null);

public sealed record RecordingLibraryEntry(
    SavedRecording Recording, AudioAvailability AudioAvailability,
    IReadOnlyList<LibraryIssue> Issues);

public sealed record TranscriptReadResult(
    string? Text, string? Subtitles, IReadOnlyList<LibraryIssue> Issues);
