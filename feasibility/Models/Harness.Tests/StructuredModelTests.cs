using System.Text.Json;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
public sealed class StructuredModelTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AnswerAndReduce_SmallOriginalBilingualFixture_ReturnsResolvableStructuredCandidates(string language)
    {
        var directory = Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY")
            ?? throw new InvalidOperationException("Set the explicitly approved external model cache first.");
        var fixture = EvaluationFixture.Minute(language);
        using var embedding = await LlamaEmbedding.LoadAsync(directory);
        using var backend = await LlamaGeneration.LoadAsync(directory);
        var probe = new GenerationProbe(backend, outputTokens: 512);
        var question = language == "en" ? "How many outages really occurred?" : "Quantas interrupções realmente ocorreram?";
        var matches = await new EmbeddingProbe(embedding).RetrieveAsync(question, fixture.Excerpts);
        Assert.Contains(matches, match => match.Excerpt.Id == "s000011");
        Assert.Contains(matches, match => match.Excerpt.Id == "s000012");
        var answer = await probe.AnswerAsync(fixture.Identity,
            [fixture.Excerpts[11], fixture.Excerpts[10], fixture.Excerpts[18]], question, language);
        var references = answer.Output.Claims.SelectMany(c => c.EvidenceIds).ToArray();
        Assert.Contains("s000011", references);
        Assert.Contains("s000012", references);
        var root = await probe.MapReduceAsync(fixture.Identity,
            [[fixture.Excerpts[1], fixture.Excerpts[3]], [fixture.Excerpts[4], fixture.Excerpts[5]]], language);
        Assert.Equal(["s000002", "s000004", "s000005", "s000006"], root.CoveredIds);
        Assert.All(root.Output.Claims, claim => Assert.All(claim.EvidenceIds, id => Assert.Contains(id, root.CoveredIds)));
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runs", $"structured-{language}.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            fixture.Identity, language, answer, root, matches,
            generation = new { backend.NativeModelBytes, backend.Metadata },
            embedding = new { embedding.NativeModelBytes, embedding.Metadata }
        }));
    }
}
