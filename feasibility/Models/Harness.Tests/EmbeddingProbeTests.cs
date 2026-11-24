using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class EmbeddingProbeTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task RetrieveAsync_BlankSource_ReportsMissingTextWithoutEmbeddingADocument(string text)
    {
        var backend = new EmbeddingDouble([[1, 0]]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
                [new("source", null, null, text)]));

        Assert.Contains("text", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["search_query: question"], backend.Inputs);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("!")]
    [InlineData("?")]
    public async Task RetrieveAsync_LongSource_PrefersACompleteSentenceOverALaterWordBoundary(string punctuation)
    {
        var sentence = new string('a', 600) + " " + new string('b', 249) + punctuation + " ";
        var text = sentence + "later words " + new string('c', 400);
        var backend = RangeBackend();

        var matches = await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("source", 10, 20, text)], topK: 20);

        Assert.Equal("search_document: " + sentence, backend.Inputs[1]);
        Assert.Equal("source@0", matches[0].Excerpt.Id);
        Assert.All(matches, match => Assert.Equal((10L, 20L), (match.Excerpt.StartMs, match.Excerpt.EndMs)));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task RetrieveAsync_NoCompleteSentence_PreservesTheWholeWordAndItsSeparator(string separator)
    {
        var word = new string('a', 600) + separator;
        var text = word + new string('b', 600);
        var backend = RangeBackend();

        await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("source", null, null, text)], topK: 20);

        Assert.Equal("search_document: " + word, backend.Inputs[1]);
    }

    [Fact]
    public async Task RetrieveAsync_OnlyBoundaryAtTheRangeMidpoint_KeepsTheLongerOriginalRange()
    {
        var first = new string('a', 511) + ". " + new string('b', 511);
        var text = first + new string('c', 300);
        var backend = RangeBackend();

        await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("source", null, null, text)], topK: 20);

        Assert.Equal("search_document: " + first, backend.Inputs[1]);
    }

    [Fact]
    public async Task RetrieveAsync_OneCharacterBeyondAFullRange_RetainsBothExactRanges()
    {
        var first = new string('a', 1024);
        var backend = RangeBackend();
        var matches = await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("source", null, null, first + "b")], topK: 20);

        Assert.Equal(["source@0", "source@1024"], matches.Select(match => match.Excerpt.Id));
        Assert.Equal([first, "b"], matches.Select(match => match.Excerpt.Text));
    }

    [Fact]
    public async Task RetrieveAsync_SurrogateAtTheRangeEnd_NeverPassesAHalfScalarToTheModel()
    {
        var first = new string('a', 1023);
        var text = first + "😀" + new string('b', 300);
        var backend = RangeBackend(input => new System.Text.UTF8Encoding(false, true).GetByteCount(input) == 0 ? 0 : 10);

        var matches = await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("source", null, null, text)], topK: 20);

        Assert.Equal([first, "😀" + new string('b', 300)], matches.Select(match => match.Excerpt.Text));
        Assert.Equal(["source@0", "source@1023"], matches.Select(match => match.Excerpt.Id));
    }

    [Fact]
    public async Task RetrieveAsync_TokenFittingAcrossASurrogate_PreservesDeterministicCompleteScalarRanges()
    {
        var backend = RangeBackend(input => input.Length);
        var matches = await new EmbeddingProbe(backend, dimensions: 2, contextTokens: 20).RetrieveAsync("q",
            [new("source", null, null, "A😀B")], topK: 20);

        Assert.Equal(["A", "😀B"], matches.Select(match => match.Excerpt.Text));
        Assert.Equal(["source@0", "source@1"], matches.Select(match => match.Excerpt.Id));
    }

    [Fact]
    public async Task RetrieveAsync_MultipleWordRanges_OverlapsRelationshipsWithoutSkippingOriginalText()
    {
        var text = string.Concat(Enumerable.Repeat("Ana reviews the draft by Friday. ", 80));
        var backend = RangeBackend();
        var matches = await new EmbeddingProbe(backend, dimensions: 2).RetrieveAsync("question",
            [new("source", null, null, text)], topK: 20);
        var ranges = matches.Select(match => new
        {
            Offset = int.Parse(match.Excerpt.Id.Split('@')[1], System.Globalization.CultureInfo.InvariantCulture),
            match.Excerpt.Text
        }).OrderBy(range => range.Offset).ToArray();

        Assert.True(ranges.Length > 2);
        Assert.Equal(0, ranges[0].Offset);
        Assert.True(ranges[1].Offset < ranges[0].Text.Length);
        var end = 0;
        foreach (var range in ranges)
        {
            Assert.True(range.Offset <= end, "Retrieval must not skip original text between ranges.");
            Assert.Equal(text.Substring(range.Offset, range.Text.Length), range.Text);
            Assert.True(range.Offset == 0 || char.IsWhiteSpace(text[range.Offset - 1]));
            end = Math.Max(end, range.Offset + range.Text.Length);
        }
        Assert.Equal(text.Length, end);
    }

    private static EmbeddingDouble RangeBackend(Func<string, int>? counter = null) =>
        new(Enumerable.Range(0, 30).Select(_ => new float[] { 1, 0 }).ToArray()) { TokenCounter = counter };

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
    public Func<string, int>? TokenCounter { get; init; }
    public Action? OnEmbed { get; init; }
    public List<string> Inputs { get; } = [];
    public int CountTokens(string input) => TokenCounter?.Invoke(input) ?? TokenCount;
    public Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        OnEmbed?.Invoke();
        return Task.FromResult(vectors[Inputs.Count - 1]);
    }
}
