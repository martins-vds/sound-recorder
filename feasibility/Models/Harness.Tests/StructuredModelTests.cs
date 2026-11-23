using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
public sealed class StructuredModelTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AnswerAndExtract_SmallOriginalFixture_PersistsResolvableOriginalEvidence(string language)
    {
        var directory = Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY")
            ?? throw new InvalidOperationException("Configure the already approved external model cache.");
        var fixture = EvaluationFixture.Minute(language);
        using var backend = await LlamaGeneration.LoadAsync(directory);
        var probe = new GenerationProbe(backend);
        var root = Path.Combine(System.IO.Directory.GetCurrentDirectory(), "runs", $"structured-v2-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(root);
        var result = await probe.SummarizeAsync(fixture.Identity, RealModelTests.Sources(fixture.Excerpts.Take(2)),
            Path.Combine(root, "claims.sqlite"), language, TimeSpan.FromMinutes(2));
        Assert.Equal(AnalysisStatus.Completed, result.Status);
        using var ledger = ClaimLedger.Open(result.LedgerPath);
        Assert.Equal(fixture.Identity, ledger.ReadRun().Identity);
        foreach (var claim in ledger.ReadClaims())
        {
            Assert.All(ledger.ReadEvidence(claim.Id), evidence =>
                Assert.Equal(fixture.Excerpts.Single(e => e.Id == evidence.SourceId).Text, evidence.Quote));
        }
    }
}
