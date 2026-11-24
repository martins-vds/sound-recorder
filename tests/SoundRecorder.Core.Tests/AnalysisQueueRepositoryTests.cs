using SoundRecorder.Infrastructure.Analysis.Storage;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class AnalysisQueueRepositoryTests
{
    [Fact]
    public async Task LoadAsync_MissingJobMetadata_RejectsInsteadOfReportingRestoredWork()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scheduling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = AnalysisQueueRepository.Create(Path.Combine(directory, "jobs.sqlite"));
            await repository.SaveAsync([null!], default);
            Assert.Equal("Scheduling journal contains a missing job.",
                (await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(default))).Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LoadAndSaveAsync_CancelledBeforeMissingFileAccess_ReportCancellationWithoutStorageFallback()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scheduling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "jobs.sqlite");
            var repository = AnalysisQueueRepository.Create(path);
            File.Delete(path);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.LoadAsync(cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync([], cancelled.Token));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Open_InvalidDatabase_ReleasesOwnedFileHandleAfterFailure()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scheduling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "jobs.sqlite");
            File.WriteAllText(path, "Not a SQLite database");
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => AnalysisQueueRepository.Open(path));
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("version")]
    public async Task LoadAsync_CorruptPrivateJournal_RejectsInsteadOfRetargetingWork(string corruption)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scheduling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "jobs.sqlite");
            var repository = AnalysisQueueRepository.Create(path);
            Assert.Empty(await repository.LoadAsync(default));
            var identity = new AnalysisStoreIdentity(Guid.NewGuid(), new('A', 64), new('B', 64), "English",
                new("phi", new('C', 64), new('D', 64), "{}"), new("nomic", new('E', 64), new('F', 64), "{}"), "v1", 2);
            var request = new AnalysisJobRequest(new(identity.AttemptId, new("one.txt", "one.srt"), "English", null), identity, "PINNED-MODEL-DIRECTORY");
            await repository.SaveAsync([new(Guid.NewGuid(), request, DateTimeOffset.UtcNow, AnalysisJobStatus.Queued)], default);
            var bytes = await File.ReadAllBytesAsync(path);
            if (corruption == "version") bytes[63] = 2;
            else
            {
                var offset = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("PINNED-MODEL-DIRECTORY"));
                Assert.True(offset >= 0);
                bytes[offset] = (byte)'Z';
            }
            await File.WriteAllBytesAsync(path, bytes);
            if (corruption == "version")
            {
                Assert.Equal("Unsupported analysis scheduling journal version.",
                    Assert.Throws<InvalidDataException>(() => AnalysisQueueRepository.Open(path)).Message);
                await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(default));
                await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync([], default));
            }
            else
                Assert.Equal("Scheduling journal integrity verification failed.",
                    (await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(default))).Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SaveAsync_ReopenedJournal_RetainsOrderedPinsAndPreviousCommitOnCancellation()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scheduling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "jobs.sqlite");
            var repository = AnalysisQueueRepository.Create(path);
            var identity = new AnalysisStoreIdentity(Guid.NewGuid(), new('A', 64), new('B', 64), "English",
                new("phi", new('C', 64), new('D', 64), "{}"), new("nomic", new('E', 64), new('F', 64), "{}"), "v1", 2);
            var request = new AnalysisJobRequest(new(identity.AttemptId, new("one.txt", "one.srt"), "English", "What was decided?"), identity,
                "verified-model-cache", AnalysisJobMode.GlobalQuestion, new(true, "The release decision"));
            var original = new AnalysisJob(Guid.NewGuid(), request, DateTimeOffset.Parse("2026-11-24T00:00:00Z"), AnalysisJobStatus.Interrupted,
                new(Guid.NewGuid(), identity));
            var retry = new AnalysisJob(Guid.NewGuid(), request, original.CreatedAt.AddSeconds(1), AnalysisJobStatus.Queued, PreviousJobId: original.Id);
            await repository.SaveAsync([original, retry], default);
            repository = AnalysisQueueRepository.Open(path);
            Assert.Equal(new[] { original, retry }, await repository.LoadAsync(default));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync([], cancelled.Token));
            Assert.Equal(new[] { original, retry }, await repository.LoadAsync(default));
            Assert.Throws<IOException>(() => AnalysisQueueRepository.Create(path));
            Assert.Equal("Local analysis scheduling journal is missing.",
                Assert.Throws<FileNotFoundException>(() => AnalysisQueueRepository.Open(Path.Combine(directory, "absent.sqlite"))).Message);
            Assert.Throws<InvalidOperationException>(() => AnalysisQueueRepository.Create(@"\\not-contacted.invalid\share\queue.sqlite"));
            Assert.Contains("What was decided?", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            await repository.SaveAsync([], default);
            Assert.Empty(await AnalysisQueueRepository.Open(path).LoadAsync(default));
            Assert.DoesNotContain("What was decided?", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
