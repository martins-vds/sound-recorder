using System.Security.Cryptography;
using SoundRecorder.Analysis;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Analysis;

/// <summary>Coverage is an explicit caller decision, never inferred from question wording.</summary>
public enum AnalysisProcessingMode { Summary, GlobalQuestion, LocalQuestion }

/// <summary>Attempt-bound processing over the existing native and durable store seams.</summary>
public sealed partial class AnalysisProcessingService(ITranscriptAnalysisStore store, AnalysisStoreRevision revision,
    string artifactDirectory, IGenerationBackend generation, IEmbeddingBackend embedding, AnalysisProcessingMode mode,
    TranscriptDiscussionContext? discussion = null,
    Func<CancellationToken, Task<IDisposable>>? enterInferenceAsync = null)
    : ILocalTranscriptAnalysis
{
    /// <inheritdoc />
    public async Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        var snapshot = await AnalysisProcessingRevision.RequireActiveAsync(store, revision);
        try
        {
            ValidateRequest(request);
            await using var text = File.Open(request.Outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var subtitles = File.Open(request.Outputs.SubtitlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await ValidateHashesAsync(text, subtitles, cancellationToken);
            progress.Report("Indexing exact saved source");
            var previous = snapshot.Completed;
            Directory.CreateDirectory(artifactDirectory);
            await IndexAsync(request, previous, cancellationToken);
            var question = await DiscussionQuestionAsync(previous, request.Question, cancellationToken);
            if (discussion?.IsFollowUp == true && question is null)
            {
                await store.CompleteAsync(revision, ClarificationAsync(previous, request.Question!, cancellationToken), cancellationToken);
                return new("Completed", "Clarification required: no explicit antecedent and prior user context for this attempt.", "", []);
            }
            var ledgerPath = Path.Combine(artifactDirectory, revision.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".sqlite");
            progress.Report("Generating UNVERIFIED candidates");
            var probe = new GenerationProbe(new GenerationBoundary(generation, enterInferenceAsync));
            var identity = new TranscriptIdentity(request.AttemptId.ToString("N"), revision.Identity.TextSha256);
            var sources = InputsAsync(question, cancellationToken);
            var outcome = mode == AnalysisProcessingMode.Summary
                ? await probe.SummarizeAsync(identity, sources, ledgerPath, Language(request.Language), TimeSpan.FromMinutes(5), cancellationToken)
                : await probe.AnswerAsync(identity, sources, ledgerPath, question!, Language(request.Language), TimeSpan.FromMinutes(3), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanCommit(outcome))
            {
                await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Failed, CancellationToken.None);
                return new(outcome.Status.ToString(), Coverage + " " + outcome.Detail, ledgerPath, []);
            }
            progress.Report("Committing source-grounded candidates");
            await store.CompleteAsync(revision, OutputsAsync(previous, request.Question, ledgerPath, cancellationToken), cancellationToken);
            return Present(ledgerPath);
        }
        catch (OperationCanceledException)
        {
            await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, CancellationToken.None);
            throw;
        }
        catch
        {
            await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Failed, CancellationToken.None);
            throw;
        }
    }

    private static bool CanCommit(AnalysisResult outcome)
    {
        if (outcome.Status == AnalysisStatus.Completed) return true;
        if (outcome.Status != AnalysisStatus.Partial) return false;
        using var ledger = ClaimLedger.Open(outcome.LedgerPath);
        return !ledger.ReadClaims().Any();
    }

    private void ValidateRequest(TranscriptAnalysisRequest request)
    {
        LocalPathPolicy.RejectNetworkDestination(request.Outputs.TextPath);
        LocalPathPolicy.RejectNetworkDestination(request.Outputs.SubtitlePath);
        LocalPathPolicy.RejectNetworkDestination(artifactDirectory);
        if (request.AttemptId != revision.Identity.AttemptId || request.Language != revision.Identity.Language)
            throw new InvalidDataException("The request does not belong to this exact transcript attempt.");
        ValidateMode(request.Question);
        _ = Language(request.Language);
    }

    private void ValidateMode(string? question)
    {
        if (!Enum.IsDefined(mode) || (mode == AnalysisProcessingMode.Summary) != (question is null))
            throw new ArgumentException("Explicit summary/question mode and a bounded question are required.");
        if (question is not null && (string.IsNullOrWhiteSpace(question) || question.Length > 512))
            throw new ArgumentException("A nonempty question within 512 characters is required.");
    }

    private async Task ValidateHashesAsync(Stream text, Stream subtitles, CancellationToken token)
    {
        if (Convert.ToHexString(await SHA256.HashDataAsync(text, token)) != revision.Identity.TextSha256 ||
            Convert.ToHexString(await SHA256.HashDataAsync(subtitles, token)) != revision.Identity.SubtitleSha256)
            throw new InvalidDataException("The saved transcript source changed; explicit rebuild is required.");
    }

    private async IAsyncEnumerable<ExcerptInput> InputsAsync(string? question,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (mode == AnalysisProcessingMode.LocalQuestion)
        {
            foreach (var chunk in await RetrieveAsync(question!, token))
                yield return Input(chunk);
            yield break;
        }
        await foreach (var chunk in store.ReadChunksAsync(revision, token))
            yield return Input(chunk);
    }

    private static ExcerptInput Input(AnalysisStoreChunk chunk) =>
        new(chunk.SourceId + "|" + chunk.Offset, chunk.StartMs, chunk.EndMs, new StringReader(chunk.Text));

    private static string Language(string value) => value switch
    {
        "English" => "en",
        "Brazilian Portuguese" => "pt-BR",
        _ => throw new ArgumentException("The successful transcript language must be English or Brazilian Portuguese.")
    };

}
