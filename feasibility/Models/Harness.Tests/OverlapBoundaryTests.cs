using System.Text;
using System.Text.Json;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class OverlapBoundaryTests
{
    [Fact]
    public async Task ExtractAsync_ExactlyFullScalarPrefix_ShrinksToLeaveSpareContextWithoutDroppingText()
    {
        var directory=Path.Combine(Directory.GetCurrentDirectory(),"pipeline-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend=new RangeBackend{TokensFromLength=true};
            var result=await new GenerationProbe(backend,5,1).ExtractAsync(new("attempt",new string('a',64)),Source(),
                Path.Combine(directory,"claims.sqlite"),"en",TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed,result.Status);
            Assert.Equal(2,result.Calls);
            Assert.Equal(["ab","cd"],backend.Ranges.Select(r=>r.Quote));
            using var ledger=ClaimLedger.Open(result.LedgerPath);
            Assert.All(ledger.ReadBatches(),b=>Assert.True(b.PromptTokens+b.OutputBudget<5));
            Assert.Equal(4,Assert.Single(ledger.ReadCoverage()).Characters);
            static async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001",null,null,new StringReader("abcd"));
            }
        }
        finally{Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAsync_WordAndSurrogateAtOverlapBoundary_PreservesWholeWordsExactOffsetsAndUniqueCoverage(bool surrogate)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var prefix = surrogate ? new string('x', 767) + "😀" : new string('x', 766) + " xx";
            var text = prefix + " " + new string('w', 253) + " " + new string('y', 800);
            var backend = new RangeBackend();
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Source(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.True(backend.Ranges.Count >= 2);
            var second = backend.Ranges[1];
            Assert.True(second.Offset >= 768);
            Assert.True(second.Offset <= 1024);
            Assert.True(char.IsWhiteSpace(text[(int)second.Offset - 1]));
            Assert.False(char.IsLowSurrogate(second.Quote[0]));
            Assert.All(backend.Ranges, range => Assert.Equal(text.Substring((int)range.Offset, range.Length), range.Quote));
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(text.Length, Assert.Single(ledger.ReadCoverage()).Characters);
            async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader(text));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExtractAsync_TokenLimitedPieces_RetainsBoundedWordOverlapInsteadOfDroppingRelationship()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda";
            var backend = new RangeBackend { TokensFromLength = true };
            var result = await new GenerationProbe(backend, 25, 4).ExtractAsync(new("attempt", new string('a', 64)), Source(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.True(backend.Ranges.Count > 2);
            var overlap = false;
            long end = 0;
            foreach (var range in backend.Ranges)
            {
                Assert.True(range.Offset <= end);
                overlap |= range.Offset < end;
                Assert.Equal(text.Substring((int)range.Offset, range.Length), range.Quote);
                end = Math.Max(end, range.Offset + range.Length);
            }
            Assert.True(overlap);
            Assert.Equal(text.Length, end);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(text.Length, Assert.Single(ledger.ReadCoverage()).Characters);
            async IAsyncEnumerable<ExcerptInput> Source() { await Task.Yield(); yield return new("s000001", null, null, new StringReader(text)); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RangeBackend : IGenerationBackend
    {
        public bool TokensFromLength { get; init; }
        public List<ClaimEvidence> Ranges { get; } = [];
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt)
        {
            if (!TokensFromLength) return 10;
            using var input = JsonDocument.Parse(prompt);
            return input.RootElement.GetProperty("excerpts").EnumerateArray().Sum(e => e.GetProperty("Length").GetInt32());
        }
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            using var input = JsonDocument.Parse(prompt);
            var excerpts = input.RootElement.GetProperty("excerpts").Deserialize<ClaimEvidence[]>()!;
            Ranges.AddRange(excerpts);
            return Task.FromResult(new GenerationResult(JsonSerializer.Serialize(new
            {
                claims = new[]{new
                {
                    kind="Topic",statement="review",owner=new{state="Absent",value=(string?)null},
                    deadline=new{state="Absent",value=(string?)null},qualifiers=Array.Empty<string>(),
                    references=Enumerable.Range(0,excerpts.Length).ToArray()
                }},
                limitation = (string?)null
            }), GenerationFinish.Completed, 10));
        }
    }
}
