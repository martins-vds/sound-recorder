using System.Text.Json.Nodes;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class PipelineBoundaryTests
{
    private const string Valid = """
        {"claims":[{"kind":"Topic","statement":"Access review","owner":{"state":"Absent","value":null},
        "deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}
        """;

    [Theory]
    [InlineData("statement-null")]
    [InlineData("statement-empty")]
    [InlineData("owner-null")]
    [InlineData("owner-stated-empty")]
    [InlineData("deadline-null")]
    [InlineData("deadline-unknown")]
    [InlineData("qualifiers-null")]
    [InlineData("qualifier-empty")]
    [InlineData("references-null")]
    [InlineData("references-empty")]
    [InlineData("references-negative")]
    [InlineData("references-too-many")]
    [InlineData("limitation-empty")]
    [InlineData("unknown-field")]
    public async Task ExtractAsync_InvalidRequiredRecordField_FailsAtomicallyWithExplicitDataError(string invalid)
    {
        using var area = new Area();
        var output = JsonNode.Parse(Valid)!;
        var claim = output["claims"]![0]!;
        switch (invalid)
        {
            case "statement-null": claim["statement"] = null; break;
            case "statement-empty": claim["statement"] = " "; break;
            case "owner-null": claim["owner"] = null; break;
            case "owner-stated-empty": claim["owner"] = JsonNode.Parse("""{"state":"Stated","value":""}"""); break;
            case "deadline-null": claim["deadline"] = null; break;
            case "deadline-unknown": claim["deadline"]!["state"] = "Unknown"; break;
            case "qualifiers-null": claim["qualifiers"] = null; break;
            case "qualifier-empty": claim["qualifiers"] = new JsonArray(" "); break;
            case "references-null": claim["references"] = null; break;
            case "references-empty": claim["references"] = new JsonArray(); break;
            case "references-negative": claim["references"] = new JsonArray(-1); break;
            case "references-too-many": claim["references"] = new JsonArray(Enumerable.Range(0, 17).Select(_ => (JsonNode?)JsonValue.Create(0)).ToArray()); break;
            case "limitation-empty": output["limitation"] = " "; break;
            case "unknown-field": claim["quote"] = "invented"; break;
        }
        var result = await new GenerationProbe(new GenerationDouble(output.ToJsonString())).ExtractAsync(
            new("attempt", new string('a', 64)), Source(), area.Path, "en", TimeSpan.FromSeconds(5));
        Assert.Equal(AnalysisStatus.Failed, result.Status);
        Assert.Contains(invalid == "unknown-field" ? "JsonException" : "InvalidDataException", result.Detail);
        using var ledger = ClaimLedger.Open(area.Path);
        Assert.Empty(ledger.ReadClaims());
        Assert.Empty(ledger.ReadBatches());
        Assert.Equal("Failed", ledger.ReadRun().Status);
    }

    [Theory]
    [InlineData("attempt-empty")]
    [InlineData("attempt-long")]
    [InlineData("hash-nonhex")]
    [InlineData("language-empty")]
    [InlineData("language-long")]
    [InlineData("question-empty")]
    [InlineData("zero-deadline")]
    public async Task AnswerAsync_InvalidRequestMetadata_RejectsBeforeSourceAndArtifact(string invalid)
    {
        using var area = new Area();
        var backend = new GenerationDouble(Valid);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new GenerationProbe(backend).AnswerAsync(
            new(invalid == "attempt-empty" ? "" : invalid == "attempt-long" ? new string('a', 129) : "attempt",
                invalid == "hash-nonhex" ? new string('g', 64) : new string('a', 64)),
            Source(), area.Path, invalid == "question-empty" ? " " : "What was discussed?",
            invalid == "language-empty" ? "" : invalid == "language-long" ? new string('a', 17) : "en",
            invalid == "zero-deadline" ? TimeSpan.Zero : TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(area.Path));
        Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [InlineData(-1L, 10L)]
    [InlineData(11L, 10L)]
    [InlineData(null, 10L)]
    [InlineData(10L, null)]
    public async Task ExtractAsync_InvalidOriginalTimeline_RejectsBeforeInference(long? start, long? end)
    {
        using var area = new Area();
        var backend = new GenerationDouble(Valid);
        var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
            Source(start, end), area.Path, "en", TimeSpan.FromSeconds(5));
        Assert.Equal(AnalysisStatus.Failed, result.Status);
        Assert.Contains("timestamps", result.Detail);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task ExtractAsync_UnrelatedCancellation_SurfacesFaultInsteadOfUserCancellation()
    {
        using var area = new Area();
        var cause = new OperationCanceledException("Backend cancelled without request cancellation.");
        var backend = new GenerationDouble(Valid) { OnGenerate = () => throw cause };
        Assert.Same(cause, await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Source(), area.Path, "en", TimeSpan.FromSeconds(5))));
        using var ledger = ClaimLedger.Open(area.Path);
        Assert.Equal("Failed", ledger.ReadRun().Status);
    }

    [Fact]
    public void Open_MissingArtifact_DoesNotCreateEmptyDatabase()
    {
        using var area = new Area();
        Assert.Throws<FileNotFoundException>(() => ClaimLedger.Open(area.Path));
        Assert.False(File.Exists(area.Path));
    }

    private static async IAsyncEnumerable<ExcerptInput> Source(long? start = null, long? end = null)
    {
        await Task.Yield();
        yield return new("s000001", start, end, new StringReader("Access review."));
    }

    private sealed class Area : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "claims.sqlite");
        public Area() => Directory.CreateDirectory(directory);
        public void Dispose() => Directory.Delete(directory, true);
    }
}
