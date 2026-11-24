using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Core.Tests;

public sealed class StorageBoundaryTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();

    [Fact]
    public async Task History_AddAndReload_PreservesEveryRecordingAndReleasesGate()
    {
        var path = _directory.PathFor("nested", "history.json");
        var logger = new StorageTestLogger<JsonRecordingHistory>();
        var history = new JsonRecordingHistory(path, logger);
        Assert.Empty(await history.LoadAsync(default));
        var first = Recording("first.wav", RecordingOutcome.Interrupted);
        var second = Recording("second.wav", RecordingOutcome.Recovered);

        await history.AddAsync(first, default);
        Assert.Equal([first], await history.LoadAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
        await history.AddAsync(second, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([first, second], await new JsonRecordingHistory(path).LoadAsync(default));
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Message)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData("[]")]
    public async Task History_LoadEmptyJson_ReturnsEmptyHistory(string json)
    {
        var path = _directory.PathFor("history.json");
        await File.WriteAllTextAsync(path, json);
        Assert.Empty(await new JsonRecordingHistory(path).LoadAsync(default));
    }

    [Fact]
    public async Task History_FailedRead_ReleasesGateAndPreservesOriginalFile()
    {
        var path = _directory.PathFor("history.json");
        await File.WriteAllTextAsync(path, "{broken");
        var history = new JsonRecordingHistory(path);
        await Assert.ThrowsAsync<JsonException>(() => history.AddAsync(Recording("a.wav"), default));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "[]");
        await history.AddAsync(Recording("b.wav"), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(await history.LoadAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task History_ConcurrentAppends_PersistsEveryRecordingOnce()
    {
        var path = _directory.PathFor("concurrent.json");
        var history = new JsonRecordingHistory(path);
        await File.WriteAllTextAsync(path, "[]");
        var recordings = Enumerable.Range(0, 20).Select(index => Recording($"{index}.wav")).ToArray();
        await Task.WhenAll(recordings.Select(recording => history.AddAsync(recording, default)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var restored = await history.LoadAsync(default);
        Assert.Equal(recordings.Select(recording => recording.Id).Order(),
            restored.Select(recording => recording.Id).Order());
    }

    [Fact]
    public async Task Jobs_SaveReloadAndReplace_PreservesOrderAndAllJobState()
    {
        var path = _directory.PathFor("nested", "jobs.json");
        var logger = new StorageTestLogger<JsonTranscriptionJobRepository>();
        var repository = new JsonTranscriptionJobRepository(path, logger);
        Assert.Empty(await repository.LoadAsync(default));
        var created = DateTimeOffset.Parse("2026-03-04T05:06:07+02:00");
        var jobs = new[]
        {
            new TranscriptionJob(Guid.NewGuid(), Recording("a.wav"), created,
                TranscriptionJobStatus.Succeeded, new TranscriptOutputs("a.txt", "a.srt"), null),
            new TranscriptionJob(Guid.NewGuid(), Recording("b.wav"), created.AddSeconds(1),
                TranscriptionJobStatus.Failed, null, "Destination unavailable"),
        };
        await repository.SaveAsync(jobs, default);
        var persisted = await File.ReadAllTextAsync(path);
        Assert.Matches(@"(?m)^  \{\r?$", persisted);
        Assert.Matches(@"(?m)^    ""recording"": \{\r?$", persisted);
        Assert.Matches(@"(?m)^      ""audioPath"": """, persisted);
        var reloaded = await new JsonTranscriptionJobRepository(path, logger).LoadAsync(default);
        Assert.Equal(jobs.Length, reloaded.Count);
        for (var index = 0; index < jobs.Length; index++)
        {
            Assert.Equal(jobs[index].Id, reloaded[index].Id);
            Assert.Equal(jobs[index].Recording, reloaded[index].Recording);
            Assert.Equal(jobs[index].CreatedAt, reloaded[index].CreatedAt);
            Assert.Equal(jobs[index].Status, reloaded[index].Status);
            Assert.Equal(jobs[index].Outputs, reloaded[index].Outputs);
            Assert.Equal(jobs[index].Error, reloaded[index].Error);
        }

        await repository.SaveAsync([], default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await repository.LoadAsync(default));
        Assert.Equal(4, logger.Entries.Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Jobs_LoadEmptyJson_ReturnsNoJobs(string json)
    {
        var path = _directory.PathFor("jobs.json");
        await File.WriteAllTextAsync(path, json);
        Assert.Empty(await new JsonTranscriptionJobRepository(path).LoadAsync(default));
    }

    [Fact]
    public async Task Jobs_SaveWhileEarlierSnapshotIsBlocked_PersistsSnapshotsInCallOrder()
    {
        var path = _directory.PathFor("ordered.json");
        var repository = new JsonTranscriptionJobRepository(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var firstJobs = new ObservedJobs(new TranscriptionJob(Guid.NewGuid(), Recording("first.wav")), () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        });
        var firstSave = Task.Run(() => repository.SaveAsync(firstJobs, default));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondEnumerated = false;
        var secondJob = new TranscriptionJob(Guid.NewGuid(), Recording("second.wav"));
        Task secondSave;
        bool readBeforeRelease;
        try
        {
            secondSave = repository.SaveAsync(new ObservedJobs(secondJob, () => secondEnumerated = true), default);
            readBeforeRelease = secondEnumerated;
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(readBeforeRelease);
        Assert.Equal(secondJob.Id, Assert.Single(await repository.LoadAsync(default)).Id);
    }

    [Fact]
    public async Task Jobs_LoadMissingParentDirectory_ReturnsEmptyWithoutCreatingStorage()
    {
        var path = _directory.PathFor("not-created", "jobs.json");

        var jobs = await new JsonTranscriptionJobRepository(path).LoadAsync(default);

        Assert.Empty(jobs);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jobs_SaveFailsAfterStaging_PreservesSnapshotCleansOwnedStagingAndReleasesGate(bool cancel)
    {
        var path = _directory.PathFor("jobs.json");
        var repository = new JsonTranscriptionJobRepository(path);
        var original = new TranscriptionJob(Guid.NewGuid(), Recording("original.wav"));
        await repository.SaveAsync([original], default);
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        var replacement = new TranscriptionJob(Guid.NewGuid(), Recording("replacement.wav"));
        var failing = new ObservedJobs(replacement, () =>
        {
            Assert.Single(Directory.GetFiles(_directory.Root, "*.tmp"));
            if (cancel) cancellation.Cancel();
            else throw new IOException("fixture cannot read replacement snapshot");
        });

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SaveAsync(failing, cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => repository.SaveAsync(failing, default));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_directory.Root, "*.tmp"));
        Assert.Equal(original.Id, Assert.Single(await repository.LoadAsync(default)).Id);
        await repository.SaveAsync([replacement], default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(replacement.Id, Assert.Single(await new JsonTranscriptionJobRepository(path).LoadAsync(default)).Id);
        Assert.Empty(Directory.GetFiles(_directory.Root, "*.tmp"));
    }

    [Fact]
    public async Task Persistence_PreCanceledSave_DoesNotReplaceExistingData()
    {
        var path = _directory.PathFor("data.json");
        await File.WriteAllTextAsync(path, "[]");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JsonRecordingHistory(path).AddAsync(Recording("a.wav"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JsonTranscriptionJobRepository(path).SaveAsync([], cancellation.Token));
        Assert.Equal("[]", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Destination_LocalOrNetwork_RejectsOnlyNetworkPaths()
    {
        var method = typeof(LocalPathPolicy).GetMethod(nameof(LocalPathPolicy.RejectNetworkDestination), [typeof(string)]);
        Assert.NotNull(method);
        var rejectNetworkDestination = method.CreateDelegate<Action<string>>();
        rejectNetworkDestination(_directory.Root);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            rejectNetworkDestination(@"\\server\share"));
        Assert.Equal("Network destinations are not supported. Select a local folder.", exception.Message);
    }

    [Fact]
    public void Destination_MappedNetworkDrive_RejectsWithActionableExplanation()
    {
        string? inspectedRoot = null;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalPathPolicy.RejectNetworkDestination(_directory.Root, root =>
            {
                inspectedRoot = root;
                return DriveType.Network;
            }));
        Assert.Equal(Path.GetPathRoot(_directory.Root), inspectedRoot);
        Assert.Equal("Network destinations are not supported. Select a local folder.", exception.Message);
        LocalPathPolicy.RejectNetworkDestination(_directory.Root, _ => DriveType.Fixed);
    }

    [Fact]
    public void SynchronizedFolder_MultipleAndEmptyRoots_MatchesAnyKnownRootCaseInsensitively()
    {
        Assert.False(LocalPathPolicy.IsKnownSynchronizedFolder(_directory.Root, []));
        Assert.False(LocalPathPolicy.IsKnownSynchronizedFolder(_directory.Root, [null, "", " ", _directory.PathFor("other")]));
        Assert.True(LocalPathPolicy.IsKnownSynchronizedFolder(
            _directory.PathFor("sync", "recordings"), [null, "", " ", _directory.PathFor("other"), _directory.PathFor("SYNC")]));
    }

    private SavedRecording Recording(string name, RecordingOutcome outcome = RecordingOutcome.Complete) =>
        new(Guid.NewGuid(), _directory.PathFor(name), "Brazilian Portuguese", outcome, TimeSpan.FromTicks(123456789));

    public void Dispose() => _directory.Dispose();

    private sealed class ObservedJobs(TranscriptionJob job, Action onRead) : IReadOnlyList<TranscriptionJob>
    {
        public int Count => 1;

        public TranscriptionJob this[int index] => index == 0 ? job : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<TranscriptionJob> GetEnumerator()
        {
            onRead();
            yield return job;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal sealed class StorageTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(
        Directory.GetCurrentDirectory(), "artifacts", "storage-tests", Guid.NewGuid().ToString("N"));

    public StorageTestDirectory() => Directory.CreateDirectory(Root);

    public string PathFor(params string[] parts) => Path.Combine([Root, .. parts]);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class StorageTestLogger<T> : ILogger<T>
{
    public List<(EventId Event, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((eventId, formatter(state, exception)));
}
