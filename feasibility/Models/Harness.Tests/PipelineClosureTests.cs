using System.Text.Json;
using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class PipelineClosureTests
{
    [Theory]
    [InlineData(420)]
    [InlineData(432)]
    public async Task AnswerAsync_FiveFreshSources_PacksToTokenBudgetAndResolvesLocalIndices(int context)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new IndexedBackend();
            var result = await new GenerationProbe(backend, context, 64, 32).AnswerAsync(
                new("attempt", new string('a',64)), Sources(), Path.Combine(directory,"claims.sqlite"),
                "What was reported?", "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed,result.Status);
            Assert.Equal(2,result.Calls);
            Assert.Equal(300,result.MaximumPromptTokens);
            Assert.Equal([3,2],backend.BatchSizes);
            Assert.Equal(["source 1","source 2","source 3","source 4","source 5"],backend.SourceOrder);
            using var ledger=ClaimLedger.Open(result.LedgerPath);
            var claims=ledger.ReadClaims().ToArray();
            Assert.Equal(5,claims.Length);
            foreach(var claim in claims)
            {
                var evidence=Assert.Single(ledger.ReadEvidence(claim.Id));
                Assert.Equal(claim.Record.Statement,evidence.Quote);
                Assert.Equal(0,evidence.Offset);
            }
            Assert.All(ledger.ReadBatches(),batch=>Assert.True(batch.PromptTokens+batch.OutputBudget<context));
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                for(var index=1;index<=5;index++)
                {
                    yield return new($"s{index:D6}",null,null,new StringReader($"source {index}"));
                    await Task.Yield();
                }
            }
        }
        finally { Directory.Delete(directory,true); }
    }

    private sealed class IndexedBackend : IGenerationBackend
    {
        public List<int> BatchSizes {get;}=[];
        public List<string> SourceOrder {get;}=[];
        public string RenderPrompt(string instruction,string input)=>input;
        public int CountTokens(string prompt)
        {
            using var input=JsonDocument.Parse(prompt);
            return 100*input.RootElement.GetProperty("excerpts").GetArrayLength();
        }
        public Task<GenerationResult> GenerateAsync(string prompt,int outputTokens,CancellationToken cancellationToken)
        {
            using var input=JsonDocument.Parse(prompt);
            var excerpts=input.RootElement.GetProperty("excerpts").EnumerateArray().ToArray();
            BatchSizes.Add(excerpts.Length);
            SourceOrder.AddRange(excerpts.Select(e=>e.GetProperty("Quote").GetString()!));
            var text=JsonSerializer.Serialize(new
            {
                claims=excerpts.Select((source,index)=>new
                {
                    kind="Report",statement=source.GetProperty("Quote").GetString(),
                    owner=new {state="Absent",value=(string?)null},deadline=new {state="Absent",value=(string?)null},
                    qualifiers=Array.Empty<string>(),references=new[]{index}
                }).ToArray(),limitation=(string?)null
            });
            return Task.FromResult(new GenerationResult(text,GenerationFinish.Completed,20));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAsync_BackendFault_PersistsFailureAndSurfacesUnexpectedCause(bool operational)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "claims.sqlite");
        Exception cause = operational ? new IOException("Local model storage became unavailable.")
            : new InvalidOperationException("Broken backend invariant.");
        var backend = new GenerationDouble("") { OnGenerate = () => throw cause };
        try
        {
            var operation = new GenerationProbe(backend).ExtractAsync(new("attempt", new string('a', 64)),
                Sources(), path, "en", TimeSpan.FromSeconds(5));
            if (operational)
            {
                var result = await operation;
                Assert.Equal(AnalysisStatus.Failed, result.Status);
                Assert.Contains(cause.Message, result.Detail);
            }
            else
            {
                Assert.Same(cause, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
            }
            using var ledger = ClaimLedger.Open(path);
            Assert.Equal("Failed", ledger.ReadRun().Status);
            Assert.Contains(cause.Message, ledger.ReadRun().Detail);
            Assert.Empty(ledger.ReadClaims());
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("source"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task AnswerAsync_TinyConflictingReports_GroupsFreshSourcesAndResolvesBothReferences()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new RelationshipBackend("");
            var result = await new GenerationProbe(backend).AnswerAsync(new("attempt", new string('a', 64)),
                Sources(), Path.Combine(directory, "claims.sqlite"), "How many outages really occurred?", "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var claim = Assert.Single(ledger.ReadClaims(), c => c.Record.Kind == "Report");
            Assert.Equal("Two and zero reports conflict; actual count is unverified.", claim.Record.Statement);
            Assert.Equal(["unverified", "unresolved"], claim.Record.Qualifiers);
            Assert.Equal(["s000011", "s000012"], ledger.ReadEvidence(claim.Id).Select(e => e.SourceId));
            Assert.Equal(1, result.Calls);
            static async IAsyncEnumerable<ExcerptInput> Sources()
            {
                yield return new("s000011", 30000, 33000, new StringReader("One report says two outages."));
                await Task.Yield();
                yield return new("s000012", 33000, 36000, new StringReader("Another report says zero outages. Neither report is verified."));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task SummarizeAsync_RelationshipCrossesReadBoundary_PresentsCompleteOwnerDeadlineToBackend()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string relationship = "Ana will verify the access logs by 5 September 2027.";
        var original = new string('x', 998) + " . " + relationship + " End.";
        try
        {
            var backend = new RelationshipBackend(relationship);
            var result = await new GenerationProbe(backend).SummarizeAsync(new("attempt", new string('a', 64)),
                Source(), Path.Combine(directory, "claims.sqlite"), "en", TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            using var ledger = ClaimLedger.Open(result.LedgerPath);
            var action = Assert.Single(ledger.ReadClaims(), c => c.Record.Kind == "Action");
            Assert.Equal(new ClaimField("Stated", "Ana"), action.Record.Owner);
            Assert.Equal(new ClaimField("Stated", "5 September 2027"), action.Record.Deadline);
            var evidence = ledger.ReadEvidence(action.Id).ToArray();
            Assert.Contains(evidence, e => e.Quote.Contains(relationship, StringComparison.Ordinal));
            Assert.All(evidence, e =>
            {
                Assert.Equal(original.Substring((int)e.Offset, e.Length), e.Quote);
                Assert.Equal(12000, e.StartMs);
                Assert.Equal(24000, e.EndMs);
            });
            Assert.InRange(backend.MaximumSourceCharacters, 1, 4096);
            async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", 12000, 24000, new StringReader(original));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RelationshipBackend(string relationship) : IGenerationBackend
    {
        public int MaximumSourceCharacters { get; private set; }
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt) => prompt.Length / 4;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            using var input = JsonDocument.Parse(prompt);
            var sources = input.RootElement.GetProperty("excerpts").EnumerateArray()
                .Select(e => e.GetProperty("Quote").GetString()!).ToArray();
            MaximumSourceCharacters = Math.Max(MaximumSourceCharacters, sources.Sum(s => s.Length));
            var conflict = sources.Contains("One report says two outages.") &&
                sources.Contains("Another report says zero outages. Neither report is verified.");
            var complete = relationship.Length > 0 && sources.Any(s => s.Contains(relationship, StringComparison.Ordinal));
            var text = JsonSerializer.Serialize(new
            {
                claims = new[] { new
                {
                    kind = conflict ? "Report" : complete ? "Action" : "Topic",
                    statement = conflict ? "Two and zero reports conflict; actual count is unverified." : complete ? "Verify access logs" : "Background",
                    owner = new { state = complete ? "Stated" : "Absent", value = complete ? "Ana" : (string?)null },
                    deadline = new { state = complete ? "Stated" : "Absent", value = complete ? "5 September 2027" : null },
                    qualifiers = conflict ? ["unverified", "unresolved"] : Array.Empty<string>(),
                    references = Enumerable.Range(0, sources.Length).ToArray()
                } },
                limitation = (string?)null
            });
            return Task.FromResult(new GenerationResult(text, GenerationFinish.Completed, 40));
        }
    }
}
