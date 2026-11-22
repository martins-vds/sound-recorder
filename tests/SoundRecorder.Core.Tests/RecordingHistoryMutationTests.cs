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

    [Theory]
    [InlineData(RecordingDeletionScope.AllArtifacts, ArtifactDeletionState.Removed, true)]
    [InlineData(RecordingDeletionScope.AudioOnly, ArtifactDeletionState.Removed, false)]
    [InlineData(RecordingDeletionScope.AudioOnly, ArtifactDeletionState.Pending, true)]
    [InlineData(RecordingDeletionScope.AudioOnly, ArtifactDeletionState.Failed, true)]
    public async Task UpdateAsync_ClearingIntent_RetainsFullCleanupOrAnyUnfinishedOutcome(
        RecordingDeletionScope scope, ArtifactDeletionState state, bool retained)
    {
        var history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording();
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), scope,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Text, _directory.PathFor("meeting.txt"), Guid.NewGuid(), state, null),
        ], [], null);
        await history.AddAsync(recording with { PendingDeletion = pending }, default);

        await history.UpdateAsync(recording with { Title = "Updated title" }, default);

        var saved = Assert.Single(await history.LoadAsync(default));
        Assert.Equal("Updated title", saved.Title);
        Assert.Equal(retained, saved.PendingDeletion is not null);
        if (retained)
            Assert.Equal(pending.OperationId, saved.PendingDeletion!.OperationId);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("kind")]
    [InlineData("state")]
    [InlineData("mixed-kind")]
    [InlineData("audio-job")]
    [InlineData("duplicate")]
    public async Task UpdateAsync_InvalidDeletionMetadata_ReportsTheReasonAndRetainsPreviousVersion(string invalid)
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        await history.AddAsync(recording, default);
        var audio = new ArtifactDeletion(invalid is "kind" or "mixed-kind" ? (RecordingArtifactKind)999 : RecordingArtifactKind.Audio,
            recording.AudioPath, null, invalid == "state" ? (ArtifactDeletionState)999 : ArtifactDeletionState.Pending, null);
        var pending = new PendingRecordingDeletion(Guid.NewGuid(),
            invalid == "scope" ? (RecordingDeletionScope)999 : RecordingDeletionScope.AudioOnly,
            invalid == "duplicate" ? [audio, audio] : invalid == "mixed-kind"
                ? [audio, new(RecordingArtifactKind.Text, _directory.PathFor("meeting.txt"), Guid.NewGuid(), ArtifactDeletionState.Pending, null)]
                : [audio],
            invalid == "audio-job" ? [Guid.NewGuid()] : [], null);
        var before = await File.ReadAllBytesAsync(path);

        var error = await Assert.ThrowsAsync<JsonException>(() =>
            history.UpdateAsync(recording with { PendingDeletion = pending }, default));

        Assert.Contains(invalid switch
        {
            "audio-job" => "cannot remove job history",
            "duplicate" => "duplicate artifact",
            _ => "unknown pending-deletion",
        }, error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAsync_EmptyOrDuplicateRecordingId_ReportsInvalidIdentityWithoutPublishing(bool duplicate)
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        await history.AddAsync(recording, default);
        var before = await File.ReadAllBytesAsync(path);

        var error = await Assert.ThrowsAsync<JsonException>(() =>
            history.AddAsync(recording with { Id = duplicate ? recording.Id : Guid.Empty }, default));

        Assert.Contains("invalid or duplicate recording", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("null", "legacy array")]
    [InlineData("{\"Version\":2,\"Recordings\":[]}", "version is not supported")]
    public async Task LoadAsync_UnrecognizedStorage_ExplainsRequiredFormatWithoutReplacingIt(string json, string reason)
    {
        var path = _directory.PathFor("history.json");
        await File.WriteAllTextAsync(path, json);

        var error = await Assert.ThrowsAsync<JsonException>(() => new JsonRecordingHistory(path).LoadAsync(default));

        Assert.Contains(reason, error.Message);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
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
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutatingUnknownRecording_ExplainsMissingEntryAndPreservesStorage(bool register)
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        await history.AddAsync(Recording(), default);
        var before = await File.ReadAllBytesAsync(path);

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => register
            ? history.RegisterOutputsAsync(Guid.NewGuid(), new(Guid.NewGuid(),
                new(_directory.PathFor("missing.txt"), _directory.PathFor("missing.srt"))), default)
            : history.UpdateAsync(Recording(), default));

        Assert.Contains("no longer exists", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("same-job")]
    [InlineData("same-text")]
    [InlineData("same-subtitle")]
    public async Task UpdateAsync_ConflictingOutputOwnership_CannotPublishAnAmbiguousLibrary(string conflict)
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording();
        await history.AddAsync(recording, default);
        var first = new OwnedTranscriptOutputs(Guid.NewGuid(),
            new(_directory.PathFor("meeting.txt"), _directory.PathFor("meeting.srt")));
        await history.RegisterOutputsAsync(recording.Id, first, default);
        var second = new OwnedTranscriptOutputs(conflict == "same-job" ? first.JobId : Guid.NewGuid(),
            new(conflict == "same-text" ? first.Outputs.TextPath : _directory.PathFor("meeting.transcript-2.txt"),
                conflict == "same-subtitle" ? first.Outputs.SubtitlePath : _directory.PathFor("meeting.transcript-2.srt")));
        var before = await File.ReadAllBytesAsync(path);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            history.UpdateAsync(recording with { TranscriptOutputs = [second] }, default));

        Assert.Contains("already owned", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
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
