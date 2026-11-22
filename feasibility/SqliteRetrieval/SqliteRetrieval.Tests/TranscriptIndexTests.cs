using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace SqliteRetrieval.Tests;

[Trait("type", "IntegrationTests")]
public sealed class TranscriptIndexTests(ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, "TestResults", Guid.NewGuid().ToString("N"));
    private readonly IndexIdentity identity = new("attempt-a", "sha256-source-a", "sha256-model-a", 3, "mean", "none", "chunks-v1");

    [Fact]
    public void Search_CompletedIndexReopened_ReturnsExactCosineAndStableExcerpt()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cache.db");
        using (var index = new TranscriptIndex(path))
        {
            index.Begin(identity, 3);
            index.Append(identity, new(0, "excerpt-a", 1250, [2, 0, 0]));
            index.Append(identity, new(1, "excerpt-b", null, [1, 1, 0]));
            index.Append(identity, new(2, "excerpt-c", 4000, [-1, 0, 0]));
            index.Complete(identity);
        }

        using var reopened = new TranscriptIndex(path);
        var matches = reopened.Search(identity, [1, 0, 0], 3);
        Assert.Equal(["excerpt-a", "excerpt-b", "excerpt-c"], matches.Select(match => match.Reference));
        Assert.Equal([0, 1, 2], matches.Select(match => match.Ordinal));
        Assert.Equal(1250, matches[0].TimestampMilliseconds);
        Assert.Null(matches[1].TimestampMilliseconds);
        Assert.Equal(1, matches[0].Similarity, 8);
        Assert.Equal(0.7071067811865475, matches[1].Similarity, 8);
        Assert.Equal(-1, matches[2].Similarity, 8);
    }

    [Fact]
    public void Begin_InterruptedIndexReopened_ResumesOnlyCompatibleCompletedExcerpts()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cache.db");
        using (var index = new TranscriptIndex(path))
        {
            Assert.Equal(new IndexProgress(0, false), index.Begin(identity, 2));
            index.Append(identity, new(0, "excerpt-a", null, [1, 0, 0]));
            Assert.Throws<InvalidOperationException>(() => index.Search(identity, [1, 0, 0], 1));
            Assert.Throws<InvalidOperationException>(() => index.Complete(identity));
        }
        using var reopened = new TranscriptIndex(path);
        Assert.Equal(new IndexProgress(1, false), reopened.Begin(identity, 2));
        Assert.Throws<InvalidOperationException>(() => reopened.Begin(identity, 3));
        reopened.Append(identity, new(1, "excerpt-b", null, [0, 1, 0]));
        reopened.Complete(identity);
        Assert.Equal(new IndexProgress(2, true), reopened.Begin(identity, 2));
        Assert.Single(reopened.Search(identity, [0, 1, 0], 1));
        Assert.Throws<InvalidOperationException>(() => reopened.Append(identity, new(2, "excerpt-c", null, [0, 0, 1])));
    }

    [Fact]
    public void DeleteCache_ChangedModelRejected_PreservesOtherAttemptsAndAuthoritativeFiles()
    {
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "transcript.txt");
        var summary = Path.Combine(directory, "summary.txt");
        var discussion = Path.Combine(directory, "discussion.txt");
        File.WriteAllText(source, "synthetic source");
        File.WriteAllText(summary, "synthetic completed summary");
        File.WriteAllText(discussion, "synthetic discussion");
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        var other = identity with { Attempt = "attempt-b" };
        foreach (var target in new[] { identity, other })
        {
            index.Begin(target, 1);
            index.Append(target, new(0, "same-reference", null, [1, 0, 0]));
            index.Complete(target);
        }
        var changed = identity with { ModelHash = "sha256-model-b" };
        Assert.Throws<InvalidOperationException>(() => index.Search(changed, [1, 0, 0], 1));
        Assert.Throws<InvalidOperationException>(() => index.Begin(changed, 1));
        index.DeleteCache(identity.Attempt);
        Assert.Throws<InvalidOperationException>(() => index.Search(identity, [1, 0, 0], 1));
        Assert.Single(index.Search(other, [1, 0, 0], 1));
        Assert.Equal(new IndexProgress(0, false), index.Begin(changed, 1));
        Assert.Equal("synthetic source", File.ReadAllText(source));
        Assert.Equal("synthetic completed summary", File.ReadAllText(summary));
        Assert.Equal("synthetic discussion", File.ReadAllText(discussion));
    }

    [Theory]
    [InlineData(1, 1f, 0f, 0f)]
    [InlineData(-1, 1f, 0f, 0f)]
    [InlineData(0, 0f, 0f, 0f)]
    [InlineData(0, float.NaN, 0f, 0f)]
    [InlineData(0, float.PositiveInfinity, 0f, 0f)]
    public void Append_InvalidCheckpointOrVector_DoesNotAdvanceCheckpoint(int ordinal, float x, float y, float z)
    {
        Directory.CreateDirectory(directory);
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        index.Begin(identity, 1);
        Assert.Throws<ArgumentException>(() => index.Append(identity, new(ordinal, "excerpt", null, [x, y, z])));
        Assert.Throws<ArgumentException>(() => index.Append(identity, new(0, "excerpt", null, [1, 0])));
        Assert.Equal(new IndexProgress(0, false), index.Begin(identity, 1));
        index.Append(identity, new(0, "excerpt", null, [1, 0, 0]));
        Assert.Throws<ArgumentException>(() => index.Append(identity, new(1, "extra", null, [1, 0, 0])));
    }

    [Fact]
    public void Search_TiedVectorsAndInvalidQuery_ReturnsBoundedStableOrderAndHonorsCancellation()
    {
        Directory.CreateDirectory(directory);
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        index.Begin(identity, 3);
        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            index.Append(identity, new(ordinal, $"excerpt-{ordinal}", ordinal, [1, 0, 0]));
        }
        index.Complete(identity);
        Assert.Equal([0, 1], index.Search(identity, [1, 0, 0], 2).Select(match => match.Ordinal));
        Assert.Throws<ArgumentException>(() => index.Search(identity, [0, 0, 0], 1));
        Assert.Throws<ArgumentException>(() => index.Search(identity, [1, 0], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search(identity, [1, 0, 0], 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => index.Search(identity, [1, 0, 0], 1, cancellation.Token));
    }

    [Fact]
    public void Begin_InvalidMetadataAndExcerpt_RejectsBeforePersisting()
    {
        Directory.CreateDirectory(directory);
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Begin(identity, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Begin(identity with { Dimensions = 0 }, 1));
        foreach (var invalid in new[]
        {
            identity with { Attempt = "" }, identity with { TranscriptHash = "" }, identity with { ModelHash = "" },
            identity with { Pooling = "" }, identity with { Normalization = "" }, identity with { Chunking = "" }
        })
        {
            Assert.Throws<ArgumentException>(() => index.Begin(invalid, 1));
        }
        index.Begin(identity, 2);
        Assert.Throws<ArgumentException>(() => index.Append(identity, new(0, "", null, [1, 0, 0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Append(identity, new(0, "excerpt-a", -1, [1, 0, 0])));
        index.Append(identity, new(0, "excerpt-a", 0, [1, 0, 0]));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => index.Append(identity, new(1, "excerpt-a", null, [1, 0, 0])));
        Assert.Equal(new IndexProgress(1, false), index.Begin(identity, 2));
        index.Append(identity, new(1, "excerpt-b", null, [1, 0, 0]));
        index.Complete(identity);
    }

    [Fact]
    public void Search_AnyCompatibilityFieldChanged_RejectsStaleReadWriteAndResume()
    {
        Directory.CreateDirectory(directory);
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        index.Begin(identity, 1);
        foreach (var changed in new[]
        {
            identity with { Attempt = "retry-attempt" }, identity with { TranscriptHash = "changed-source" },
            identity with { ModelHash = "changed-model" }, identity with { Dimensions = 2 },
            identity with { Pooling = "cls" }, identity with { Normalization = "l2" },
            identity with { Chunking = "chunks-v2" }
        })
        {
            Assert.Throws<InvalidOperationException>(() => index.Append(changed, new(0, "excerpt", null, [1, 0, 0])));
            Assert.Throws<InvalidOperationException>(() => index.Complete(changed));
            var query = new float[changed.Dimensions];
            query[0] = 1;
            Assert.Throws<InvalidOperationException>(() => index.Search(changed, query, 1));
            if (changed.Attempt == identity.Attempt)
            {
                Assert.Throws<InvalidOperationException>(() => index.Begin(changed, 1));
            }
        }
        Assert.Equal(new IndexProgress(0, false), index.Begin(identity, 1));
    }

    [Fact]
    public void Search_RelevanceOpposesInsertionOrderAndManyTies_SortsBeforeBounding()
    {
        Directory.CreateDirectory(directory);
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        index.Begin(identity, 34);
        for (var ordinal = 0; ordinal < 34; ordinal++)
        {
            float[] vector = ordinal < 2 ? [-1, 0, 0] : [1, 0, 0];
            index.Append(identity, new(ordinal, $"excerpt-{ordinal}", null, vector));
        }
        index.Complete(identity);
        Assert.Equal(Enumerable.Range(2, 32).Concat([0, 1]), index.Search(identity, [1, 0, 0], 34).Select(match => match.Ordinal));
        Assert.Equal([2, 3, 4], index.Search(identity, [1, 0, 0], 3).Select(match => match.Ordinal));
    }

    [Fact]
    public void Begin_IncompatibleOrPartialWork_ExplainsFailureWithoutHidingIt()
    {
        Directory.CreateDirectory(directory);
        using var index = new TranscriptIndex(Path.Combine(directory, "cache.db"));
        Assert.Equal("No cache for the selected attempt.", Assert.Throws<InvalidOperationException>(() => index.Complete(identity)).Message);
        index.Begin(identity, 1);
        Assert.Equal("Incompatible excerpt count.", Assert.Throws<InvalidOperationException>(() => index.Begin(identity, 2)).Message);
        Assert.Equal("Incompatible transcript or embedding configuration.",
            Assert.Throws<InvalidOperationException>(() => index.Begin(identity with { ModelHash = "changed" }, 1)).Message);
        Assert.Equal("Partial work is not a complete index.", Assert.Throws<InvalidOperationException>(() => index.Complete(identity)).Message);
        Assert.Equal("Partial work is not queryable.", Assert.Throws<InvalidOperationException>(() => index.Search(identity, [1, 0, 0], 1)).Message);
        Assert.Equal("Excerpts must form a contiguous checkpoint prefix.",
            Assert.Throws<ArgumentException>(() => index.Append(identity, new(1, "excerpt", null, [1, 0, 0]))).Message);
        Assert.Equal("Embedding dimension mismatch.",
            Assert.Throws<ArgumentException>(() => index.Append(identity, new(0, "excerpt", null, [1, 0]))).Message);
        Assert.Equal("Non-finite embedding.",
            Assert.Throws<ArgumentException>(() => index.Append(identity, new(0, "excerpt", null, [float.NaN, 0, 0]))).Message);
        Assert.Equal("Cosine is undefined for zero embeddings.",
            Assert.Throws<ArgumentException>(() => index.Append(identity, new(0, "excerpt", null, [0, 0, 0]))).Message);
        index.Append(identity, new(0, "excerpt", null, [0, float.Epsilon, 0]));
        index.Complete(identity);
        Assert.Equal(1, index.Search(identity, [0, float.MaxValue, 0], 1)[0].Similarity, 8);
        Assert.Equal("Completed caches are immutable.",
            Assert.Throws<InvalidOperationException>(() => index.Append(identity, new(0, "excerpt", null, [1, 0, 0]))).Message);
    }

    [Theory]
    [Trait("Category", "Measurement")]
    [InlineData(1000)]
    [InlineData(10000)]
    public void Search_Synthetic384DimensionCorpus_ReturnsGoldenTopFiveWithLocalMetrics(int excerpts)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cache.db");
        var target = identity with { Dimensions = 384 };
        var query = new float[384];
        query[0] = 1;
        var indexing = Stopwatch.StartNew();
        using (var index = new TranscriptIndex(path))
        {
            index.Begin(target, excerpts);
            for (var ordinal = 0; ordinal < excerpts; ordinal++)
            {
                var vector = new float[384];
                vector[0] = 1;
                vector[1] = ordinal;
                index.Append(target, new(ordinal, $"synthetic-{ordinal}", ordinal * 1000L, vector));
            }
            index.Complete(target);
        }
        indexing.Stop();
        using var reopened = new TranscriptIndex(path);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var retrieval = Stopwatch.StartNew();
        var matches = reopened.Search(target, query, 5);
        retrieval.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.Equal([0, 1, 2, 3, 4], matches.Select(match => match.Ordinal));
        Assert.Equal(0.24253562503633297, matches[4].Similarity, 8);
        output.WriteLine($"RID={RuntimeInformation.RuntimeIdentifier}; Process={RuntimeInformation.ProcessArchitecture}; " +
            $"Rows={excerpts}; Dimensions=384; K=5; IndexMs={indexing.Elapsed.TotalMilliseconds:F3}; " +
            $"SearchMs={retrieval.Elapsed.TotalMilliseconds:F3}; SearchAllocatedBytes={allocated}; " +
            $"DatabaseBytes={new FileInfo(path).Length}; ProcessPeakWorkingSetBytes={Process.GetCurrentProcess().PeakWorkingSet64}");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
