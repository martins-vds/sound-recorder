using Models.Harness;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace SoundRecorder.Infrastructure.Analysis;

/// <summary>Narrow experimental adapter over the approved feasibility pipeline, without downloads.</summary>
public sealed class LocalTranscriptAnalysis(string modelDirectory, string artifactDirectory,
    IGenerationBackend? generation = null, IEmbeddingBackend? embedding = null) : ILocalTranscriptAnalysis, ILocalAnalysisDiagnostics
{
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);

    public Task DeleteDiagnosticsAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await InferenceGate.WaitAsync(cancellationToken);
            try
            {
                foreach (var attempt in attemptIds.Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = Path.Combine(artifactDirectory, attempt.ToString("N"));
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
            finally { InferenceGate.Release(); }
        });

    public Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunAsync(request, progress, cancellationToken));

    private async Task<TranscriptAnalysisResult> RunAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken token)
    {
        if (request.AttemptId == Guid.Empty || request.Question is { Length: > 512 })
            throw new ArgumentException("Select a successful transcript and keep questions within 512 characters.");
        var language = AnalysisLanguage(request.Language);
        await InferenceGate.WaitAsync(token);
        try
        {
            progress.Report("Reading the exact saved transcript");
            var run = Path.Combine(artifactDirectory, request.AttemptId.ToString("N"), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            var snapshot = Path.Combine(run, "source.txt");
            var subtitleSnapshot = Path.ChangeExtension(snapshot, ".srt");
            await using (var subtitles = File.Open(request.Outputs.SubtitlePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var source = File.Open(request.Outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var destination = File.Create(snapshot))
            {
                await source.CopyToAsync(destination, token);
                await using var subtitleDestination = File.Create(subtitleSnapshot);
                await subtitles.CopyToAsync(subtitleDestination, token);
            }
            foreach (var _ in SavedTranscriptSources.Read(snapshot, subtitleSnapshot, token)) { }
            var hash = await PairHashAsync(snapshot, subtitleSnapshot, token);
            var identity = new TranscriptIdentity(request.AttemptId.ToString("N"), hash);
            progress.Report("Verifying and loading cached Phi-mini (no downloads)");
            if (generation is null) NativeCpu.Probe();
            using var nativeGeneration = generation is null
                ? await LlamaGeneration.LoadPhiMiniAsync(RequireModelDirectory(), token) : null;
            var backend = generation ?? nativeGeneration!;
            var probe = new GenerationProbe(backend);
            var ledgerPath = Path.Combine(run, "claims.sqlite");
            AnalysisResult outcome;
            if (request.Question is { } question)
            {
                progress.Report("Retrieving original excerpts with the cached embedding model");
                var matches = await RetrieveAsync(question, snapshot, token);
                progress.Report("Generating an UNVERIFIED answer candidate from retrieved excerpts");
                outcome = await probe.AnswerAsync(identity, Inputs(matches, token),
                    ledgerPath, question, language, TimeSpan.FromMinutes(3), token);
            }
            else
            {
                progress.Report("Generating UNVERIFIED summary candidates from original excerpts");
                outcome = await probe.SummarizeAsync(identity, Inputs(snapshot, token), ledgerPath,
                    language, TimeSpan.FromMinutes(5), token);
            }
            token.ThrowIfCancellationRequested();
            progress.Report("Resolving exact original evidence");
            var result = Present(outcome);
            return request.Question is null ? result : result with
            {
                Detail = "Retrieval-limited: at most three highest-ranked original ranges were supplied. This is not exhaustive and cannot establish every item or the absence of an answer. " + result.Detail
            };
        }

        finally { InferenceGate.Release(); }
    }

    private static async Task<string> PairHashAsync(string text, string subtitles, CancellationToken token)
    {
        await using var textFile = File.OpenRead(text);
        await using var subtitleFile = File.OpenRead(subtitles);
        var textHash = Convert.ToHexString(await SHA256.HashDataAsync(textFile, token));
        var subtitleHash = Convert.ToHexString(await SHA256.HashDataAsync(subtitleFile, token));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(textHash + ":" + subtitleHash)));
    }

    private async Task<Excerpt[]> RetrieveAsync(string question, string snapshot, CancellationToken token)
    {
        using var nativeEmbedding = embedding is null
            ? await LlamaEmbedding.LoadAsync(RequireModelDirectory(), token) : null;
        var matches = await new EmbeddingProbe(embedding ?? nativeEmbedding!)
            .RetrieveAsync(question, ReadExcerptsAsync(snapshot, token), 3, token);
        return matches.Select(match => match.Excerpt).ToArray();
    }

    private static TranscriptAnalysisResult Present(AnalysisResult outcome)
    {
        using var ledger = ClaimLedger.Open(outcome.LedgerPath);
        var claims = ledger.ReadClaims().Take(201).ToArray();
        var detail = outcome.Detail;
        if (claims.Length > 200) detail += " Display limited to 200 candidates; full records remain in the local ledger.";
        var presented = claims.Take(200).Select(claim => new TranscriptAnalysisClaim(
            Format(claim.Record), ledger.ReadEvidence(claim.Id).Select(evidence => new TranscriptAnalysisEvidence(
                $"{evidence.SourceId} · characters {evidence.Offset}–{evidence.Offset + evidence.Length}",
                evidence.StartMs, evidence.Quote)).ToArray())).ToArray();
        if (presented.Length == 0) detail += " No supported candidate was returned; this is not an answer or summary.";
        return new(outcome.Status.ToString(), detail, outcome.LedgerPath, presented);
    }

    private string RequireModelDirectory()
    {
        var directory = ConfiguredModelDirectory();
        return Directory.Exists(directory) ? directory :
            throw new DirectoryNotFoundException(
                "Set SOUNDRECORDER_ANALYSIS_MODELS or analysis-model-cache.txt beside the executable to the existing approved Phi-mini and embedding cache directory. No models are downloaded.");
    }

    private string ConfiguredModelDirectory()
    {
        var configuration = Path.Combine(AppContext.BaseDirectory, "analysis-model-cache.txt");
        return string.IsNullOrWhiteSpace(modelDirectory) && File.Exists(configuration)
            ? File.ReadAllText(configuration).Trim() : modelDirectory;
    }

    private static string Format(StructuredClaim claim) =>
        $"{claim.Kind}: {claim.Statement}\nOwner: {claim.Owner.Value ?? "not stated"} · Deadline: {claim.Deadline.Value ?? "not stated"}" +
        (claim.Qualifiers.Length == 0 ? "" : "\nQualifications: " + string.Join("; ", claim.Qualifiers));

    private static string AnalysisLanguage(string language) => language switch
    {
        "Brazilian Portuguese" => "pt-BR",
        "English" => "en",
        _ => throw new ArgumentException("The exact successful attempt language must be English or Brazilian Portuguese; no language is guessed.")
    };

    private static async IAsyncEnumerable<Excerpt> ReadExcerptsAsync(string path,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var excerpt in SavedTranscriptSources.Read(path, Path.ChangeExtension(path, ".srt"), token))
            yield return excerpt;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ExcerptInput> Inputs(string path,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var excerpt in ReadExcerptsAsync(path, token))
            yield return new(excerpt.Id, excerpt.StartMs, excerpt.EndMs, new StringReader(excerpt.Text));
    }

    private static async IAsyncEnumerable<ExcerptInput> Inputs(IEnumerable<Excerpt> excerpts,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var excerpt in excerpts)
        {
            token.ThrowIfCancellationRequested();
            yield return new(excerpt.Id, excerpt.StartMs, excerpt.EndMs, new StringReader(excerpt.Text));
        }
        await Task.CompletedTask;
    }
}
