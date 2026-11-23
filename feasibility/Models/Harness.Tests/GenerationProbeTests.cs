using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class GenerationProbeTests
{
    [Theory]
    [InlineData(11, 20)]
    [InlineData(10, 21)]
    public async Task ExtractAsync_ChangedOverlappingTimestamp_RejectsRetimingAndRetainsOriginalEvidence(long start, long end)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new GenerationDouble(ClaimOutput) { SingleRangeOnly = true };
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Sources(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Failed, result.Status);
            Assert.Equal(1, backend.Calls);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var original = Assert.Single(ledger.ReadEvidence(Assert.Single(ledger.ReadClaims()).Id));
            Assert.Equal(10, original.StartMs);
            Assert.Equal(20, original.EndMs);
            Assert.Equal("same source", original.Quote);
            async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("s000001", 10, 20, new StringReader("same source"));
                await Task.Yield();
                yield return new("s000002", 10, 20, new StringReader("other source"));
                yield return new("s000001", start, end, new StringReader("same source"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    private const string ClaimOutput = """
        {"claims":[{"kind":"Report","statement":"candidate","owner":{"state":"Absent","value":null},
        "deadline":{"state":"Absent","value":null},"qualifiers":["unverified"],"references":[0]}],"limitation":null}
        """;

    [Theory]
    [InlineData(GenerationFinish.TokenLimit, false, false, AnalysisStatus.TokenLimited)]
    [InlineData(GenerationFinish.Completed, true, false, AnalysisStatus.Cancelled)]
    [InlineData(GenerationFinish.Completed, false, true, AnalysisStatus.Failed)]
    public async Task ExtractAsync_SecondBatchIncomplete_RetainsOnlyCommittedInitialClaims(
        GenerationFinish finish, bool cancel, bool malformed, AnalysisStatus expected)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var backend = new GenerationDouble(ClaimOutput)
            {
                Responses = [ClaimOutput, malformed ? "{" : ClaimOutput],
                Finishes = [GenerationFinish.Completed, finish]
            };
            backend.OnGenerate = () => { if (cancel && backend.Calls == 2) cancellation.Cancel(); };
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Sources(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5), cancellation.Token);
            Assert.Equal(expected, result.Status);
            Assert.Equal(2, result.Calls);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var claim = Assert.Single(ledger.ReadClaims());
            Assert.Equal(new ClaimField("Absent", null), claim.Record.Owner);
            Assert.Equal(new ClaimField("Absent", null), claim.Record.Deadline);
            Assert.Equal("s000001", Assert.Single(ledger.ReadEvidence(claim.Id)).SourceId);
            Assert.Equal(expected.ToString(), ledger.ReadRun().Status);
            Assert.False(string.IsNullOrWhiteSpace(ledger.ReadRun().Detail));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExtractAsync_ConflictingOverlap_RejectsChangedSourceBeforeSecondNativeCall()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new GenerationDouble(ClaimOutput);
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Sources(sameId: true), Path.Combine(directory, "claims.sqlite"), "pt-BR", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Failed, result.Status);
            Assert.Equal(0, backend.Calls);
            Assert.Contains("changed", result.Detail);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExtractAsync_ExactOverlap_DeduplicatesWithoutRewritingAbsenceAndProvenance()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await new GenerationProbe(new GenerationDouble(ClaimOutput)).ExtractAsync(
                new("attempt", new string('a', 64)), Sources(true, true), Path.Combine(directory, "claims.sqlite"),
                "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal("first", Assert.Single(ledger.ReadEvidence(Assert.Single(ledger.ReadClaims()).Id)).Quote);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async IAsyncEnumerable<ExcerptInput> Sources(bool sameId = false, bool sameText = false)
    {
        yield return new("s000001", null, null, new StringReader("first"));
        await Task.Yield();
        yield return new(sameId ? "s000001" : "s000002", null, null, new StringReader(sameText ? "first" : "second"));
    }
}

internal sealed class GenerationDouble(string response) : IGenerationBackend
{
    public bool SingleRangeOnly { get; init; }
    public int TokenCount { get; init; } = 10;
    public string[]? Responses { get; init; }
    public GenerationFinish[]? Finishes { get; init; }
    public Action? OnGenerate { get; set; }
    public List<string> Prompts { get; } = [];
    public int Calls { get; private set; }
    public string RenderPrompt(string instruction, string input) => instruction + "\n" + input;
    public int CountTokens(string prompt)
    {
        using var input = System.Text.Json.JsonDocument.Parse(prompt[(prompt.IndexOf('\n') + 1)..]);
        return (SingleRangeOnly || Responses is not null || Finishes is not null) &&
            input.RootElement.GetProperty("excerpts").GetArrayLength() > 1 ? 4096 : TokenCount;
    }
    public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        Calls++;
        OnGenerate?.Invoke();
        return Task.FromResult(new GenerationResult(Responses?[Calls - 1] ?? response,
            Finishes?[Calls - 1] ?? GenerationFinish.Completed, 2));
    }
}
