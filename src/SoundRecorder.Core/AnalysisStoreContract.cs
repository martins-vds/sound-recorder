namespace SoundRecorder.Core;

/// <summary>Pinned model role: hashes identify immutable weights and canonical effective configuration.</summary>
public sealed record AnalysisStoreModel(string Id, string Sha256, string ConfigurationSha256,
    string CanonicalConfiguration);

/// <summary>Exact successful transcript and processing identity; hashes include saved text and timestamp source.</summary>
public sealed record AnalysisStoreIdentity(Guid AttemptId, string TextSha256, string SubtitleSha256,
    string Language, AnalysisStoreModel Generation, AnalysisStoreModel Embedding,
    string AlgorithmVersion, int VectorDimensions);

/// <summary>Durable processing state, never inferred from the presence of partial chunks.</summary>
public enum AnalysisStoreStatus { Processing, Cancelled, Interrupted, Failed, Complete }

/// <summary>An optimistic writer token belonging to one immutable identity and revision.</summary>
public sealed record AnalysisStoreRevision(Guid Id, AnalysisStoreIdentity Identity);

/// <summary>Last committed ordinal and bounded opaque algorithm cursor, not a claim of global coverage.</summary>
public sealed record AnalysisStoreCheckpoint(int NextOrdinal, string Stage, string Cursor);

/// <summary>One bounded original excerpt and its optional embedding, stored without normalization or inference.</summary>
public sealed record AnalysisStoreChunk(int Ordinal, string SourceId, long Offset, long? StartMs,
    long? EndMs, string Text, IReadOnlyList<float> Vector);

/// <summary>One ordered summary/discussion output portion with original citations; content is semantically unverified.</summary>
public sealed record AnalysisStoreOutput(int Ordinal, string Kind, string? Question, string Content,
    IReadOnlyList<TranscriptAnalysisClaim> Claims);

/// <summary>Exact identity state: completed revision remains available during unfinished replacement.</summary>
public sealed record AnalysisStoreSnapshot(AnalysisStoreRevision? Completed, AnalysisStoreRevision? Unfinished,
    AnalysisStoreStatus Status, AnalysisStoreCheckpoint? Checkpoint);

/// <summary>Local durable attempt-owned analysis. Missing data and incompatible identities throw explicitly.</summary>
public interface ITranscriptAnalysisStore
{
    /// <summary>Starts an explicit replacement without changing an earlier completed activation.</summary>
    Task<AnalysisStoreRevision> BeginAsync(AnalysisStoreIdentity identity, CancellationToken cancellationToken);

    /// <summary>Loads only an exact identity; returns unfinished state separately from complete results.</summary>
    Task<AnalysisStoreSnapshot> LoadAsync(AnalysisStoreIdentity identity, CancellationToken cancellationToken);

    /// <summary>Commits contiguous bounded chunks and their cursor together using an optimistic ordinal guard.</summary>
    Task SaveCheckpointAsync(AnalysisStoreRevision revision, AnalysisStoreCheckpoint checkpoint,
        IAsyncEnumerable<AnalysisStoreChunk> chunks, CancellationToken cancellationToken);

    /// <summary>Streams the selected revision's committed original excerpt/index chunks in ordinal order.</summary>
    IAsyncEnumerable<AnalysisStoreChunk> ReadChunksAsync(AnalysisStoreRevision revision,
        CancellationToken cancellationToken);

    /// <summary>Atomically activates all streamed output portions and the staged index/checkpoint revision.</summary>
    Task CompleteAsync(AnalysisStoreRevision revision, IAsyncEnumerable<AnalysisStoreOutput> outputs,
        CancellationToken cancellationToken);

    /// <summary>Streams only completed summary/discussion content and citations, never incomplete output.</summary>
    IAsyncEnumerable<AnalysisStoreOutput> ReadOutputsAsync(AnalysisStoreRevision revision,
        CancellationToken cancellationToken);

    /// <summary>Records explicit unfinished status; retained compatible checkpoints remain resumable.</summary>
    Task SetUnfinishedAsync(AnalysisStoreRevision revision, AnalysisStoreStatus status,
        CancellationToken cancellationToken);

    /// <summary>Explicitly resumes a compatible noncomplete revision without discarding its chunks.</summary>
    Task ResumeAsync(AnalysisStoreRevision revision, CancellationToken cancellationToken);

    /// <summary>Deletes all analysis revisions/indexes/checkpoints owned by this attempt, not source files or models.</summary>
    Task DeleteOwnedAsync(Guid attemptId, CancellationToken cancellationToken);
}
