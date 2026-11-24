using System.Text.Json;
using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class CancellationPackingTests
{
    [Fact]
    public async Task ExtractAsync_SourceCancelsAfterRead_DoesNotStartTokenizerOrGeneration()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var backend = new OrderedBudgetBackend();
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Source(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5), cancellation.Token);
            Assert.Equal(AnalysisStatus.Cancelled, result.Status);
            Assert.Equal(0, backend.TokenizerCalls);
            Assert.Equal(0, backend.GenerationCalls);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal("Cancelled", ledger.ReadRun().Status);
            Assert.Empty(ledger.ReadBatches());
            async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new CancellingReader(cancellation));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AnswerAsync_OrderChangesTokenizerBoundary_PacksUsingActualOriginalOrder()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new OrderedBudgetBackend();
            var result = await new GenerationProbe(backend, 432, 64, 32).AnswerAsync(new("attempt", new string('a', 64)), Sources(),
                Path.Combine(directory, "claims.sqlite"), "What was reported?", "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.Equal(2, backend.GenerationCalls);
            Assert.Equal(["first", "second"], backend.GeneratedQuotes);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(2, ledger.ReadClaims().Count());
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("s000001", null, null, new StringReader("first"));
                await Task.Yield();
                yield return new("s000002", null, null, new StringReader("second"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CancellingReader(CancellationTokenSource cancellation) : StringReader("source")
    {
        public override async ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadBlockAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return count;
        }
    }

    private sealed class OrderedBudgetBackend : IGenerationBackend
    {
        public int TokenizerCalls { get; private set; }
        public int GenerationCalls { get; private set; }
        public List<string> GeneratedQuotes { get; } = [];
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt)
        {
            TokenizerCalls++;
            using var input = JsonDocument.Parse(prompt);
            var excerpts = input.RootElement.GetProperty("excerpts");
            return excerpts.GetArrayLength() == 2 && excerpts[0].GetProperty("Quote").GetString() == "first" ? 400 : 100;
        }
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            Assert.True(CountTokens(prompt) + outputTokens < 432, "A generation request must use the actual serialized order's token budget.");
            using var input = JsonDocument.Parse(prompt);
            var excerpts = input.RootElement.GetProperty("excerpts").EnumerateArray().ToArray();
            GenerationCalls++;
            GeneratedQuotes.AddRange(excerpts.Select(e => e.GetProperty("Quote").GetString()!));
            return Task.FromResult(new GenerationResult(JsonSerializer.Serialize(new
            {
                claims = excerpts.Select((e, index) => new
                {
                    kind = "Report",
                    statement = e.GetProperty("Quote").GetString(),
                    owner = new { state = "Absent", value = (string?)null },
                    deadline = new { state = "Absent", value = (string?)null },
                    qualifiers = Array.Empty<string>(),
                    references = new[] { index }
                }).ToArray(),
                limitation = (string?)null
            }), GenerationFinish.Completed, 20));
        }
    }
}
