using Xunit;

namespace Models.Harness.Tests;

public sealed class EvaluationFixtureTests
{
    [Theory]
    [InlineData("en", "f5d48c8f7d5d28acd824827cdb9b6139971e259657696fb133178dc9a088f234")]
    [InlineData("pt-BR", "1cc4e34686cdb4e9095a2dbc6847ef5af0c2c69b2f8514f243c530e33e5865a2")]
    public void Minute_FrozenCorpus_ReusesExactSuccessfulAttemptAndSourceBytes(string language, string hash)
    {
        var fixture = EvaluationFixture.Minute(language);
        Assert.Equal(hash, fixture.Identity.TranscriptSha256);
        Assert.Equal($"sr-eval-v1-{language}-minute-attempt-001", fixture.Identity.SuccessfulTranscriptionAttemptId);
        Assert.Equal(20, fixture.Excerpts.Length);
        Assert.Equal("s000020", fixture.Excerpts[^1].Id);
        Assert.Equal(60000, fixture.Excerpts[^1].EndMs);
        Assert.Contains("000001", fixture.Excerpts[^1].Text);
    }
}
