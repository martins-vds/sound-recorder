using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Models.Harness;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RealModelTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Probe_ApprovedBilingualMinute_RetainsRawCandidatesForLocalRubricReview(string language)
    {
        var directory = Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Model run blocked: approve the exact files/sizes first, then set SR_APPROVED_MODEL_DIRECTORY.");
        }
        var fixture = EvaluationFixture.Minute(language);
        using var log = new RunLog(language);
        log.Write("start", new
        {
            fixture.Identity, language, profile = "sr-model-candidate-v1", historyTokens = 0,
            contextTokens = 4096, outputTokens = 768,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            harnessAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(GenerationProbe).Assembly.Location))),
            candidateFiles = ModelFiles.CandidateFiles()
        });
        try
        {
            using var embedding = await LlamaEmbedding.LoadAsync(directory);
            log.Write("embedding-loaded", new { embedding.NativeModelBytes, embedding.Metadata });
            using var generation = await LlamaGeneration.LoadAsync(directory);
            log.Write("generation-loaded", new { generation.NativeModelBytes, generation.Metadata });
            log.Write("source-token-inventory", new
            {
                uniqueSourceTextTokensIncludingLfAndSpecialTokens = generation.CountTokens(string.Join("\n", fixture.Excerpts.Select(e => e.Text))),
                largestExcerptTokensIncludingSpecialTokens = fixture.Excerpts.Max(e => generation.CountTokens(e.Text)),
                largestPrefixedEmbeddingDocumentTokens = fixture.Excerpts.Max(e => embedding.CountTokens("search_document: " + e.Text)),
                sourceSeparator = "LF; original excerpt text unchanged"
            });
            var retriever = new EmbeddingProbe(new CapturedEmbedding(embedding, log));
            var capturedGeneration = new CapturedGeneration(generation, log);
            var probe = new GenerationProbe(capturedGeneration, outputTokens: 768);
            var errors = new List<string>();
            using var queries = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Evaluation", "queries.json")));
            foreach (var query in queries.RootElement.GetProperty("queries").EnumerateArray())
            {
                var question = query.GetProperty(language).GetString()!;
                var id = query.GetProperty("id").GetString()!;
                var scope = query.GetProperty("scope").GetString();
                log.Write("query-start", new { queryId = id, scope });
                try
                {
                    Excerpt[] evidence = fixture.Excerpts;
                    if (scope == "local")
                    {
                        var matches = await retriever.RetrieveAsync(question, fixture.Excerpts);
                        log.Write("retrieval", new { queryId = id, matches, wholeSourceCoverage = false });
                        evidence = matches.Select(m => m.Excerpt).ToArray();
                    }
                    var result = await probe.AnswerAsync(fixture.Identity, evidence, question, language);
                    log.Write("candidate-answer", new { queryId = id, scope, result, semanticScore = (double?)null });
                }
                catch (Exception error)
                {
                    errors.Add($"{id}: {error.GetType().Name}: {error.Message}");
                    log.Write("candidate-cell-failure", new { queryId = id, type = error.GetType().Name, error.Message });
                }
            }
            try
            {
                IReadOnlyList<Excerpt>[] portions = fixture.Excerpts.Chunk(5).Select(p => (IReadOnlyList<Excerpt>)p).ToArray();
                var root = await probe.MapReduceAsync(fixture.Identity, portions, language);
                Assert.Equal(fixture.Excerpts.Select(e => e.Id), root.CoveredIds);
                log.Write("recursive-candidate", new { root, semanticScore = (double?)null });
            }
            catch (Exception error)
            {
                errors.Add($"reduction: {error.GetType().Name}: {error.Message}");
                log.Write("reduction-failure", new { type = error.GetType().Name, error.Message });
            }
            using var cancellation = new CancellationTokenSource();
            using var requested = cancellation.Token.Register(() => log.Write("native-cancel-request", new { delayMs = 100 }));
            cancellation.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                probe.AnswerAsync(fixture.Identity, fixture.Excerpts, "Summarize the supplied evidence.", language, cancellation.Token));
            log.Write("native-cancel-acknowledged", new { immediateInterruptionProven = false });
            var fresh = await probe.AnswerAsync(fixture.Identity, [fixture.Excerpts[5]],
                language == "en" ? "Who verifies the access logs?" : "Quem verifica os registros de acesso?", language);
            log.Write("fresh-call-after-cancellation", new { result = fresh });
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }
        catch (Exception error)
        {
            log.Write("failure", new { type = error.GetType().Name, error.Message });
            throw;
        }
        finally
        {
            log.Write("after-disposal", new { completeSemanticEvaluation = false });
        }
    }

    private sealed class CapturedGeneration(LlamaGeneration backend, RunLog log) : IGenerationBackend
    {
        public string RenderPrompt(string instruction, string input) => backend.RenderPrompt(instruction, input);
        public int CountTokens(string prompt) => backend.CountTokens(prompt);
        public async Task<string> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            log.Write("generation-call-start", new { prompt, promptTokens = CountTokens(prompt), outputTokens });
            var text = await backend.GenerateAsync(prompt, outputTokens, cancellationToken);
            log.Write("generation-call-end", new { text, backend.LastGeneratedTokens });
            return text;
        }
    }

    private sealed class CapturedEmbedding(LlamaEmbedding backend, RunLog log) : IEmbeddingBackend
    {
        public int CountTokens(string input) => backend.CountTokens(input);
        public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
        {
            log.Write("embedding-call-start", new { input, tokens = CountTokens(input) });
            var vector = await backend.EmbedAsync(input, cancellationToken);
            log.Write("embedding-call-end", new { vector });
            return vector;
        }
    }

    private sealed class RunLog : IDisposable
    {
        private readonly string path;
        private readonly object gate = new();
        private readonly Timer sampler;

        public RunLog(string language)
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runs",
                $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{language}-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "events.jsonl");
            sampler = new Timer(_ => Write("memory-sample", new { nominalIntervalMs = 100, observer = "in-process diagnostic" }),
                null, 100, 100);
        }

        public void Write(string phase, object data)
        {
            lock (gate)
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var record = new
                {
                    phase, data, utc = DateTime.UtcNow, ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                    processId = process.Id, processStartUtc = process.StartTime.ToUniversalTime(),
                    process.WorkingSet64, process.PeakWorkingSet64, process.PrivateMemorySize64,
                    process.PeakPagedMemorySize64, memoryObservation = "phase snapshot + process lifetime high-water; native attribution unavailable"
                };
                File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
            }
        }

        public void Dispose() => sampler.Dispose();
    }
}
