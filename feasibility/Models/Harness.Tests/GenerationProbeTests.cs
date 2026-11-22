using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class GenerationProbeTests
{
    [Fact]
    public async Task MapAsync_ExactExcerpt_ReturnsIdentityAndCandidateEvidence()
    {
        var backend = new GenerationDouble("""{"claims":[{"text":"Ana checks logs.","evidenceIds":["s000006"]}],"limitation":null}""");
        var identity = new TranscriptIdentity("attempt-001", new string('a', 64));
        var source = new[] { new Excerpt("s000006", 15000, 18000, "Ana will check the access logs by 5 September 2027.") };
        var result = await new GenerationProbe(backend).MapAsync(identity, source, "en");

        Assert.Equal(identity, result.Identity);
        Assert.Equal(["s000006"], result.CoveredIds);
        Assert.Equal("Ana checks logs.", Assert.Single(result.Output.Claims).Text);
        Assert.Equal(["s000006"], result.Output.Claims[0].EvidenceIds);
    }

    [Fact]
    public async Task MapAsync_PromptAndOutputExceedContext_FailsBeforeInference()
    {
        var backend = new GenerationDouble("""{"claims":[],"limitation":"No evidence."}""") { TokenCount = 17 };
        var probe = new GenerationProbe(backend, contextTokens: 20, outputTokens: 4);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            probe.MapAsync(new("attempt", new string('a', 64)), [new("s000001", null, null, "source")], "en"));
        Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [InlineData("""{"claims":[{"text":"invented","evidenceIds":["s999999"]}],"limitation":null}""")]
    [InlineData("""{"claims":[{"text":"uncited","evidenceIds":[]}],"limitation":null}""")]
    [InlineData("""{"claims":[],"limitation":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"claims":[{"text":"","evidenceIds":["s000001"]}],"limitation":null}""")]
    public async Task MapAsync_UnresolvableOrEmptyOutput_RejectsSuccessShape(string response)
    {
        var probe = new GenerationProbe(new GenerationDouble(response));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            probe.MapAsync(new("attempt", new string('a', 64)), [new("s000001", null, null, "source")], "en"));
    }

    [Fact]
    public async Task MapReduceAsync_FourPortions_PreservesLeafIdentityAndRecursiveReferenceUnion()
    {
        var backend = new GenerationDouble("""{"claims":[],"limitation":"none"}""")
        {
            Responses = [
                Output("s000001"), Output("s000002"), Output("s000003"), Output("s000004"),
                Output("s000001", "s000002"), Output("s000003", "s000004"),
                Output("s000001", "s000002", "s000003", "s000004")]
        };
        var identity = new TranscriptIdentity("attempt-001", new string('a', 64));
        IReadOnlyList<Excerpt>[] portions = [
            [new("s000001", null, null, "first")], [new("s000002", null, null, "second")],
            [new("s000003", null, null, "third")], [new("s000004", null, null, "fourth")]];
        var root = await new GenerationProbe(backend).MapReduceAsync(identity, portions, "pt-BR");

        Assert.Equal(7, backend.Calls);
        Assert.Equal(["s000001", "s000002", "s000003", "s000004"], root.CoveredIds);
        Assert.Equal(root.CoveredIds, Assert.Single(root.Output.Claims).EvidenceIds);
        Assert.Equal(2, root.Children.Length);
        Assert.All(root.Children.SelectMany(n => n.Children), leaf => Assert.Equal(identity, leaf.Identity));
        Assert.Contains("fourth", backend.Prompts.Last());
        Assert.Contains("pt-BR", backend.Prompts.Last());
    }

    private static string Output(params string[] ids) =>
        System.Text.Json.JsonSerializer.Serialize(new CandidateOutput([new("candidate", ids)], null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapReduceAsync_CancelAtSafeBoundary_NoNextCallOrCompletedRoot(bool beforeCall)
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new GenerationDouble(Output("s000001")) { OnGenerate = () => cancellation.Cancel() };
        if (beforeCall)
        {
            cancellation.Cancel();
        }
        var probe = new GenerationProbe(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.MapReduceAsync(
            new("attempt", new string('a', 64)),
            [[new("s000001", null, null, "first")], [new("s000002", null, null, "second")]],
            "en", cancellation.Token));
        Assert.Equal(beforeCall ? 0 : 1, backend.Calls);
    }

    [Theory]
    [InlineData(4096, 0)]
    [InlineData(4096, -1)]
    [InlineData(0, 512)]
    public async Task MapAsync_InvalidBudget_RejectsUnboundedInference(int context, int output)
    {
        var backend = new GenerationDouble(Output("s000001"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new GenerationProbe(backend, context, output)
            .MapAsync(new("attempt", new string('a', 64)), [new("s000001", null, null, "source")], "en"));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task MapReduceAsync_ConflictingOverlap_RejectsChangedSourceBeforeInference()
    {
        var backend = new GenerationDouble(Output("s000001"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GenerationProbe(backend).MapReduceAsync(
            new("attempt", new string('a', 64)),
            [[new("s000001", null, null, "first")], [new("s000001", null, null, "changed")]], "en"));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task AnswerAsync_FreshPortugueseEvidence_SuppliesQuestionAndOriginalExcerpt()
    {
        var fixture = EvaluationFixture.Minute("pt-BR");
        var backend = new GenerationDouble(Output("s000006"));
        var result = await new GenerationProbe(backend).AnswerAsync(fixture.Identity,
            [fixture.Excerpts[5]], "Quem vai verificar os registros de acesso e até quando?", fixture.Language);
        Assert.Equal(fixture.Identity, result.Identity);
        Assert.Contains("Quem vai verificar", Assert.Single(backend.Prompts));
        Assert.Contains("pt-BR", backend.Prompts[0]);
        Assert.Contains("s000006", backend.Prompts[0]);
        Assert.DoesNotContain("s000005", backend.Prompts[0]);
    }
}

internal sealed class GenerationDouble(string response) : IGenerationBackend
{
    public int TokenCount { get; init; } = 10;
    public string[]? Responses { get; init; }
    public Action? OnGenerate { get; init; }
    public List<string> Prompts { get; } = [];
    public int Calls { get; private set; }
    public string RenderPrompt(string instruction, string input) => instruction + "\n" + input;
    public int CountTokens(string prompt) => TokenCount;
    public Task<string> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        Calls++;
        OnGenerate?.Invoke();
        return Task.FromResult(Responses?[Calls - 1] ?? response);
    }
}
