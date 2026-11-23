using System.Text.Json;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class PackingBoundaryTests
{
    [Theory]
    [InlineData(1024, 4, 2)]
    public async Task ExtractAsync_SourceCharacterCap_PreservesExactBoundaryAndSourceOrder(int length, int firstBatch, int calls)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new PayloadBackend();
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Sources(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.Equal(calls, result.Calls);
            Assert.Equal(firstBatch, backend.Groups[0].Length);
            Assert.Equal(Enumerable.Range(1, 4).Select(i => $"s{i:D6}").Append("s000005"), backend.Groups.SelectMany(g => g));
            Assert.All(backend.Characters, size => Assert.InRange(size, 1, 4096));
            Assert.True(result.MaximumLiveSourceCharacters >= backend.MinimumOwnedCharacters,
                "The live-state bound cannot omit the final prompt, input ranges and active read buffer.");
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(5, ledger.ReadClaims().Count());
            async IAsyncEnumerable<ExcerptInput> Sources()
            {
                for (var i = 1; i <= 4; i++)
                {
                    yield return new($"s{i:D6}", null, null, new StringReader(new string('x', length)));
                    await Task.Yield();
                }
                yield return new("s000005", null, null, new StringReader("last"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExtractAsync_OverlappingWindowsExceedCharacterCap_FlushesBeforeFourthRange()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new PayloadBackend();
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Sources(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.Equal([3, 2], backend.Groups.Select(g => g.Length));
            Assert.Equal(3584, backend.Characters[0]);
            Assert.All(backend.Characters, c => Assert.True(c <= 4096));
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(2, ledger.ReadClaims().Count());
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("s000001", null, null, new StringReader(string.Concat(Enumerable.Repeat("x ", 2048))));
                await Task.Yield();
                yield return new("s000002", null, null, new StringReader("last"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class PayloadBackend : IGenerationBackend
    {
        public List<string[]> Groups { get; } = [];
        public List<int> Characters { get; } = [];
        public int MinimumOwnedCharacters { get; private set; }
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt) => 10;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            using var input = JsonDocument.Parse(prompt);
            var excerpts = input.RootElement.GetProperty("excerpts").EnumerateArray().ToArray();
            Groups.Add(excerpts.Select(e => e.GetProperty("SourceId").GetString()!).ToArray());
            Characters.Add(excerpts.Sum(e => e.GetProperty("Length").GetInt32()));
            MinimumOwnedCharacters = Math.Max(MinimumOwnedCharacters, prompt.Length + Characters[^1] + 1025);
            return Task.FromResult(new GenerationResult(JsonSerializer.Serialize(new
            {
                claims = excerpts.Select((e, index) => new
                {
                    kind = "Report",
                    statement = e.GetProperty("SourceId").GetString(),
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
