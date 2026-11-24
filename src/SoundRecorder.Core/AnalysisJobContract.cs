namespace SoundRecorder.Core;

/// <summary>Immutable successful transcript, model and algorithm selection for one explicit request.</summary>
public sealed record AnalysisJobRequest(TranscriptAnalysisRequest Transcript, AnalysisStoreIdentity Identity,
    string ModelDirectory, AnalysisJobMode Mode = AnalysisJobMode.Summary,
    AnalysisJobDiscussionContext? Discussion = null);

/// <summary>Explicit bounded follow-up reference, not an inferred pronoun or generated answer used as evidence.</summary>
public sealed record AnalysisJobDiscussionContext(bool IsFollowUp, string? Antecedent);

/// <summary>User-selected coverage, not inferred from question wording.</summary>
public enum AnalysisJobMode { Summary, GlobalQuestion, LocalQuestion }

/// <summary>Scheduling state, separate from the stored index/output revision state.</summary>
public enum AnalysisJobStatus { Queued, Running, CancellationPending, Cancelled, Interrupted, Failed, Complete }

/// <summary>An immutable scheduling snapshot; retry creates a new job, resume retains a compatible revision.</summary>
public sealed record AnalysisJob(Guid Id, AnalysisJobRequest Request, DateTimeOffset CreatedAt,
    AnalysisJobStatus Status, AnalysisStoreRevision? Revision = null, Guid? PreviousJobId = null,
    string? ErrorCode = null);

/// <summary>Durable scheduling metadata only; chunks, results and checkpoints belong to ITranscriptAnalysisStore.</summary>
public interface IAnalysisQueueRepository
{
    /// <summary>Loads the durable ordered job history without starting inference.</summary>
    Task<IReadOnlyList<AnalysisJob>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically replaces scheduling metadata; never stores transcript contents or generated output.</summary>
    Task SaveAsync(IReadOnlyList<AnalysisJob> jobs, CancellationToken cancellationToken);
}

/// <summary>Processing binds these pins and revision, and acquires a lease before every heavy native call.</summary>
public sealed record AnalysisJobExecution(AnalysisJobRequest Request, AnalysisStoreRevision Revision,
    Func<CancellationToken, Task<IDisposable>> EnterInferenceAsync);
