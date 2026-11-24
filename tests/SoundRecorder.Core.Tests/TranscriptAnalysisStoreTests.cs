using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundRecorder.Infrastructure.Analysis.Storage;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class TranscriptAnalysisStoreTests
{
    [Fact]
    public void Create_NetworkStyleNamespaceAlias_RejectsBeforeWritingPrivateData()
    {
        using var files = new StoreFiles();
        var alias = @"\\?\" + files.Database;
        var error = Assert.Throws<InvalidOperationException>(() => SqliteTranscriptAnalysisStore.Create(alias));
        Assert.Contains("local", error.Message);
        Assert.False(File.Exists(files.Database));
        Assert.Throws<InvalidOperationException>(() => SqliteTranscriptAnalysisStore.Create(@"\\not-contacted.invalid\share\analysis.sqlite"));
        Assert.Throws<InvalidOperationException>(() => SqliteTranscriptAnalysisStore.Open(@"\\not-contacted.invalid\share\analysis.sqlite"));
    }

    [Fact]
    public async Task ResumeAsync_CorruptRetainedCheckpoint_RejectsInsteadOfHidingIncompatibleCursor()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "RETAINED-CURSOR-TOKEN"), Stream(Chunk()), default);
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, default);
        var bytes = await File.ReadAllBytesAsync(files.Database);
        var offset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("RETAINED-CURSOR-TOKEN"));
        if (offset < 0) throw new InvalidOperationException("Controlled corruption token was not persisted.");
        bytes[offset] = (byte)'Z';
        await File.WriteAllBytesAsync(files.Database, bytes);

        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        await Failure<InvalidDataException>(() => store.ResumeAsync(revision, default));
        await Failure<InvalidDataException>(() => store.LoadAsync(revision.Identity, default));
    }

    [Theory]
    [InlineData("attempt", typeof(ArgumentException))]
    [InlineData("short-hash", typeof(ArgumentException))]
    [InlineData("nonhex-hash", typeof(ArgumentException))]
    [InlineData("dimensions-zero", typeof(ArgumentException))]
    [InlineData("dimensions-too-large", typeof(ArgumentException))]
    [InlineData("language", typeof(InvalidDataException))]
    [InlineData("algorithm", typeof(InvalidDataException))]
    [InlineData("model-id", typeof(InvalidDataException))]
    [InlineData("configuration", typeof(InvalidDataException))]
    public async Task BeginAsync_InvalidPinnedIdentity_RejectsBeforeCreatingOwnedWork(string invalid, Type errorType)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var bad = invalid switch
        {
            "attempt" => identity with { AttemptId = Guid.Empty },
            "short-hash" => identity with { TextSha256 = new('A', 63) },
            "nonhex-hash" => identity with { SubtitleSha256 = new string('A', 63) + "G" },
            "dimensions-zero" => identity with { VectorDimensions = 0 },
            "dimensions-too-large" => identity with { VectorDimensions = 4097 },
            "language" => identity with { Language = " " },
            "algorithm" => identity with { AlgorithmVersion = "" },
            "model-id" => identity with { Embedding = identity.Embedding with { Id = "" } },
            _ => identity with { Generation = Model("generation", new('C', 64), "") }
        };
        var error = await Assert.ThrowsAsync(errorType, () => store.BeginAsync(bad, default));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        await Failure<KeyNotFoundException>(() => store.LoadAsync(identity, default));
        Assert.Equal(identity, (await store.BeginAsync(identity, default)).Identity);
    }

    [Fact]
    public async Task SaveCheckpointAsync_TransportBoundariesAndUntimedPortuguese_RoundTripsWithoutTruncation()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity() with { Language = "Brazilian Portuguese", AlgorithmVersion = new('v', 128),
            Generation = Model(new('g', 256), new('C', 64), new('c', 4096)) };
        var revision = await store.BeginAsync(identity, default);
        var prefix = "João: \"próxima etapa\" 👋\n";
        var text = prefix + new string('x', 4096 - prefix.Length);
        var chunk = new AnalysisStoreChunk(0, new('s', 256), 3, null, null, text, [0, 1]);
        var cursor = new AnalysisStoreCheckpoint(1, new('i', 64), new('c', 1024));
        await store.SaveCheckpointAsync(revision, cursor, Stream(chunk), default);
        var quoted = new TranscriptAnalysisEvidence(new('r', 256), 0, text);
        var claims = Enumerable.Range(0, 16).Select(_ =>
            new TranscriptAnalysisClaim(new('t', 4096), Enumerable.Repeat(quoted, 16).ToArray())).ToArray();
        var output = new AnalysisStoreOutput(0, "Discussion", new('q', 512), new('a', 16384), claims);
        await store.CompleteAsync(revision, Stream(output), default);
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        Assert.Equal(cursor, (await store.LoadAsync(identity, default)).Checkpoint);
        var restored = Assert.Single(await Collect(store.ReadChunksAsync(revision, default)));
        Assert.Equal(text, restored.Text);
        Assert.Equal(3, restored.Offset);
        Assert.Null(restored.StartMs);
        Assert.Null(restored.EndMs);
        Assert.Equal(new float[] { 0, 1 }, restored.Vector);
        var answer = Assert.Single(await Collect(store.ReadOutputsAsync(revision, default)));
        Assert.Equal(output.Content, answer.Content);
        Assert.Equal(output.Question, answer.Question);
        Assert.Equal(16, answer.Claims.Count);
        Assert.All(answer.Claims, claim => Assert.Equal(claims[0].Evidence, claim.Evidence));
        foreach (var dimensions in new[] { 1, 4096 })
        {
            var separate = Identity() with { VectorDimensions = dimensions };
            var separateRevision = await store.BeginAsync(separate, default);
            await store.SaveCheckpointAsync(separateRevision, new(0, "Starting", ""), Empty<AnalysisStoreChunk>(), default);
            Assert.Equal(0, (await store.LoadAsync(separate, default)).Checkpoint!.NextOrdinal);
        }
    }

    [Theory]
    [InlineData("source-reference")]
    [InlineData("offset")]
    [InlineData("negative-time")]
    [InlineData("missing-start")]
    [InlineData("missing-end")]
    [InlineData("equal-time")]
    public async Task SaveCheckpointAsync_InvalidOriginalRange_RejectsUnusableResumableWork(string invalid)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        var bad = invalid switch
        {
            "source-reference" => Chunk() with { SourceId = "" },
            "offset" => Chunk() with { Offset = -1 },
            "negative-time" => Chunk() with { StartMs = -1 },
            "missing-start" => Chunk() with { StartMs = null },
            "equal-time" => Chunk() with { EndMs = 1200 },
            _ => Chunk() with { EndMs = null }
        };
        await Failure<InvalidDataException>(() => store.SaveCheckpointAsync(revision, new(1, "Index", "invalid"),
            Stream(bad), default));
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
        await store.SaveCheckpointAsync(revision, new(1, "Index", "valid"),
            Stream(Chunk() with { StartMs = 0, EndMs = 1 }), default);
        Assert.Equal(0, Assert.Single(await Collect(store.ReadChunksAsync(revision, default))).StartMs);
    }

    [Theory]
    [InlineData("claim-limit")]
    [InlineData("claim-text")]
    [InlineData("no-citation")]
    [InlineData("citation-limit")]
    [InlineData("citation-reference")]
    [InlineData("citation-text")]
    [InlineData("citation-time")]
    public async Task CompleteAsync_InvalidClaimPortion_RejectsWithActionableError(string invalid)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        var claim = Output().Claims[0];
        var citation = claim.Evidence[0];
        var bad = invalid switch
        {
            "claim-limit" => Output() with { Claims = Enumerable.Repeat(claim, 17).ToArray() },
            "claim-text" => Output() with { Claims = [claim with { Text = "" }] },
            "no-citation" => Output() with { Claims = [claim with { Evidence = [] }] },
            "citation-limit" => Output() with { Claims = [claim with { Evidence = Enumerable.Repeat(citation, 17).ToArray() }] },
            "citation-reference" => Output() with { Claims = [claim with { Evidence = [citation with { Reference = "" }] }] },
            "citation-text" => Output() with { Claims = [claim with { Evidence = [citation with { Text = "" }] }] },
            _ => Output() with { Claims = [claim with { Evidence = [citation with { StartMs = -1 }] }] }
        };
        await Failure<InvalidDataException>(() => store.CompleteAsync(revision, Stream(bad), default));
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        await store.CompleteAsync(revision, Stream(Output() with { Claims = [], Content = "Insufficient evidence" }), default);
        Assert.Empty(Assert.Single(await Collect(store.ReadOutputsAsync(revision, default))).Claims);
    }

    [Fact]
    public async Task WriteAsync_EmptyOutputWrongCursorOrInvalidState_RejectsWithoutClaimingCompletion()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        await Failure<InvalidDataException>(() => store.CompleteAsync(revision, Empty<AnalysisStoreOutput>(), default));
        await Failure<InvalidDataException>(() => store.SaveCheckpointAsync(revision,
            new(2, "Index", "wrong"), Stream(Chunk()), default));
        await Failure<InvalidDataException>(() => store.SaveCheckpointAsync(revision,
            new(-1, "Index", "negative"), Empty<AnalysisStoreChunk>(), default));
        await Failure<InvalidDataException>(() => store.SaveCheckpointAsync(revision,
            new(0, "", ""), Empty<AnalysisStoreChunk>(), default));
        var stateError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Complete, default));
        Assert.Contains("unfinished", stateError.Message);
        var deleteError = await Failure<ArgumentException>(() => store.DeleteOwnedAsync(Guid.Empty, default));
        Assert.Contains("attempt", deleteError.Message);
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
    }

    [Fact]
    public async Task PublicOperations_PreCancelledAndMissingStore_RejectBeforeAccessOrEnumeration()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        File.Delete(files.Database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginAsync(revision.Identity, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(revision.Identity, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveCheckpointAsync(revision,
            new(1, "Index", "cursor"), Stream(Chunk()), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteAsync(revision, Stream(Output()), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ResumeAsync(revision, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteOwnedAsync(revision.Identity.AttemptId, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(store.ReadChunksAsync(revision, token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(store.ReadOutputsAsync(revision, token)));
        Assert.False(File.Exists(files.Database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CancelledAtEnumerationBoundary_DoesNotValidateNextPortionOrCommit(bool atEnd)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        using var chunksCancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveCheckpointAsync(revision,
            new(1, "Index", "cancelled"),
            CancelledStream(Chunk(), Chunk(99), chunksCancelled, atEnd), chunksCancelled.Token));
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
        using var outputsCancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteAsync(revision,
            CancelledStream(Output(), Output(99), outputsCancelled, atEnd), outputsCancelled.Token));
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Completed);
        await store.CompleteAsync(revision, Pair(Output(), Output(1)), default);
        using var readingCancelled = new CancellationTokenSource();
        await using var reader = store.ReadOutputsAsync(revision, readingCancelled.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        readingCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task CompleteAsync_ConcurrentReplacementTransaction_KeepsOldReadersAndRejectsSecondWriter()
    {
        using var files = new StoreFiles();
        var logger = new StorageTestLogger<SqliteTranscriptAnalysisStore>();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database, logger);
        var identity = Identity();
        var old = await store.BeginAsync(identity, default);
        await store.CompleteAsync(old, Stream(Output()), default);
        var replacement = await store.BeginAsync(identity, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = store.CompleteAsync(replacement, HeldOutputs(entered, release), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var other = SqliteTranscriptAnalysisStore.Open(files.Database, logger);
            Assert.Equal(old, (await other.LoadAsync(identity, default)).Completed);
            Assert.Equal("Draft handoff", Assert.Single(await Collect(other.ReadOutputsAsync(old, default))).Content);
            var error = await Assert.ThrowsAsync<IOException>(() => other.BeginAsync(identity, default));
            Assert.Contains("storage", error.Message);
        }
        finally { release.TrySetResult(); }
        await pending;
        Assert.Equal(replacement, (await store.LoadAsync(identity, default)).Completed);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("storage failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BeginAsync_ChangedConfigurationWithoutMatchingFingerprint_RejectsBeforeOwningAttempt()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var mismatched = identity with
        {
            Generation = identity.Generation with { CanonicalConfiguration = "context=256;seed=0" }
        };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.BeginAsync(mismatched, default));
        Assert.Contains("configuration", error.Message, StringComparison.OrdinalIgnoreCase);
        var revision = await store.BeginAsync(identity, default);
        var reopened = await SqliteTranscriptAnalysisStore.Open(files.Database).LoadAsync(identity, default);
        Assert.Equal("context=512;seed=0", reopened.Unfinished!.Identity.Generation.CanonicalConfiguration);
        Assert.Equal(revision, reopened.Unfinished);
    }

    [Theory]
    [InlineData("vector")]
    [InlineData("checkpoint")]
    [InlineData("output")]
    [InlineData("version")]
    [InlineData("identity")]
    public async Task LoadAsync_CorruptPayloadOrUnsupportedVersion_RejectsInsteadOfReturningCompatibleSuccess(string corrupt)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var revision = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "CURSOR-CORRUPTION-TOKEN"), Stream(Chunk()), default);
        await store.CompleteAsync(revision, Stream(Output(content: "OUTPUT-CORRUPTION-TOKEN")), default);
        var bytes = await File.ReadAllBytesAsync(files.Database);
        if (corrupt == "version") bytes[63] = 2;
        else
        {
            var token = corrupt switch
            {
                "vector" => "0.25",
                "checkpoint" => "CURSOR-CORRUPTION-TOKEN",
                "identity" => "context=512;seed=0",
                _ => "OUTPUT-CORRUPTION-TOKEN"
            };
            var offset = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(token));
            if (offset < 0) throw new InvalidOperationException("Controlled corruption token was not persisted.");
            bytes[offset] = corrupt == "vector" ? (byte)'1' : (byte)'Z';
        }
        await File.WriteAllBytesAsync(files.Database, bytes);
        if (corrupt == "version")
        {
            var error = Assert.Throws<InvalidDataException>(() => SqliteTranscriptAnalysisStore.Open(files.Database));
            Assert.Contains("version", error.Message);
            return;
        }
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        if (corrupt is "checkpoint" or "identity")
            await Failure<InvalidDataException>(() => store.LoadAsync(identity, default));
        else if (corrupt == "vector")
            await Failure<InvalidDataException>(() => Collect(store.ReadChunksAsync(revision, default)));
        else
            await Failure<InvalidDataException>(() => Collect(store.ReadOutputsAsync(revision, default)));
    }

    [Fact]
    public async Task SaveCheckpointAsync_InvalidCursorOrCancelledEnumeration_DoesNotRetainPartialChunks()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var revision = await store.BeginAsync(Identity(), default);
        await Failure<InvalidDataException>(() => store.SaveCheckpointAsync(revision,
            new(1, "Index", new('x', 1025)), Stream(Chunk()), default));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveCheckpointAsync(revision,
            new(2, "Index", "cancelled"), CancelledChunks(cancellation), cancellation.Token));
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
        Assert.Null((await store.LoadAsync(revision.Identity, default)).Checkpoint);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "valid"), Stream(Chunk()), default);
        Assert.Equal(1, (await store.LoadAsync(revision.Identity, default)).Checkpoint!.NextOrdinal);
    }

    [Fact]
    public async Task CompleteAsync_ProcessCrashBeforeCommit_ReopensOldCompleteAndRetainsCompatibleCheckpoint()
    {
        const string childVariable = "SR_ANALYSIS_STORAGE_CRASH";
        var childDirectory = Environment.GetEnvironmentVariable(childVariable);
        if (childDirectory is not null)
        {
            var revision = JsonSerializer.Deserialize<AnalysisStoreRevision>(
                await File.ReadAllTextAsync(Path.Combine(childDirectory, "revision.json")))!;
            var childStore = SqliteTranscriptAnalysisStore.Open(Path.Combine(childDirectory, "analysis.sqlite"));
            await childStore.CompleteAsync(revision, CrashOutputs(childDirectory), default);
            return;
        }

        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var old = await store.BeginAsync(identity, default);
        await store.CompleteAsync(old, Stream(Output()), default);
        var replacement = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(replacement, new(1, "Index", "durable-before-crash"), Stream(Chunk()), default);
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "revision.json"), JsonSerializer.Serialize(replacement));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(TranscriptAnalysisStoreTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=SoundRecorder.Core.Tests.TranscriptAnalysisStoreTests.CompleteAsync_ProcessCrashBeforeCommit_ReopensOldCompleteAndRetainsCompatibleCheckpoint");
        start.ArgumentList.Add("/ResultsDirectory:" + files.Directory);
        start.Environment[childVariable] = files.Directory;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!File.Exists(Path.Combine(files.Directory, "transaction-ready")))
            {
                if (process.HasExited) throw new InvalidOperationException(await output + await error);
                await Task.Delay(50, deadline.Token);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        await output;
        await error;
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        var snapshot = await store.LoadAsync(identity, default);
        Assert.Equal(old, snapshot.Completed);
        Assert.Equal(replacement, snapshot.Unfinished);
        Assert.Equal(AnalysisStoreStatus.Processing, snapshot.Status);
        Assert.Equal("durable-before-crash", snapshot.Checkpoint!.Cursor);
        Assert.Single(await Collect(store.ReadChunksAsync(replacement, default)));
        Assert.Equal("Draft handoff", Assert.Single(await Collect(store.ReadOutputsAsync(old, default))).Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(store.ReadOutputsAsync(replacement, default)));
        await store.SetUnfinishedAsync(replacement, AnalysisStoreStatus.Interrupted, default);
        await store.ResumeAsync(replacement, default);
        await store.CompleteAsync(replacement, Stream(Output(content: "Recovered replacement")), default);
        Assert.Equal("Recovered replacement", Assert.Single(await Collect(store.ReadOutputsAsync(replacement, default))).Content);
    }

    [Fact]
    public async Task Open_MissingCorruptOrWriteBlockedStore_ReportsFailureWithoutCreatingOrDestroyingActivation()
    {
        using var files = new StoreFiles();
        var missing = Assert.Throws<FileNotFoundException>(() => SqliteTranscriptAnalysisStore.Open(files.Database));
        Assert.Contains("missing", missing.Message);
        Assert.False(File.Exists(files.Database));
        var logger = new StorageTestLogger<SqliteTranscriptAnalysisStore>();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database, logger);
        Assert.Throws<IOException>(() => SqliteTranscriptAnalysisStore.Create(files.Database));
        var identity = Identity();
        var first = await store.BeginAsync(identity, default);
        await store.CompleteAsync(first, Stream(Output()), default);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("activated", StringComparison.Ordinal));
        using (new FileStream(files.Database, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => store.BeginAsync(identity, default));
        store = SqliteTranscriptAnalysisStore.Open(files.Database, logger);
        Assert.Equal(first, (await store.LoadAsync(identity, default)).Completed);
        Assert.Equal("Draft handoff", Assert.Single(await Collect(store.ReadOutputsAsync(first, default))).Content);
        await File.WriteAllTextAsync(files.Database, "not a SQLite database");
        Assert.Throws<IOException>(() => SqliteTranscriptAnalysisStore.Open(files.Database, logger));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("storage failed", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(files.Directory, entry.Message);
            Assert.DoesNotContain("Ana", entry.Message);
            Assert.DoesNotContain("Draft handoff", entry.Message);
        });
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("content")]
    [InlineData("citation")]
    [InlineData("kind")]
    [InlineData("question")]
    public async Task CompleteAsync_InvalidOutput_RejectsAtomicActivationWithoutDiscardingContent(string invalid)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var first = await store.BeginAsync(identity, default);
        await store.CompleteAsync(first, Stream(Output()), default);
        var replacement = await store.BeginAsync(identity, default);
        var bad = invalid switch
        {
            "ordinal" => Output(3),
            "content" => Output(1) with { Content = new('x', 16385) },
            "citation" => Output(1) with { Claims = [new("Claim", [new("", -1, "")])] },
            "kind" => Output(1) with { Kind = "Complete" },
            _ => Output(1) with { Kind = "Discussion", Question = new('q', 513) }
        };
        await Failure<InvalidDataException>(() =>
            store.CompleteAsync(replacement, Pair(Output(content: "Not published"), bad), default));
        Assert.Equal(first, (await store.LoadAsync(identity, default)).Completed);
        await store.CompleteAsync(replacement, Pair(Output(),
            Output(1, "Unverified answer") with { Kind = "Discussion", Question = "Who sends the draft?" }), default);
        var outputs = await Collect(store.ReadOutputsAsync(replacement, default));
        Assert.Equal(2, outputs.Count);
        Assert.Equal("Who sends the draft?", outputs[1].Question);
        Assert.Equal("Unverified answer", outputs[1].Content);
        Assert.Equal("source@0", Assert.Single(Assert.Single(outputs[1].Claims).Evidence).Reference);
    }

    [Fact]
    public async Task DeleteOwnedAsync_ExactAttempt_RemovesAllOwnedVersionsWithoutTouchingSourceModelsOrOtherAttempts()
    {
        using var files = new StoreFiles();
        var logger = new StorageTestLogger<SqliteTranscriptAnalysisStore>();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database, logger);
        var identity = Identity();
        var first = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(first, new(1, "Index", "first"), Stream(Chunk()), default);
        await store.CompleteAsync(first, Stream(Output()), default);
        var replacement = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(replacement, new(1, "Index", "replacement"), Stream(Chunk()), default);
        await store.SetUnfinishedAsync(replacement, AnalysisStoreStatus.Cancelled, default);
        var otherIdentity = Identity();
        var other = await store.BeginAsync(otherIdentity, default);
        await store.SaveCheckpointAsync(other, new(1, "Index", "other"), Stream(Chunk()), default);
        await store.CompleteAsync(other, Stream(Output(content: "Other attempt")), default);
        var source = Path.Combine(files.Directory, "source.txt");
        var audio = Path.Combine(files.Directory, "recording.wav");
        var model = Path.Combine(files.Directory, "model.gguf");
        foreach (var file in new[] { source, audio, model }) await File.WriteAllTextAsync(file, "untouched sentinel");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteOwnedAsync(identity.AttemptId, cancelled.Token));
        Assert.Equal(first, (await store.LoadAsync(identity, default)).Completed);

        await store.DeleteOwnedAsync(identity.AttemptId, default);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("deleted", StringComparison.Ordinal));
        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        await Failure<KeyNotFoundException>(() => store.LoadAsync(identity, default));
        await Failure<KeyNotFoundException>(() => Collect(store.ReadChunksAsync(first, default)));
        await Failure<KeyNotFoundException>(() => Collect(store.ReadOutputsAsync(first, default)));
        await Failure<KeyNotFoundException>(() => Collect(store.ReadChunksAsync(replacement, default)));
        await Failure<KeyNotFoundException>(() => store.ResumeAsync(replacement, default));
        Assert.Equal(other, (await store.LoadAsync(otherIdentity, default)).Completed);
        Assert.Equal("Other attempt", Assert.Single(await Collect(store.ReadOutputsAsync(other, default))).Content);
        foreach (var file in new[] { source, audio, model }) Assert.Equal("untouched sentinel", await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData("dimensions")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("zero")]
    [InlineData("range")]
    [InlineData("text")]
    public async Task SaveCheckpointAsync_InvalidBoundedChunk_RejectsWithoutAdvancingCursor(string invalid)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var revision = await store.BeginAsync(identity, default);
        var bad = invalid switch
        {
            "dimensions" => Chunk(1) with { Vector = [0.25f] },
            "nan" => Chunk(1) with { Vector = [float.NaN, 1] },
            "infinity" => Chunk(1) with { Vector = [float.PositiveInfinity, 1] },
            "zero" => Chunk(1) with { Vector = [0, 0] },
            "range" => Chunk(1) with { EndMs = 1199 },
            _ => Chunk(1) with { Text = new('x', 4097) }
        };
        await Failure<InvalidDataException>(() =>
            store.SaveCheckpointAsync(revision, new(2, "Index", "bad"), Pair(Chunk(), bad), default));
        Assert.Empty(await Collect(store.ReadChunksAsync(revision, default)));
        Assert.Null((await store.LoadAsync(identity, default)).Checkpoint);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "source-only"),
            Stream(Chunk() with { Vector = [] }), default);
        Assert.Empty(Assert.Single(await Collect(store.ReadChunksAsync(revision, default))).Vector);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("subtitle")]
    [InlineData("generation")]
    [InlineData("generation-config")]
    [InlineData("embedding")]
    [InlineData("embedding-config")]
    [InlineData("algorithm")]
    [InlineData("dimensions")]
    public async Task LoadAsync_StaleSourceOrProcessingIdentity_RejectsWithoutRetargeting(string change)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var revision = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "cursor"), Stream(Chunk()), default);
        await store.CompleteAsync(revision, Stream(Output()), default);
        var different = change switch
        {
            "text" => identity with { TextSha256 = new('0', 64) },
            "subtitle" => identity with { SubtitleSha256 = new('0', 64) },
            "generation" => identity with { Generation = identity.Generation with { Sha256 = new('0', 64) } },
            "generation-config" => identity with { Generation = Model("generation", new('C', 64), "context=256;seed=0") },
            "embedding" => identity with { Embedding = identity.Embedding with { Sha256 = new('0', 64) } },
            "embedding-config" => identity with { Embedding = Model("embedding", new('E', 64), "pooling=last;prefix=v1") },
            "algorithm" => identity with { AlgorithmVersion = "analysis-v2" },
            _ => identity with { VectorDimensions = 3 }
        };
        await Failure<InvalidDataException>(() => store.LoadAsync(different, default));
        await Failure<InvalidDataException>(() =>
            Collect(store.ReadChunksAsync(revision with { Identity = different }, default)));
        Assert.Equal(revision, (await store.LoadAsync(identity, default)).Completed);
        if (change is "text" or "subtitle")
            await Failure<InvalidDataException>(() => store.BeginAsync(different, default));
    }

    [Fact]
    public async Task BeginAsync_CanonicalHashesAndConcurrentWriters_PinsOneCurrentRevision()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var lowercase = identity with
        {
            TextSha256 = identity.TextSha256.ToLowerInvariant(),
            SubtitleSha256 = identity.SubtitleSha256.ToLowerInvariant(),
            Generation = identity.Generation with { Sha256 = identity.Generation.Sha256.ToLowerInvariant(),
                ConfigurationSha256 = identity.Generation.ConfigurationSha256.ToLowerInvariant() },
            Embedding = identity.Embedding with { Sha256 = identity.Embedding.Sha256.ToLowerInvariant(),
                ConfigurationSha256 = identity.Embedding.ConfigurationSha256.ToLowerInvariant() }
        };
        var revision = await store.BeginAsync(lowercase, default);
        Assert.Equal(identity, revision.Identity);
        await Failure<InvalidOperationException>(() => store.BeginAsync(identity, default));
        var concurrent = SqliteTranscriptAnalysisStore.Open(files.Database);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "one"), Stream(Chunk()), default);
        await Failure<InvalidDataException>(() =>
            concurrent.SaveCheckpointAsync(revision, new(1, "Index", "duplicate"), Stream(Chunk()), default));
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, default);
        var replacement = await concurrent.BeginAsync(identity, default);
        await Failure<InvalidOperationException>(() => store.ResumeAsync(revision, default));
        await Failure<InvalidOperationException>(() =>
            store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Interrupted, default));
        await concurrent.CompleteAsync(replacement, Stream(Output()), default);
        Assert.Equal(replacement, (await store.LoadAsync(lowercase, default)).Completed);
    }

    [Theory]
    [InlineData(AnalysisStoreStatus.Cancelled)]
    [InlineData(AnalysisStoreStatus.Interrupted)]
    [InlineData(AnalysisStoreStatus.Failed)]
    public async Task SaveCheckpointAsync_ReopenedUnfinishedRevision_ResumesOnlyCommittedContiguousChunks(
        AnalysisStoreStatus status)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var revision = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "resume@18"), Stream(Chunk()), default);
        await store.SetUnfinishedAsync(revision, status, default);

        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        var snapshot = await store.LoadAsync(identity, default);
        Assert.Equal(status, snapshot.Status);
        Assert.Null(snapshot.Completed);
        Assert.Equal(new(1, "Index", "resume@18"), snapshot.Checkpoint);
        await Failure<InvalidOperationException>(() =>
            store.SaveCheckpointAsync(revision, new(2, "Index", "resume@36"), Stream(Chunk(1)), default));
        await Failure<InvalidOperationException>(() => store.CompleteAsync(revision, Stream(Output()), default));
        await store.ResumeAsync(snapshot.Unfinished!, default);
        await Failure<InvalidDataException>(() =>
            store.SaveCheckpointAsync(revision, new(3, "Index", "wrong"), Stream(Chunk(2)), default));
        Assert.Single(await Collect(store.ReadChunksAsync(revision, default)));
        await store.SaveCheckpointAsync(revision, new(2, "Index", "resume@36"), Stream(Chunk(1)), default);
        await Failure<InvalidDataException>(() =>
            store.SaveCheckpointAsync(revision, new(3, "Index", "duplicate"), Stream(Chunk(1)), default));
        Assert.Equal(2, (await store.LoadAsync(identity, default)).Checkpoint!.NextOrdinal);
        await store.CompleteAsync(revision, Stream(Output()), default);
        await Failure<InvalidOperationException>(() => store.ResumeAsync(revision, default));
        await Failure<InvalidOperationException>(() =>
            store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Interrupted, default));
        Assert.Equal(2, (await Collect(store.ReadChunksAsync(revision, default))).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAsync_InterruptedReplacement_PreservesOldActivationAndRejectsPartialOutput(bool cancel)
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var old = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(old, new(1, "Index", "old-cursor"), Stream(Chunk()), default);
        await store.CompleteAsync(old, Stream(Output()), default);
        var replacement = await store.BeginAsync(identity, default);
        using var cancellation = new CancellationTokenSource();

        var failure = store.CompleteAsync(replacement, BrokenOutputs(cancel, cancellation), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failure);
        else await Assert.ThrowsAsync<IOException>(() => failure);

        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        var snapshot = await store.LoadAsync(identity, default);
        Assert.Equal(AnalysisStoreStatus.Processing, snapshot.Status);
        Assert.Equal(old, snapshot.Completed);
        Assert.Equal(replacement, snapshot.Unfinished);
        Assert.Null(snapshot.Checkpoint);
        Assert.Equal("Draft handoff", Assert.Single(await Collect(store.ReadOutputsAsync(old, default))).Content);
        await Failure<InvalidOperationException>(() => Collect(store.ReadOutputsAsync(replacement, default)));
        await store.CompleteAsync(replacement, Stream(Output(content: "New handoff")), default);
        snapshot = await store.LoadAsync(identity, default);
        Assert.Equal(replacement, snapshot.Completed);
        Assert.Null(snapshot.Unfinished);
        Assert.Equal("New handoff", Assert.Single(await Collect(store.ReadOutputsAsync(replacement, default))).Content);
    }

    [Fact]
    public async Task CompleteAsync_ReopenedStore_RestoresTypedContentCitationsAndIndex()
    {
        using var files = new StoreFiles();
        ITranscriptAnalysisStore store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var identity = Identity();
        var revision = await store.BeginAsync(identity, default);
        await store.SaveCheckpointAsync(revision, new(1, "Index", "source:18"), Stream(Chunk()), default);
        await store.CompleteAsync(revision, Stream(Output()), default);

        store = SqliteTranscriptAnalysisStore.Open(files.Database);
        var snapshot = await store.LoadAsync(identity, default);
        Assert.Equal(AnalysisStoreStatus.Complete, snapshot.Status);
        Assert.Null(snapshot.Unfinished);
        Assert.Equal(revision, snapshot.Completed);
        Assert.Equal(new(1, "Index", "source:18"), snapshot.Checkpoint);
        var chunks = await Collect(store.ReadChunksAsync(snapshot.Completed!, default));
        Assert.Equal("Ana sends draft.", Assert.Single(chunks).Text);
        Assert.Equal(new float[] { 0.25f, -0.75f }, chunks[0].Vector);
        var output = Assert.Single(await Collect(store.ReadOutputsAsync(snapshot.Completed!, default)));
        Assert.Equal("Summary", output.Kind);
        Assert.Equal("Draft handoff", output.Content);
        var citation = Assert.Single(Assert.Single(output.Claims).Evidence);
        Assert.Equal(new("source@0", 1200, "Ana sends draft."), citation);
    }

    private static AnalysisStoreIdentity Identity() => new(Guid.NewGuid(), new('A', 64), new('B', 64),
        "English", Model("generation", new('C', 64), "context=512;seed=0"),
        Model("embedding", new('E', 64), "pooling=mean;prefix=v1"), "analysis-v1", 2);

    private static AnalysisStoreModel Model(string id, string sha256, string configuration) =>
        new(id, sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration))), configuration);

    private static AnalysisStoreChunk Chunk(int ordinal = 0) =>
        new(ordinal, "source", 0, 1200, 2200, "Ana sends draft.", [0.25f, -0.75f]);

    private static AnalysisStoreOutput Output(int ordinal = 0, string content = "Draft handoff") =>
        new(ordinal, "Summary", null, content,
            [new("Ana sends draft", [new("source@0", 1200, "Ana sends draft.")])]);

    private static async IAsyncEnumerable<T> Stream<T>(T value,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<T> Pair<T>(T first, T second)
    {
        yield return first;
        await Task.Yield();
        yield return second;
    }

    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<T> CancelledStream<T>(T first, T invalidNext,
        CancellationTokenSource cancellation, bool atEnd)
    {
        yield return first;
        cancellation.Cancel();
        await Task.Yield();
        if (!atEnd) yield return invalidNext;
    }

    private static async Task<T> Failure<T>(Func<Task> action) where T : Exception
    {
        var error = await Assert.ThrowsAsync<T>(action);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        return error;
    }

    private static async IAsyncEnumerable<AnalysisStoreOutput> BrokenOutputs(bool cancel,
        CancellationTokenSource cancellation)
    {
        yield return Output(content: "Never activated");
        await Task.Yield();
        if (cancel)
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        throw new IOException("Controlled output-stream failure");
    }

    private static async IAsyncEnumerable<AnalysisStoreOutput> CrashOutputs(string directory)
    {
        yield return Output(content: "Uncommitted before forced process termination");
        await File.WriteAllTextAsync(Path.Combine(directory, "transaction-ready"), "ready");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async IAsyncEnumerable<AnalysisStoreOutput> HeldOutputs(TaskCompletionSource entered,
        TaskCompletionSource release)
    {
        yield return Output(content: "Replacement");
        entered.SetResult();
        await release.Task;
    }

    private static async IAsyncEnumerable<AnalysisStoreChunk> CancelledChunks(CancellationTokenSource cancellation)
    {
        yield return Chunk();
        cancellation.Cancel();
        await Task.Yield();
        yield return Chunk(1);
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed class StoreFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Environment.CurrentDirectory, "artifacts",
            "analysis-storage-tests", Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Directory, "analysis.sqlite");
        public StoreFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
