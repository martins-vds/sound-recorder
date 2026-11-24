using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SoundRecorder.Analysis;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RealModelTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Summary_ApprovedBilingualMinute_PersistsInitialUnverifiedClaimLedger(string language)
    {
        var fixture = EvaluationFixture.Minute(language);
        using var log = new RunLog(language, "summary");
        WriteIdentity(log, fixture);
        using var generation = await LoadGeneration();
        log.Write("generation-loaded", new { generation.NativeModelBytes, generation.Metadata });
        var probe = new GenerationProbe(new CapturedGeneration(generation, log));
        var result = await probe.SummarizeAsync(fixture.Identity, Sources(fixture.Excerpts),
            Path.Combine(log.Directory, "claims.sqlite"), language, TimeSpan.FromMinutes(20));
        log.Write("summary-diagnostic", new { result, semanticVerification = "UNVERIFIED", diagnosticDeadlineMinutes = 20 });
        ExportLedger(log, result);
        Assert.Equal(AnalysisStatus.Completed, result.Status);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Questions_ApprovedBilingualMinute_UseIndependentRequestDeadlines(string language)
    {
        var fixture = EvaluationFixture.Minute(language);
        using var log = new RunLog(language, "questions");
        WriteIdentity(log, fixture);
        using var costGuard = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var generation = await LoadGeneration();
        using var embedding = await LlamaEmbedding.LoadAsync(ApprovedCache(), costGuard.Token);
        var probe = new GenerationProbe(new CapturedGeneration(generation, log));
        var retriever = new EmbeddingProbe(embedding);
        using var queries = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Evaluation", "queries.json")));
        var requestedId = Environment.GetEnvironmentVariable("SR_EVAL_QUERY_ID");
        var failures = new List<string>();
        foreach (var query in queries.RootElement.GetProperty("queries").EnumerateArray())
        {
            var id = query.GetProperty("id").GetString()!;
            if (requestedId is not null && requestedId != id) continue;
            var question = query.GetProperty(language).GetString()!;
            var scope = query.GetProperty("scope").GetString();
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(costGuard.Token);
            requestDeadline.CancelAfter(TimeSpan.FromMinutes(2));
            var elapsed = Stopwatch.StartNew();
            var evidence = fixture.Excerpts;
            if (scope == "local")
            {
                var matches = await retriever.RetrieveAsync(question, evidence, cancellationToken: requestDeadline.Token);
                log.Write("retrieval", new { queryId = id, matches, wholeSourceCoverage = false });
                evidence = matches.Select(match => match.Excerpt).ToArray();
            }
            var result = await probe.AnswerAsync(fixture.Identity, Sources(evidence),
                Path.Combine(log.Directory, $"{id}.sqlite"), question, language,
                TimeSpan.FromMinutes(2), requestDeadline.Token);
            log.Write("question-diagnostic", new { queryId = id, scope, result, requestElapsed = elapsed.Elapsed,
                requestDeadlineMinutes = 2, wholeRunCostGuardMinutes = 20, semanticVerification = "UNVERIFIED" });
            ExportLedger(log, result);
            if (result.Status != AnalysisStatus.Completed) failures.Add($"{id}: {result.Status}: {result.Detail}");
            if (costGuard.IsCancellationRequested) break;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    internal static async IAsyncEnumerable<ExcerptInput> Sources(IEnumerable<Excerpt> excerpts)
    {
        foreach (var excerpt in excerpts)
        {
            yield return new(excerpt.Id, excerpt.StartMs, excerpt.EndMs, new StringReader(excerpt.Text));
            await Task.Yield();
        }
    }

    private static string ApprovedCache() => Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY")
        ?? throw new InvalidOperationException("Configure the already approved external model cache; no download.");

    private static Task<LlamaGeneration> LoadGeneration() => Environment.GetEnvironmentVariable("SR_EVAL_PROFILE") switch
    {
        "phi-mini" => LlamaGeneration.LoadPhiMiniAsync(ApprovedCache()),
        "qwen3" => LlamaGeneration.LoadQwen3Async(ApprovedCache()),
        _ => LlamaGeneration.LoadAsync(ApprovedCache())
    };

    private static void WriteIdentity(RunLog log, EvaluationFixture fixture) => log.Write("start", new
    {
        diagnosticSchema = "sr-structured-ledger-v4", fixture.Identity, fixture.Language,
        profile = Environment.GetEnvironmentVariable("SR_EVAL_PROFILE") ?? "qwen2",
        contextTokens = 4096, extractionOutputTokens = 1024, answerOutputTokens = 512,
        sourceBufferCharacters = 1025, extractionBatchRanges = 4, sourceOverlapCharacters = 256,
        extractionBatchCharacters = 4096, historyTokens = 0,
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        framework = RuntimeInformation.FrameworkDescription,
        harnessAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(GenerationProbe).Assembly.Location))),
        semanticVerification = "UNVERIFIED", completionObservation = "sampled native EOG versus native length",
        cancellationBoundary = "safe native boundary, not immediate abort"
    });

    private static void ExportLedger(RunLog log, AnalysisResult result)
    {
        using var ledger = ClaimLedger.Open(result.LedgerPath);
        using var writer = File.CreateText(result.LedgerPath + ".jsonl");
        var run = ledger.ReadRun();
        writer.WriteLine(JsonSerializer.Serialize(new { run.Identity, run.Status, run.Detail }));
        foreach (var batch in ledger.ReadBatches())
        {
            writer.WriteLine(JsonSerializer.Serialize(new { batch }));
        }
        foreach (var claim in ledger.ReadClaims())
        {
            writer.WriteLine(JsonSerializer.Serialize(new { claim }));
            foreach (var evidence in ledger.ReadEvidence(claim.Id))
            {
                writer.WriteLine(JsonSerializer.Serialize(new { claimId = claim.Id, evidence }));
            }
        }
        log.Write("ledger-exported", new { result.LedgerPath, authoritativeEvidence = "resolved original quotes only" });
    }

    private sealed class CapturedGeneration(LlamaGeneration backend, RunLog log) : IGenerationBackend
    {
        public string RenderPrompt(string instruction, string input) => backend.RenderPrompt(instruction, input);
        public int CountTokens(string prompt) => backend.CountTokens(prompt);
        public async Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            log.Write("generation-call-start", new { prompt, promptTokens = CountTokens(prompt), outputTokens });
            try
            {
                var result = await backend.GenerateAsync(prompt, outputTokens, cancellationToken);
                log.Write("generation-call-end", new { result, backend.LastGeneratedTokens });
                return result;
            }
            catch (Exception error)
            {
                log.Write("generation-call-failure", new { type = error.GetType().Name, error.Message, backend.LastGeneratedTokens });
                throw;
            }
        }
    }

    private sealed class RunLog : IDisposable
    {
        public string Directory { get; }
        private readonly string path;
        private readonly object gate = new();
        private readonly Timer sampler;

        public RunLog(string language, string diagnostic)
        {
            var root = Environment.GetEnvironmentVariable("SR_EVAL_RUN_DIRECTORY")
                ?? Path.Combine(System.IO.Directory.GetCurrentDirectory(), "runs");
            Directory = Path.GetFullPath(Path.Combine(root,
                $"{Environment.GetEnvironmentVariable("SR_EVAL_PHASE") ?? "structured-v2"}-{diagnostic}-{language}-{Guid.NewGuid():N}"));
            System.IO.Directory.CreateDirectory(Directory);
            path = Path.Combine(Directory, "events.jsonl");
            sampler = new Timer(_ => Write("memory-sample", new { nominalIntervalMs = 100, observer = "in-process diagnostic" }),
                null, 100, 100);
        }

        public void Write(string phase, object data)
        {
            lock (gate)
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    phase, data, utc = DateTime.UtcNow, ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                    processId = process.Id, process.WorkingSet64, process.PeakWorkingSet64, process.PrivateMemorySize64,
                    memoryObservation = "native-inclusive shared process; not phase-isolated allocation attribution"
                }) + "\n");
            }
        }

        public void Dispose() => sampler.Dispose();
    }
}
