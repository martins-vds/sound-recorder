using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis;

/// <summary>One immutable successful output pair, never a recording-wide "latest" alias.</summary>
public sealed record TranscriptAnalysisRequest(Guid AttemptId, TranscriptOutputs Outputs, string Language, string? Question);

/// <summary>Original saved text, with an original saved-audio timestamp when present.</summary>
public sealed record TranscriptAnalysisEvidence(string Reference, long? StartMs, string Text);

/// <summary>A semantically unverified candidate interpretation and its unchanged source excerpts.</summary>
public sealed record TranscriptAnalysisClaim(string Text, IReadOnlyList<TranscriptAnalysisEvidence> Evidence);

/// <summary>Terminal candidate output; a ledger is diagnostic evidence, not proof of semantic correctness.</summary>
public sealed record TranscriptAnalysisResult(string Status, string? Detail, string LedgerPath,
    IReadOnlyList<TranscriptAnalysisClaim> Claims);

/// <summary>Local-only analysis of a selected successful transcript using explicitly installed weights.</summary>
public interface ILocalTranscriptAnalysis
{
    Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken cancellationToken);
}

/// <summary>Removes private diagnostics only for explicitly owned successful attempts.</summary>
public interface ILocalAnalysisDiagnostics
{
    Task DeleteDiagnosticsAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken);
}
