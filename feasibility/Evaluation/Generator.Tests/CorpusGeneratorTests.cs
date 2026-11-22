using Evaluation.Generator;
using Xunit;

namespace Evaluation.Generator.Tests;

public sealed class CorpusGeneratorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Write_InvalidBlockCount_RejectsBeforeWriting(int blocks)
    {
        using var writer = new StringWriter();

        Assert.Throws<ArgumentOutOfRangeException>(() => CorpusGenerator.Write(writer, ["text"], blocks, true));
        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void Write_EmptySeed_RejectsBeforeWriting()
    {
        using var writer = new StringWriter();

        Assert.Throws<ArgumentException>(() => CorpusGenerator.Write(writer, [], 1, true));
        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void Write_UntimedTranscript_PreservesStableReferencesWithoutInventingTimestamps()
    {
        using var writer = new StringWriter();

        CorpusGenerator.Write(writer, ["Sem áudio."], 1, false);

        Assert.Equal("{\"id\":\"s000001\",\"startMs\":null,\"endMs\":null,\"text\":\"Sem áudio.\"}\n", writer.ToString());
    }

    [Fact]
    public void Write_RepeatedBilingualSeed_PreservesTextIdentityAndSavedAudioTimeline()
    {
        using var writer = new StringWriter();

        CorpusGenerator.Write(writer, ["Ação \"{block}\".", "Decide {block}."], 2, true);

        Assert.Equal(
            "{\"id\":\"s000001\",\"startMs\":0,\"endMs\":3000,\"text\":\"Ação \\\"000001\\\".\"}\n" +
            "{\"id\":\"s000002\",\"startMs\":3000,\"endMs\":6000,\"text\":\"Decide 000001.\"}\n" +
            "{\"id\":\"s000003\",\"startMs\":6000,\"endMs\":9000,\"text\":\"Ação \\\"000002\\\".\"}\n" +
            "{\"id\":\"s000004\",\"startMs\":9000,\"endMs\":12000,\"text\":\"Decide 000002.\"}\n",
            writer.ToString());
    }
}
