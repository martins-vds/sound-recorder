using System.Text.Json.Nodes;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class ExactBoundaryTests
{
    [Theory]
    [InlineData("Topic")]
    [InlineData("Decision")]
    [InlineData("Action")]
    [InlineData("Report")]
    public async Task ExtractAsync_ValidKindAndSixteenReferences_PreservesRecordAndUnionsDuplicates(string kind)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = JsonNode.Parse("""
                {"claims":[{"kind":"Topic","statement":"Access review","owner":{"state":"Absent","value":null},
                "deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}
                """)!;
            output["claims"]![0]!["kind"] = kind;
            output["claims"]![0]!["references"] = new JsonArray(Enumerable.Range(0, 16).Select(_ => (JsonNode?)JsonValue.Create(0)).ToArray());
            var result = await new GenerationProbe(new GenerationDouble(output.ToJsonString())).ExtractAsync(
                new("attempt", new string('a', 64)), Source("s000001"), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var claim = Assert.Single(ledger.ReadClaims());
            Assert.Equal(kind, claim.Record.Kind);
            Assert.Single(ledger.ReadEvidence(claim.Id));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(0L, 10L)]
    [InlineData(10L, 10L)]
    public async Task ExtractAsync_ValidZeroOrEqualTimelineAndMaxMetadata_PreservesExactInput(long start, long end)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var id = new string('s', 128);
            var backend = new GenerationDouble("""
                {"claims":[{"kind":"Topic","statement":"Access review","owner":{"state":"Absent","value":null},
                "deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}
                """);
            var identity = new TranscriptIdentity(new string('a', 128), new string('a', 64));
            var result = await new GenerationProbe(backend).AnswerAsync(identity, Source(id, start, end),
                Path.Combine(directory, "claims.sqlite"), new string('q', 1024), new string('l', 16), TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(identity, ledger.ReadRun().Identity);
            var evidence = Assert.Single(ledger.ReadEvidence(Assert.Single(ledger.ReadClaims()).Id));
            Assert.Equal(id, evidence.SourceId);
            Assert.Equal(start, evidence.StartMs);
            Assert.Equal(end, evidence.EndMs);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async IAsyncEnumerable<ExcerptInput> Source(string id, long? start = null, long? end = null)
    {
        await Task.Yield();
        yield return new(id, start, end, new StringReader("Access review."));
    }
}
