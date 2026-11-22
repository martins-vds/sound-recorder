namespace SoundRecorder.Core;

public enum RecordingDeletionScope { AudioOnly, AllArtifacts }
public enum RecordingArtifactKind { Audio, Text, Subtitle }
public enum ArtifactDeletionState { Pending, Removed, AlreadyMissing, Failed }
public enum LibraryDeletionStatus { Completed, Incomplete, Rejected, NotFound }

public sealed record ArtifactDeletion(
    RecordingArtifactKind Kind, string Path, Guid? JobId,
    ArtifactDeletionState State, LibraryIssue? Failure);

/// <summary>Durable intent and outcomes; loading it must never resume file removal.</summary>
public sealed record PendingRecordingDeletion(
    Guid OperationId, RecordingDeletionScope Scope,
    IReadOnlyList<ArtifactDeletion> Artifacts,
    IReadOnlyList<Guid> RemainingJobIds, LibraryIssue? Failure);

public sealed record LibraryDeletionResult(
    Guid TargetId, LibraryDeletionStatus Status,
    PendingRecordingDeletion? PendingDeletion, IReadOnlyList<LibraryIssue> Issues);
