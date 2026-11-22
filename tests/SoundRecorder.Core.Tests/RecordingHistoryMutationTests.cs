using System.Text.Json;
using System.Text.Json.Nodes;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingHistoryMutationTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();

    [Theory]
    [InlineData("Outcome")]
    [InlineData("CapturedDuration")]
    public async Task LoadAsync_MissingRequiredLegacyMetadata_RejectsRatherThanInventingDefaults(string property)
    {
        var record = Recording();
        var json = JsonSerializer.SerializeToNode(new[] { record })!;
        json[0]!.AsObject().Remove(property);
        await AssertRejectedStorageAsync(json.ToJsonString());
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("language")]
    [InlineData("outcome")]
    [InlineData("duration")]
    public async Task LoadAsync_InvalidRecordingMetadata_ReportsTheInvalidLibraryAndPreservesIt(string invalid)
    {
        var recording = Recording();
        recording = invalid switch
        {
            "audio" => recording with { AudioPath = " " },
            "language" => recording with { Language = " " },
            "outcome" => recording with { Outcome = (RecordingOutcome)999 },
            _ => recording with { CapturedDuration = TimeSpan.FromTicks(-1) },
        };
        await AssertRejectedStorageAsync(JsonSerializer.Serialize(new[] { recording }));
    }

    [Fact]
    public async Task AddAsync_ZeroCapturedDuration_PreservesTheEmptyCaptureWithoutInventingAudio()
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording() with { CapturedDuration = TimeSpan.Zero };
        await history.AddAsync(recording, default);
        Assert.Equal(TimeSpan.Zero, Assert.Single(await history.LoadAsync(default)).CapturedDuration);
        Assert.False(File.Exists(recording.AudioPath));
    }

    [Theory]
    [InlineData("null-entry")]
    [InlineData("empty-id")]
    [InlineData("null-pair")]
    [InlineData("empty-text")]
    [InlineData("empty-subtitles")]
    public async Task UpdateAsync_InvalidOwnedPair_IsRejectedWithoutDroppingExistingMetadata(string invalid)
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        await history.AddAsync(recording, default);
        var pair = new OwnedTranscriptOutputs(Guid.NewGuid(),
            new(_directory.PathFor("meeting.txt"), _directory.PathFor("meeting.srt")));
        OwnedTranscriptOutputs output = invalid switch
        {
            "null-entry" => null!,
            "empty-id" => pair with { JobId = Guid.Empty },
            "null-pair" => pair with { Outputs = null! },
            "empty-text" => pair with { Outputs = pair.Outputs with { TextPath = " " } },
            _ => pair with { Outputs = pair.Outputs with { SubtitlePath = " " } },
        };
        var before = await File.ReadAllBytesAsync(path);

        var error = await Assert.ThrowsAsync<JsonException>(() =>
            history.UpdateAsync(recording with { TranscriptOutputs = [output] }, default));

        Assert.Contains("transcript output", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Assert.Single(await history.LoadAsync(default)).TranscriptOutputs);
    }

    [Fact]
    public async Task RegisterOutputsAsync_IdempotentRegistrationAndMissingRemoval_DoNotRewriteStorage()
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        var pair = new OwnedTranscriptOutputs(Guid.NewGuid(),
            new(_directory.PathFor("meeting.txt"), _directory.PathFor("meeting.srt")));
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, pair, default);
        var unchangedAt = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, unchangedAt);

        await history.RegisterOutputsAsync(recording.Id, pair, default);
        await history.RemoveAsync(Guid.NewGuid(), default);

        Assert.Equal(unchangedAt, File.GetLastWriteTimeUtc(path));
        Assert.Equal(pair, Assert.Single(Assert.Single(await history.LoadAsync(default)).TranscriptOutputs));
    }

    [Fact]
    public async Task UpdateAsync_AnyUnfinishedArtifact_KeepsIntentEvenWhenAnotherArtifactIsFinished()
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Text, _directory.PathFor("meeting.txt"), Guid.NewGuid(), ArtifactDeletionState.Pending, null),
        ], [], null);
        await history.AddAsync(recording with { AudioDeleted = true, PendingDeletion = pending }, default);

        await history.UpdateAsync(recording with { Title = "Retained request", PendingDeletion = null }, default);

        var saved = Assert.Single(await history.LoadAsync(default));
        Assert.NotNull(saved.PendingDeletion);
        Assert.Equal(pending.OperationId, saved.PendingDeletion.OperationId);
    }

    [Fact]
    public async Task LoadAsync_ReaderCompletesOldSnapshot_AtomicPublisherCanReplaceThePath()
    {
        var path = _directory.PathFor("history.json");
        var replacement = _directory.PathFor("replacement.json");
        var recording = Recording() with { Title = new string('x', 32 * 1024 * 1024) };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { recording }));
        await File.WriteAllTextAsync(replacement, "[]");
        var read = new JsonRecordingHistory(path).LoadAsync(default);
        try
        {
            Assert.False(read.IsCompleted);
            var snapshot = _directory.PathFor("old-snapshot.json");
            var error = Record.Exception(() =>
            {
                File.Move(path, snapshot);
                File.Move(replacement, path);
            });
            Assert.Null(error);
        }
        finally { await read; }
        Assert.Equal(recording.Id, Assert.Single(await read).Id);
        Assert.Empty(await new JsonRecordingHistory(path).LoadAsync(default));
    }

    [Fact]
    public async Task UpdateAsync_ReadOnlySnapshotIsStillOpen_PublishesNewHistoryWithoutChangingOldSnapshot()
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        await history.AddAsync(recording, default);
        using var snapshot = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(snapshot);

        var error = await Record.ExceptionAsync(() => history.UpdateAsync(recording with { Title = "New version" }, default));

        Assert.Null(error);
        Assert.DoesNotContain("New version", await reader.ReadToEndAsync());
        Assert.Equal("New version", Assert.Single(await new JsonRecordingHistory(path).LoadAsync(default)).Title);
    }

    private async Task AssertRejectedStorageAsync(string json)
    {
        var path = _directory.PathFor("history.json");
        await File.WriteAllTextAsync(path, json);
        var logger = new StorageTestLogger<JsonRecordingHistory>();
        var history = new JsonRecordingHistory(path, logger);
        var error = await Assert.ThrowsAnyAsync<JsonException>(() => history.LoadAsync(default));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    private SavedRecording Recording() => new(Guid.NewGuid(), _directory.PathFor("meeting.wav"),
        "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));

    public void Dispose() => _directory.Dispose();
}
