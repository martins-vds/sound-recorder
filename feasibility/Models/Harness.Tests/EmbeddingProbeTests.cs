using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class EmbeddingProbeTests
{
    [Theory]
    [InlineData(0, 512)]
    [InlineData(2, 0)]
    public async Task RetrieveAsync_InvalidNativeConfiguration_RejectsBeforeEmbedding(int dimensions, int context)
    {
        var backend = new EmbeddingDouble([[1, 0]]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new EmbeddingProbe(backend, dimensions, context).RetrieveAsync("question", [new("s000001", null, null, "source")]));
        Assert.Empty(backend.Inputs);
    }

    [Fact]
    public async Task RetrieveAsync_EqualScoresAndExactContextBoundary_ReturnsStableSourceOrder()
    {
        var backend = new EmbeddingDouble([[1, 0], [1, 0], [1, 0]]) { TokenCount = 512 };
        var matches = await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("s000002", null, null, "second"), new("s000001", null, null, "first")], topK: 2);
        Assert.Equal(["s000001", "s000002"], matches.Select(m => m.Excerpt.Id));
    }

    [Fact]
    public async Task RetrieveAsync_AlreadyCancelled_RejectsBeforeEmbedding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var backend = new EmbeddingDouble([[1, 0]]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EmbeddingProbe(backend, dimensions: 2)
            .RetrieveAsync("question", [new("s000001", null, null, "source")], cancellationToken: cancellation.Token));
        Assert.Empty(backend.Inputs);
    }
    [Fact]
    public async Task RetrieveAsync_PortugueseQuery_UsesTaskPrefixesAndNormalizedCosine()
    {
        var backend = new EmbeddingDouble([[3, 4], [0, 2], [6, 8]]);
        var result = await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("Quem verifica os registros?",
            [new("s000005", null, null, "Preparar a lista."),
             new("s000006", 15000, 18000, "Ana vai verificar os registros.")], topK: 1);

        Assert.Equal("s000006", Assert.Single(result).Excerpt.Id);
        Assert.Equal(1, result[0].Similarity, 6);
        Assert.Equal([
            "search_query: Quem verifica os registros?",
            "search_document: Preparar a lista.",
            "search_document: Ana vai verificar os registros."], backend.Inputs);
    }

    public static IEnumerable<object[]> InvalidVectors =>
        [new object[] { new float[] { 0, 0 } }, new object[] { new float[] { 1 } },
         new object[] { new float[] { float.NaN, 1 } }, new object[] { new float[] { 1, float.PositiveInfinity } }];

    [Theory]
    [MemberData(nameof(InvalidVectors))]
    public async Task RetrieveAsync_InvalidNativeVector_ReportsFailure(float[] vector)
    {
        var probe = new EmbeddingProbe(new EmbeddingDouble([vector]), dimensions: 2);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            probe.RetrieveAsync("query", [new("s000001", null, null, "source")]));
        if (vector.All(value => value == 0))
        {
            Assert.Contains("normal", error.Message);
        }
        else
        {
            Assert.Contains("2", error.Message);
        }
    }

    [Fact]
    public async Task RetrieveAsync_PrefixAndSpecialTokensExceedContext_FailsBeforeNativeCall()
    {
        var backend = new EmbeddingDouble([[1, 0], [1, 0]]) { TokenCount = 513 };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("query", [new("s000001", null, null, "source")]));
        Assert.Contains(backend.TokenCount.ToString(), error.Message);
        Assert.Contains("512", error.Message);
        Assert.Empty(backend.Inputs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetrieveAsync_InvalidTopK_RejectsEmptySuccessShape(int topK)
    {
        var backend = new EmbeddingDouble([[1, 0], [1, 0]]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("query", [new("s000001", null, null, "source")], topK));
        Assert.Empty(backend.Inputs);
    }

    [Fact]
    public async Task RetrieveAsync_CancellationDuringCall_DoesNotStartNextEmbedding()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new EmbeddingDouble([[1, 0], [1, 0]]) { OnEmbed = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("query",
                [new("s000001", null, null, "source")], cancellationToken: cancellation.Token));
        Assert.Single(backend.Inputs);
    }

    [Fact]
    public async Task RetrieveAsync_NoSource_ReportsMissingEvidenceBeforeInference()
    {
        var backend = new EmbeddingDouble([[1, 0]]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question", []));
        Assert.Contains("source", error.Message);
        Assert.Empty(backend.Inputs);
    }

    [Fact]
    public async Task RetrieveAsync_CancelledDuringLastDocument_DoesNotPublishPartialMatches()
    {
        using var cancellation = new CancellationTokenSource();
        EmbeddingDouble? backend = null;
        backend = new EmbeddingDouble([[1, 0], [1, 0]]) { OnEmbed = () => { if (backend!.Inputs.Count == 2) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EmbeddingProbe(backend, dimensions: 2)
            .RetrieveAsync("query", [new("s000001", null, null, "source")], cancellationToken: cancellation.Token));
    }
}

internal sealed class EmbeddingDouble(float[][] vectors) : IEmbeddingBackend
{
    public int TokenCount { get; init; } = 10;
    public Action? OnEmbed { get; init; }
    public List<string> Inputs { get; } = [];
    public int CountTokens(string input) => TokenCount;
    public Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        OnEmbed?.Invoke();
        return Task.FromResult(vectors[Inputs.Count - 1]);
    }
}
