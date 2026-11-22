using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class EmbeddingProbeTests
{
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
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            probe.RetrieveAsync("query", [new("s000001", null, null, "source")]));
    }

    [Fact]
    public async Task RetrieveAsync_PrefixAndSpecialTokensExceedContext_FailsBeforeNativeCall()
    {
        var backend = new EmbeddingDouble([[1, 0], [1, 0]]) { TokenCount = 513 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("query", [new("s000001", null, null, "source")]));
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
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question", []));
        Assert.Empty(backend.Inputs);
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
