using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SoundRecorder.Analysis;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis;

public sealed partial class AnalysisProcessingService
{
    private string Coverage => mode == AnalysisProcessingMode.LocalQuestion
        ? "UNVERIFIED. Retrieval-limited local lookup; not exhaustive and cannot establish absence."
        : "UNVERIFIED. Complete whole-transcript source traversal; extraction is not semantic verification and cannot prove absence.";

    private async IAsyncEnumerable<AnalysisStoreOutput> OutputsAsync(AnalysisStoreRevision? previous,
        string? question, string ledgerPath, [EnumeratorCancellation] CancellationToken token)
    {
        var ordinal = 0;
        if (previous is not null)
        {
            await foreach (var output in store.ReadOutputsAsync(previous, token))
                yield return output with { Ordinal = ordinal++ };
        }
        await foreach (var output in NewOutputsAsync(ordinal, question, ledgerPath, token))
            yield return output;
    }

    private async IAsyncEnumerable<AnalysisStoreOutput> NewOutputsAsync(int ordinal, string? question,
        string ledgerPath, [EnumeratorCancellation] CancellationToken token)
    {
        var kind = question is null ? "Summary" : "Discussion";
        yield return new(ordinal++, kind, question, Coverage, []);
        using var ledger = ClaimLedger.Open(ledgerPath);
        var any = false;
        foreach (var output in CandidateOutputs(ledger, kind, question))
        {
            token.ThrowIfCancellationRequested();
            any = true;
            yield return output with { Ordinal = ordinal++ };
        }
        if (!any)
            yield return new(ordinal++, kind, question,
                "Insufficient evidence: no supported candidate returned. This does not prove that an answer is absent.", []);
        foreach (var output in LimitationOutputs(ledger, kind, question))
            yield return output with { Ordinal = ordinal++ };
        await Task.CompletedTask;
    }

    private static IEnumerable<AnalysisStoreOutput> CandidateOutputs(ClaimLedger ledger, string kind, string? question)
    {
        foreach (var stored in ledger.ReadClaims())
        {
            foreach (var evidence in ledger.ReadEvidence(stored.Id).Chunk(16))
            {
                var claim = new TranscriptAnalysisClaim(Format(stored.Record), evidence.Select(Citation).ToArray());
                yield return new(0, kind, question,
                    JsonSerializer.Serialize(stored.Record), [claim]);
            }
        }
    }

    private static IEnumerable<AnalysisStoreOutput> LimitationOutputs(ClaimLedger ledger, string kind, string? question)
    {
        foreach (var diagnostic in ledger.ReadBatches())
        {
            if (diagnostic.Limitation is not null)
                yield return new(0, kind, question,
                    "UNVERIFIED extraction limitation: " + diagnostic.Limitation, []);
        }
    }

    private TranscriptAnalysisResult Present(string ledgerPath)
    {
        using var ledger = ClaimLedger.Open(ledgerPath);
        var preview = ledger.ReadClaims().Take(201).ToArray();
        var claims = preview.Take(200).Select(claim => new TranscriptAnalysisClaim(Format(claim.Record),
            ledger.ReadEvidence(claim.Id).Take(16).Select(Citation).ToArray())).ToArray();
        return new("Completed", Coverage + (claims.Length == 0 ? " Insufficient evidence; no answer was inferred." : "") +
            " Preview bounded to 200 claims/16 citations; every record and citation remains in completed store output.",
            ledgerPath, claims);
    }

    private static TranscriptAnalysisEvidence Citation(ClaimEvidence evidence)
    {
        var separator = evidence.SourceId.IndexOf('|');
        var offset = long.Parse(evidence.SourceId[(separator + 1)..], CultureInfo.InvariantCulture) + evidence.Offset;
        return new($"{evidence.SourceId[..separator]} · characters {offset}–{offset + evidence.Length}",
            evidence.StartMs, evidence.Quote);
    }

    private static string Format(StructuredClaim claim) =>
        $"{claim.Kind}: {claim.Statement}\nOwner: {claim.Owner.Value ?? "not stated"} · Deadline: {claim.Deadline.Value ?? "not stated"}" +
        (claim.Qualifiers.Length == 0 ? "" : "\nQualifications: " + string.Join("; ", claim.Qualifiers));
}
