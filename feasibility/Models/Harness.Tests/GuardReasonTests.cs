using System.Text.Json;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class GuardReasonTests
{
    [Fact]
    public async Task ExtractAsync_MiddleBatchHitsNativeCap_StopsBeforeReadingRemainingSources()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var requested = 0;
        try
        {
            const string valid = """{"claims":[{"kind":"Report","statement":"review","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}""";
            var backend = new GenerationDouble(valid) { SingleRangeOnly = true, Finishes = [GenerationFinish.Completed, GenerationFinish.TokenLimit] };
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Sources(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.TokenLimited, result.Status);
            Assert.Equal(2, backend.Calls);
            Assert.Equal(3, requested);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal("s000001", Assert.Single(ledger.ReadEvidence(Assert.Single(ledger.ReadClaims()).Id)).SourceId);
            Assert.Equal([GenerationFinish.Completed, GenerationFinish.TokenLimit], ledger.ReadBatches().Select(b => b.Finish));
            async IAsyncEnumerable<ExcerptInput> Sources()
            {
                for (var i = 1; i <= 5; i++)
                {
                    requested++;
                    yield return new($"s{i:D6}", null, null, new StringReader("source"));
                    await Task.Yield();
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAsync_InvalidSourceIdentity_PersistsFailureAndSurfacesMetadataReason(bool unicode)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "claims.sqlite");
        try
        {
            var backend = new GenerationDouble("");
            var operation = new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Source(), path, "en", TimeSpan.FromSeconds(5));
            if (unicode)
            {
                var result = await operation;
                Assert.Equal(AnalysisStatus.Failed, result.Status);
                Assert.Contains("Unicode", result.Detail!);
            }
            else
            {
                var error = await Assert.ThrowsAsync<ArgumentException>(() => operation);
                Assert.Contains("metadata", error.Message);
            }
            using var ledger = ClaimLedger.Open(path);
            Assert.Equal("Failed", ledger.ReadRun().Status);
            Assert.Contains(unicode ? "Unicode" : "metadata", ledger.ReadRun().Detail!);
            Assert.Equal(0, backend.Calls);
            async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new(unicode ? new string((char)0xD800, 1) : "", null, null, new StringReader("source"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("no-sources")]
    [InlineData("empty-source")]
    [InlineData("bad-unicode")]
    [InlineData("no-scalar-fits")]
    [InlineData("no-output")]
    [InlineData("mixed-references")]
    [InlineData("shortened-original")]
    [InlineData("too-many-claims")]
    public async Task ExtractAsync_OperationalGuard_ExplainsActionableCauseBeyondExceptionType(string failure)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = failure == "no-output" ? "null" :
                failure == "mixed-references"
                ? """{"claims":[{"kind":"Report","statement":"review","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0,3]}],"limitation":null}"""
                : """{"claims":[{"kind":"Report","statement":"review","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}""";
            if (failure == "too-many-claims")
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(output)!;
                var item = node["claims"]![0]!;
                node["claims"] = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 17).Select(_ => item.DeepClone()).ToArray());
                output = node.ToJsonString();
            }
            var backend = new GenerationDouble(output) { TokenCount = failure == "no-scalar-fits" ? 4096 : 10 };
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Sources(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Failed, result.Status);
            Assert.Matches(@"^[A-Za-z]+Exception: \S", result.Detail!);
            var reason = failure switch
            {
                "no-sources" => "source",
                "empty-source" => "text",
                "bad-unicode" => "Unicode",
                "no-scalar-fits" => "scalar",
                "no-output" => "output",
                "mixed-references" => "reference",
                "shortened-original" => "length",
                "too-many-claims" => "claims",
                _ => throw new InvalidOperationException()
            };
            Assert.Contains(reason, result.Detail!, StringComparison.OrdinalIgnoreCase);
            async IAsyncEnumerable<ExcerptInput> Sources()
            {
                await Task.Yield();
                if (failure == "no-sources") yield break;
                var text = failure == "empty-source" ? "" : failure == "bad-unicode" ? new string((char)0xD800, 1) :
                    failure == "shortened-original" ? new string('x', 2000) : "review";
                yield return new("s000001", null, null, new StringReader(text));
                if (failure == "shortened-original") yield return new("s000001", null, null, new StringReader(new string('x', 1024)));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("mixed-hash")]
    [InlineData("unicode-question")]
    [InlineData("unicode-language")]
    public async Task AnswerAsync_InvalidMetadata_RejectsMalformedRequestBeforeArtifact(string failure)
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), Guid.NewGuid() + ".sqlite");
        var invalid = new string((char)0xD800, 1);
        var backend = new GenerationDouble("");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new GenerationProbe(backend).AnswerAsync(
            new("attempt", failure == "mixed-hash" ? new string('a', 63) + "g" : new string('a', 64)), Sources(), path,
            failure == "unicode-question" ? invalid : "Who verifies?", failure == "unicode-language" ? invalid : "en", TimeSpan.FromSeconds(5)));
        Assert.Contains(failure == "mixed-hash" ? "SHA-256" : "Unicode", error.Message);
        Assert.False(File.Exists(path));
        Assert.Equal(0, backend.Calls);
        static async IAsyncEnumerable<ExcerptInput> Sources() { await Task.Yield(); yield break; }
    }

    [Fact]
    public async Task ExtractAsync_ExplicitLimitedGeneration_StoresUncommittedNativeBudgetReason()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new GenerationDouble("not JSON") { Finishes = [GenerationFinish.TokenLimit] };
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)), Sources(),
                Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.TokenLimited, result.Status);
            Assert.Contains("cap", result.Detail!);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Empty(ledger.ReadClaims());
            var diagnostic = Assert.Single(ledger.ReadBatches());
            Assert.Equal(GenerationFinish.TokenLimit, diagnostic.Finish);
            Assert.Contains("uncommitted", diagnostic.Limitation!);
            static async IAsyncEnumerable<ExcerptInput> Sources() { await Task.Yield(); yield return new("s000001", null, null, new StringReader("review")); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
