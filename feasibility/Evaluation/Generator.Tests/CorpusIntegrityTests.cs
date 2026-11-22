using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Evaluation.Generator.Tests;

public sealed class CorpusIntegrityTests
{
    [Fact]
    public void Verify_FrozenAssets_MatchesProtocolAndGeneratorIdentity()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "corpus-manifest.json")));
        foreach (var asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            using var stream = File.OpenRead(Path.Combine(root, asset.GetProperty("path").GetString()!));
            Assert.Equal(asset.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    [Theory]
    [InlineData("en", "minute")]
    [InlineData("en", "hour")]
    [InlineData("en", "12hour")]
    [InlineData("en", "untimed")]
    [InlineData("pt-BR", "minute")]
    [InlineData("pt-BR", "hour")]
    [InlineData("pt-BR", "12hour")]
    [InlineData("pt-BR", "untimed")]
    [Trait("type", "IntegrationTests")]
    public void Verify_GeneratedCorpus_MatchesFrozenHashTimelineAndClaimEvidence(string language, string size)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "corpus-manifest.json")));
        var identity = $"sr-eval-v1-{language}-{size}";
        var entry = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Single(file => file.GetProperty("corpusId").GetString() == identity);
        var sourcePath = Path.Combine(root, entry.GetProperty("transcriptPath").GetString()!);
        var coveragePath = Path.Combine(root, entry.GetProperty("coveragePath").GetString()!);
        AssertFile(sourcePath, entry.GetProperty("transcriptSha256").GetString()!, entry.GetProperty("utf8Bytes").GetInt64());
        AssertFile(coveragePath, entry.GetProperty("coverageSha256").GetString()!, entry.GetProperty("coverageBytes").GetInt64());
        AssertFile(Path.Combine(root, entry.GetProperty("seedPath").GetString()!),
            entry.GetProperty("seedSha256").GetString()!, null);
        Assert.Equal(identity + "-attempt-001", entry.GetProperty("successfulTranscriptionAttemptId").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("tokenCount").ValueKind);

        var timed = size != "untimed";
        long count = 0;
        using var coverage = File.ReadLines(coveragePath, Encoding.UTF8).GetEnumerator();
        foreach (var line in File.ReadLines(sourcePath, Encoding.UTF8))
        {
            count++;
            using var excerpt = JsonDocument.Parse(line);
            var row = excerpt.RootElement;
            Assert.Equal($"s{count:D6}", row.GetProperty("id").GetString());
            if (timed)
            {
                Assert.Equal((count - 1) * 3000, row.GetProperty("startMs").GetInt64());
                Assert.Equal(count * 3000, row.GetProperty("endMs").GetInt64());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("startMs").ValueKind);
                Assert.Equal(JsonValueKind.Null, row.GetProperty("endMs").ValueKind);
            }
            if (count % 20 == 0)
            {
                Assert.True(coverage.MoveNext(), "Missing expected batch decision evidence.");
                using var gold = JsonDocument.Parse(coverage.Current);
                Assert.Equal($"batch-{count / 20:D6}", gold.RootElement.GetProperty("claimId").GetString());
                Assert.Equal(row.GetProperty("id").GetString(), gold.RootElement.GetProperty("excerptId").GetString());
                Assert.Equal(row.GetProperty("text").GetString(), gold.RootElement.GetProperty("requiredMeaning").GetString());
                Assert.Equal(row.GetProperty("startMs").GetRawText(), gold.RootElement.GetProperty("startMs").GetRawText());
            }
        }
        Assert.Equal(entry.GetProperty("segments").GetInt64(), count);
        Assert.Equal(entry.GetProperty("uniqueBatchDecisions").GetInt64(), count / 20);
        Assert.False(coverage.MoveNext(), "Coverage contains claims from outside this transcript.");
        if (timed)
        {
            Assert.Equal(entry.GetProperty("savedAudioTimelineMs").GetInt64(), count * 3000);
        }
    }

    private static void AssertFile(string path, string expectedHash, long? expectedBytes)
    {
        using var stream = File.OpenRead(path);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(stream)));
        if (expectedBytes is not null)
        {
            Assert.Equal(expectedBytes.Value, stream.Length);
        }
        stream.Position = 0;
        Assert.Contains(stream.ReadByte(), new[] { (int)'{', (int)'[' });
    }
}
