using System.Text.Json;
using System.Text.Json.Nodes;
using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class PipelineTests
{
    [Theory]
    [InlineData("statement", 512, AnalysisStatus.Completed)]
    [InlineData("statement", 513, AnalysisStatus.Failed)]
    [InlineData("owner", 128, AnalysisStatus.Completed)]
    [InlineData("owner", 129, AnalysisStatus.Failed)]
    [InlineData("qualifier", 128, AnalysisStatus.Completed)]
    [InlineData("qualifier", 129, AnalysisStatus.Failed)]
    [InlineData("qualifiers", 8, AnalysisStatus.Completed)]
    [InlineData("qualifiers", 9, AnalysisStatus.Failed)]
    [InlineData("claims", 16, AnalysisStatus.Completed)]
    [InlineData("claims", 17, AnalysisStatus.Failed)]
    public async Task ExtractAsync_CompactRecordBoundary_PreservesExactFieldsOrRejectsWithoutTruncation(
        string field, int size, AnalysisStatus expected)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = JsonNode.Parse(Structured("Ana"))!;
            var claim = output["claims"]![0]!;
            switch (field)
            {
                case "statement": claim["statement"] = new string('x', size); break;
                case "owner": claim["owner"]!["value"] = new string('x', size); break;
                case "qualifier": claim["qualifiers"]![0] = new string('x', size); break;
                case "qualifiers": claim["qualifiers"] = new JsonArray(Enumerable.Repeat("unverified", size).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()); break;
                case "claims": output["claims"] = new JsonArray(Enumerable.Range(0, size).Select(_ => claim.DeepClone()).ToArray()); break;
            }
            var result = await new GenerationProbe(new GenerationDouble(output.ToJsonString())).ExtractAsync(
                new("attempt", new string('a', 64)), Sources(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(expected, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            if (expected == AnalysisStatus.Failed)
            {
                Assert.Empty(ledger.ReadClaims());
                Assert.Contains("InvalidDataException", result.Detail);
            }
            else
            {
                var stored = Assert.Single(ledger.ReadClaims()).Record;
                if (field == "statement") Assert.Equal(new string('x', 512), stored.Statement);
                if (field == "owner") Assert.Equal(new string('x', 128), stored.Owner.Value);
                if (field == "qualifier") Assert.Equal(new string('x', 128), stored.Qualifiers[0]);
                if (field == "qualifiers") Assert.Equal(8, stored.Qualifiers.Length);
            }
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("source"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("statement")]
    [InlineData("owner-state")]
    [InlineData("deadline-state")]
    [InlineData("deadline-value")]
    [InlineData("qualifier")]
    public async Task ExtractAsync_NonEqualStructuredFields_RetainsBothRecordsWithoutSemanticMerging(string field)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var changed = JsonNode.Parse(Structured("Ana"))!;
            var record = changed["claims"]![0]!;
            switch (field)
            {
                case "kind": record["kind"] = "Decision"; break;
                case "statement": record["statement"] = "Review logs"; break;
                case "owner-state": record["owner"] = JsonNode.Parse("""{"state":"Absent","value":null}"""); break;
                case "deadline-state": record["deadline"] = JsonNode.Parse("""{"state":"Absent","value":null}"""); break;
                case "deadline-value": record["deadline"]!["value"] = "Monday"; break;
                case "qualifier": record["qualifiers"] = new JsonArray("unresolved"); break;
            }
            var backend = new GenerationDouble("") { Responses = [Structured("Ana"), changed.ToJsonString()] };
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Sources(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var records = ledger.ReadClaims().ToArray();
            Assert.Equal(2, records.Length);
            Assert.All(records, claim => Assert.Single(ledger.ReadEvidence(claim.Id)));
            Assert.Equal("s000001", Assert.Single(ledger.ReadEvidence(records[0].Id)).SourceId);
            Assert.Equal("s000002", Assert.Single(ledger.ReadEvidence(records[1].Id)).SourceId);
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("s000001", null, null, new StringReader("first"));
                await Task.Yield();
                yield return new("s000002", null, null, new StringReader("second"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExtractAsync_HonestModelLimitation_PersistsPartialStatusAndNativeBudgetDiagnostics()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new GenerationDouble("""{"claims":[],"limitation":"No stated owner."}""");
            var result = await new GenerationProbe(backend, outputTokens: 384).ExtractAsync(
                new("attempt", new string('a', 64)), Sources(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Partial, result.Status);
            Assert.Contains("limitations",result.Detail!);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Empty(ledger.ReadClaims());
            Assert.Equal("Partial", ledger.ReadRun().Status);
            var diagnostic = Assert.Single(ledger.ReadBatches());
            Assert.Equal("No stated owner.", diagnostic.Limitation);
            Assert.Equal(GenerationFinish.Completed, diagnostic.Finish);
            Assert.Equal(384, diagnostic.OutputBudget);
            Assert.Equal(2, diagnostic.GeneratedTokens);
            Assert.Equal(10, diagnostic.PromptTokens);
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("No owner assigned."));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ExtractAsync_OnlySurrogatePairFits_DoesNotLoseOrRejectTheFirstCompleteScalar()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new PackingBackend(Structured("Ana"))
            {
                TokenCounter = prompt =>
                {
                    using var input = JsonDocument.Parse(prompt[(prompt.IndexOf('\n') + 1)..]);
                    return input.RootElement.GetProperty("excerpts")[0].GetProperty("Quote").GetString()!.Length == 3 ? 100 : 1;
                }
            };
            var result = await new GenerationProbe(backend, 6, 2).ExtractAsync(new("attempt", new string('a', 64)),
                Source(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Equal(["😀", "a"], ledger.ReadEvidence(Assert.Single(ledger.ReadClaims()).Id).Select(e => e.Quote));
            static async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("😀a"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("bad-hash")]
    [InlineData("too-long-question")]
    [InlineData("unbounded-deadline")]
    public async Task AnswerAsync_UnboundedOrInvalidRequest_RejectsBeforeCreatingArtifact(string invalid)
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), Guid.NewGuid() + ".sqlite");
        var backend = new GenerationDouble(Structured("Ana"));
        try
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => new GenerationProbe(backend).AnswerAsync(
                new("attempt", invalid == "bad-hash" ? "bad" : new string('a', 64)), Source(), path,
                invalid == "too-long-question" ? new string('q', 1025) : "Who checks?",
                "en", invalid == "unbounded-deadline" ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(path));
            Assert.Equal(0, backend.Calls);
            static async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("source"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(0, 256)]
    [InlineData(4096, 0)]
    [InlineData(4096, -1)]
    public async Task ExtractAsync_InvalidBudget_FailsBeforeReadingSourceOrCreatingArtifact(int context, int output)
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), Guid.NewGuid() + ".sqlite");
        var backend = new GenerationDouble(Structured("Ana"));
        var read = false;
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new GenerationProbe(backend, context, output)
                .ExtractAsync(new("attempt", new string('a', 64)), Source(), path, "en", TimeSpan.FromSeconds(1)));
            Assert.Equal(0, backend.Calls);
            Assert.False(read);
            Assert.False(File.Exists(path));
            async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                read = true;
                yield return new("source", null, null, new StringReader("should not be read"));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ExtractAsync_RepeatedSourceWithMissingTail_RejectsIdentityEvenWhenFirstRangeMatches()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await new GenerationProbe(new GenerationDouble(Structured("Ana"))).ExtractAsync(
                new("attempt", new string('a', 64)), Sources(), Path.Combine(directory, "claims.sqlite"),
                "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Failed, result.Status);
            Assert.Contains("length", result.Detail);
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("s000001", 10, 20, new StringReader(new string('a', 2000)));
                await Task.Yield();
                yield return new("s000001", 10, 20, new StringReader(new string('a', 1024)));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAsync_EmptyOrInvalidUnicodeSource_DoesNotClaimCompleteTraversal(bool invalidUnicode)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var text = invalidUnicode ? new string((char)0xD800, 1) : "";
            var backend = new GenerationDouble(Structured("Ana"));
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Source(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Failed, result.Status);
            Assert.Equal(0, backend.Calls);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Empty(ledger.ReadClaims());
            async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader(text));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("""{"claims":[{"kind":"Action","statement":"Check","owner":{"state":"Absent","value":"invented"},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}""")]
    [InlineData("""{"claims":[{"kind":"Fact","statement":"Check","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}""")]
    [InlineData("""{"claims":[],"limitation":null}""")]
    [InlineData("""{"claims":[null],"limitation":null}""")]
    [InlineData("""{"claims":[{"kind":"Action","statement":"Check","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[1]}],"limitation":null}""")]
    [InlineData("""{"claims":[{"kind":"Action","statement":"Check","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0,1]}],"limitation":null}""")]
    public async Task ExtractAsync_InvalidStructuredRecord_RejectsEntireBatchWithoutPartialClaimCommit(string response)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new GenerationDouble(response);
            var result = await new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Source(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Failed, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Detail));
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            Assert.Empty(ledger.ReadClaims());
            Assert.Equal("Failed", ledger.ReadRun().Status);
            static async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("source"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task AnswerAsync_ExpiredQuestion_SummaryGetsIndependentDeadlineAndStageBudget()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new PackingBackend(Structured("Ana"));
            backend.OnGenerate = async (_, tokens, cancellation) =>
            {
                backend.Budgets.Add(tokens);
                if (tokens == 128) await Task.Delay(1000, cancellation);
            };
            var probe = new GenerationProbe(backend, outputTokens: 256, answerTokens: 128);
            var identity = new TranscriptIdentity("attempt", new string('a', 64));
            var answer = await probe.AnswerAsync(identity, Source(), Path.Combine(directory, "answer.sqlite"),
                "Who checks logs?", "en", TimeSpan.FromMilliseconds(200));
            var summary = await probe.SummarizeAsync(identity, Source(), Path.Combine(directory, "summary.sqlite"),
                "en", TimeSpan.FromSeconds(1));

            Assert.Equal(AnalysisStatus.Cancelled, answer.Status);
            Assert.Equal(AnalysisStatus.Completed, summary.Status);
            Assert.Equal([128, 256], backend.Budgets);
            using var cancelled = ClaimLedger.Open(answer.LedgerPath);
            Assert.Equal("Cancelled", cancelled.ReadRun().Status);
            Assert.Empty(cancelled.ReadClaims());
            using var completed = ClaimLedger.Open(summary.LedgerPath);
            Assert.Equal(identity, completed.ReadRun().Identity);
            Assert.Single(completed.ReadClaims());

            static async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("Ana checks logs."));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task SummarizeAsync_OversizedUnicodeAndLongStream_BoundsPromptsAndRetainsExactSourceRanges()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = new string('x', 1023) + "😀" + string.Concat(Enumerable.Repeat("ação😀<|end|> ", 3000));
            var guarded = new GuardedSourceReader(new StringReader(original));
            var backend = new PackingBackend(Structured("Ana"));
            var result = await new GenerationProbe(backend, contextTokens: 1400, outputTokens: 256)
                .SummarizeAsync(new("attempt", new string('a', 64)), Sources(), Path.Combine(directory, "claims.sqlite"),
                    "pt-BR", TimeSpan.FromSeconds(30));

            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.InRange(result.MaximumLiveSourceCharacters, 6658, 16384);
            Assert.True(result.MaximumLiveSourceCharacters>=backend.Prompts.Max(p=>p.Length)+1025,
                "The reported live bound must include at least the largest final prompt and the still-owned read buffer.");
            Assert.All(backend.Prompts, prompt => Assert.True(backend.CountTokens(prompt) + 256 < 1400));
            Assert.All(backend.Prompts, prompt => Assert.DoesNotContain("<|end|>", prompt));
            Assert.True(guarded.SpareReads > 0);
            Assert.Equal(1024, guarded.MaximumReadRequest);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var claim = Assert.Single(ledger.ReadClaims());
            var pieces = ledger.ReadEvidence(claim.Id).Where(e => e.SourceId == "long").OrderBy(e => e.Offset).ToArray();
            long offset = 0;
            var reconstructed = new System.Text.StringBuilder();
            foreach (var piece in pieces)
            {
                Assert.True(piece.Offset <= offset);
                Assert.Equal(original.Substring((int)piece.Offset, piece.Length), piece.Quote);
                Assert.Equal(piece.Quote.Length, piece.Length);
                Assert.Equal(12000, piece.StartMs);
                Assert.Equal(24000, piece.EndMs);
                Assert.DoesNotContain("\uFFFD", piece.Quote);
                var start = (int)Math.Min(piece.Length, offset - piece.Offset);
                reconstructed.Append(piece.Quote[start..]);
                offset = Math.Max(offset, piece.Offset + piece.Length);
            }
            Assert.Equal(original, reconstructed.ToString());
            Assert.Equal(original.Length, offset);
            Assert.Equal(original.Length,Assert.Single(ledger.ReadCoverage(),c=>c.SourceId=="long").Characters);
            Assert.Equal(200 + pieces.Length, ledger.ReadEvidence(claim.Id).Count());

            async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("long", 12000, 24000, guarded);
                for (var i = 0; i < 200; i++)
                {
                    await Task.Yield();
                    yield return new($"s{i:D6}", null, null, new StringReader("short"));
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task SummarizeAsync_EqualClaimsAndConflictingOwner_PersistsExactRecordsAndUnionEvidence()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new GenerationDouble("")
            {
                Responses =
            [
                Structured("Ana"),
                Structured("Ana"),
                Structured("Bea")
            ]
            };
            var path = Path.Combine(directory, "claims.sqlite");
            var result = await new GenerationProbe(backend).SummarizeAsync(
                new("attempt", new string('a', 64)), Sources(), path, "en", TimeSpan.FromSeconds(5));

            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var reopened = ClaimLedger.Open(path);
            var claims = reopened.ReadClaims().ToArray();
            Assert.Equal(2, claims.Length);
            Assert.All(claims, claim =>
            {
                Assert.Equal("Action", claim.Record.Kind);
                Assert.Equal("Check logs", claim.Record.Statement);
                Assert.Equal(new ClaimField("Stated", "Friday"), claim.Record.Deadline);
                Assert.Equal(["unverified"], claim.Record.Qualifiers);
            });
            var ana = claims.Single(c => c.Record.Owner.Value == "Ana");
            var evidence = reopened.ReadEvidence(ana.Id).ToArray();
            Assert.Equal(["s000001", "s000002"], evidence.Select(e => e.SourceId));
            Assert.Equal(["first", "second"], evidence.Select(e => e.Quote));
            Assert.Single(reopened.ReadEvidence(claims.Single(c => c.Record.Owner.Value == "Bea").Id));
        }

        finally
        {
            Directory.Delete(directory, true);
        }

        static async IAsyncEnumerable<ExcerptInput> Sources()
        {
            yield return new("s000001", 10, 20, new StringReader("first"));
            await Task.Yield();
            yield return new("s000002", 20, 30, new StringReader("second"));
            yield return new("s000003", 30, 40, new StringReader("third"));
        }
    }

    private static string Structured(string owner) => JsonSerializer.Serialize(new
    {
        claims = new[] { new
        {
            kind = "Action", statement = "Check logs",
            owner = new { state = "Stated", value = owner },
            deadline = new { state = "Stated", value = "Friday" },
            qualifiers = new[] { "unverified" }, references = new[] { 0 }
        } },
        limitation = (string?)null
    });
    [Fact]
    public async Task GenerateAsync_NativeLengthFinish_ReturnsExplicitIncompleteResult()
    {
        var native = new NativeDouble { Finish = GenerationFinish.TokenLimit };
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load,
            (_, _, _) => Task.CompletedTask);

        var result = await generation.GenerateAsync("prompt", 8, CancellationToken.None);

        Assert.Equal(GenerationFinish.TokenLimit, result.Finish);
        Assert.Equal("firstsecond", result.Text);
        Assert.Equal(2, result.GeneratedTokens);
    }
}

internal sealed class PackingBackend(string response) : IGenerationBackend
{
    public List<string> Prompts { get; } = [];
    public List<int> Budgets { get; } = [];
    public Func<string, int, CancellationToken, Task>? OnGenerate { get; set; }
    public Func<string, int>? TokenCounter { get; init; }
    public string RenderPrompt(string instruction, string input) => instruction + "\n" + input;
    public int CountTokens(string prompt) => TokenCounter?.Invoke(prompt) ?? (prompt.Length + 3) / 4;
    public async Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        if (OnGenerate is not null) await OnGenerate(prompt, outputTokens, cancellationToken);
        using var input = JsonDocument.Parse(prompt[(prompt.IndexOf('\n') + 1)..]);
        var output = JsonNode.Parse(response)!;
        foreach (var claim in output["claims"]!.AsArray())
            claim!["references"] = new JsonArray(Enumerable.Range(0, input.RootElement.GetProperty("excerpts").GetArrayLength())
                .Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        return new GenerationResult(output.ToJsonString(), GenerationFinish.Completed, 2);
    }
}

internal sealed class GuardedSourceReader(TextReader inner) : TextReader
{
    public int MaximumReadRequest { get; private set; }
    public int SpareReads { get; private set; }

    public override ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        Assert.InRange(buffer.Length, 1, 1024);
        MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
        return inner.ReadBlockAsync(buffer, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        Assert.Equal(1, buffer.Length);
        SpareReads++;
        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Whole-excerpt draining is not permitted by this source boundary.");

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
