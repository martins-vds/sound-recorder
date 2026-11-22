using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingDeletionMutationTests : IDisposable
{
    private readonly LibraryDeletionFixture _fixture = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRecordingAsync_IntentOrHistoryCleanupCheckpointFails_ReportsPersistenceRatherThanOwnershipOrWorkers(
        bool afterJobRemoval)
    {
        await _fixture.InitializeAsync();
        FileStream? blocker = null;
        var writes = 0;
        var history = new InterceptingHistory(_fixture.History, _ =>
        {
            if (!afterJobRemoval && ++writes == 2)
                blocker = File.Open(_fixture.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        });
        var library = _fixture.CreateLibrary(history: history, deleteJob: async (id, token) =>
        {
            await _fixture.DeleteJobAsync(id, token);
            if (afterJobRemoval)
                blocker = File.Open(_fixture.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        try
        {
            var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

            Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
            Assert.Equal(2, result.Issues.Count);
            Assert.All(result.Issues, issue => Assert.Equal(LibraryFailureCode.PersistenceFailed, issue.Code));
            Assert.Contains(result.Issues, issue => issue.Message.Contains("progress could not be saved"));
            Assert.Contains(result.Issues, issue => issue.Message.Contains("latest deletion outcome could not be persisted"));
            Assert.Equal(afterJobRemoval, !File.Exists(_fixture.Recording.AudioPath));
            if (afterJobRemoval)
            {
                Assert.Empty(result.PendingDeletion!.RemainingJobIds);
                Assert.Empty(await _fixture.Jobs.LoadAsync(default));
            }
            else
            {
                Assert.Empty(_fixture.DeletedJobs);
                Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
            }
        }
        finally { blocker?.Dispose(); }
        var retry = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Completed, retry.Status);
        Assert.Empty(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_EntryDisappearsAfterAudioCheckpoint_StopsAndLogsTheLostOwnershipSnapshot()
    {
        await _fixture.InitializeAsync();
        var removed = false;
        var history = new AfterUpdateHistory(_fixture.History, async recording =>
        {
            if (!removed && recording.AudioDeleted)
            {
                removed = true;
                await _fixture.History.RemoveAsync(recording.Id, default);
            }
        });

        var result = await _fixture.CreateLibrary(history: history).DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.All(result.Issues, issue => Assert.Equal(LibraryFailureCode.PersistenceFailed, issue.Code));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("InvalidDataException"));
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.SubtitlePath));
        Assert.Empty(_fixture.DeletedJobs);
    }

    [Theory]
    [InlineData("live", false)]
    [InlineData("audio-deleted", true)]
    [InlineData("receipt-removed", true)]
    public async Task DeleteRecordingAsync_CaptureHasNoTranscripts_ValidatesOnlyLiveAudioAndProtectsTombstones(
        string state, bool completed)
    {
        await _fixture.InitializeAsync();
        File.Delete(_fixture.Recording.AudioPath);
        Directory.CreateDirectory(_fixture.Recording.AudioPath);
        var protectedPath = Path.Combine(_fixture.Recording.AudioPath, "protected.txt");
        await File.WriteAllTextAsync(protectedPath, "Directory replacement must survive");
        var recording = _fixture.Recording with
        {
            AudioDeleted = state == "audio-deleted",
            PendingDeletion = state == "receipt-removed" ? new(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
                [new(RecordingArtifactKind.Audio, _fixture.Recording.AudioPath, null, ArtifactDeletionState.Removed, null)], [], null) : null,
        };
        await File.WriteAllTextAsync(_fixture.HistoryPath, JsonSerializer.Serialize(new[] { recording }));
        await _fixture.Jobs.SaveAsync([], default);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(completed ? LibraryDeletionStatus.Completed : LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal("Directory replacement must survive", await File.ReadAllTextAsync(protectedPath));
        if (completed)
        {
            Assert.Empty(result.Issues);
            Assert.Empty(await _fixture.History.LoadAsync(default));
        }
        else
        {
            Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
            Assert.Empty(_fixture.CancelledRecordings);
            Assert.Single(await _fixture.History.LoadAsync(default));
        }
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("text")]
    [InlineData("subtitle")]
    public async Task DeleteRecordingAsync_ImportedLibraryReferencesOneLiveArtifact_ProtectsForeignMetadata(string kind)
    {
        await _fixture.InitializeAsync();
        var foreignHistoryPath = Path.Combine(_fixture.Root, "foreign-history.json");
        var imported = new JsonRecordingHistory(foreignHistoryPath);
        var foreign = new SavedRecording(Guid.NewGuid(),
            kind == "audio" ? _fixture.Recording.AudioPath : Path.Combine(_fixture.Root, "foreign.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await imported.AddAsync(foreign, default);
        if (kind != "audio")
        {
            var pair = new OwnedTranscriptOutputs(Guid.NewGuid(), new(
                kind == "text" ? _fixture.Outputs[0].Outputs.TextPath : Path.Combine(_fixture.Root, "foreign.txt"),
                kind == "subtitle" ? _fixture.Outputs[0].Outputs.SubtitlePath : Path.Combine(_fixture.Root, "foreign.srt")));
            await imported.RegisterOutputsAsync(foreign.Id, pair, default);
        }
        var foreignBefore = await File.ReadAllBytesAsync(foreignHistoryPath);
        var mainBefore = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        var history = new ImportedRecordingHistory(_fixture.History, imported);

        var result = await _fixture.CreateLibrary(history: history).DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Equal(mainBefore, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Equal(foreignBefore, await File.ReadAllBytesAsync(foreignHistoryPath));
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal("Transcript attempt 1", await File.ReadAllTextAsync(_fixture.Outputs[0].Outputs.TextPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRecordingAsync_UnsupportedAudioReference_RefusesEvenWhenJobsMatchTheReference(bool relative)
    {
        await _fixture.InitializeAsync();
        var path = relative ? Path.GetRelativePath(Environment.CurrentDirectory, _fixture.Recording.AudioPath)
            : Path.ChangeExtension(_fixture.Recording.AudioPath, ".bin");
        if (!relative)
            File.Copy(_fixture.Recording.AudioPath, path);
        var recording = _fixture.Recording with { AudioPath = path };
        await _fixture.History.UpdateAsync(recording, default);
        var job = Assert.Single(await _fixture.Jobs.LoadAsync(default));
        await _fixture.Jobs.SaveAsync(
            [new(job.Id, recording, job.CreatedAt, job.Status, job.Outputs, job.Error, job.ModelId, job.InterruptionReason)], default);
        var before = await File.ReadAllBytesAsync(path);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
    }

    [Fact]
    public async Task DeleteJobHistoryAsync_TranscriptOwnershipIsInvalid_ReturnsActionableRejectionWithoutCallback()
    {
        await _fixture.InitializeAsync();
        var path = _fixture.Outputs[0].Outputs.TextPath;
        File.Delete(path);
        Directory.CreateDirectory(path);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        var result = await _fixture.CreateLibrary().DeleteJobHistoryAsync(_fixture.Outputs[0].JobId, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, issue.Code);
        Assert.Contains("original recording", issue.Message);
        Assert.Contains("shared references", issue.Message);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteJobHistoryAsync_JobChangesAfterOwnershipPublication_RefusesChangedJob(bool foreign)
    {
        await _fixture.InitializeAsync();
        var job = Assert.Single(await _fixture.Jobs.LoadAsync(default));
        var replacementRecording = _fixture.Recording with { Id = Guid.NewGuid(), AudioPath = Path.Combine(_fixture.Root, "foreign.wav") };
        var changed = false;
        var history = new InterceptingHistory(_fixture.History, async _ =>
        {
            if (changed)
                return;
            changed = true;
            await _fixture.Jobs.SaveAsync(
                [new(job.Id, foreign ? replacementRecording : job.Recording, job.CreatedAt,
                    foreign ? TranscriptionJobStatus.Succeeded : TranscriptionJobStatus.Queued,
                    job.Outputs, job.Error, job.ModelId, job.InterruptionReason)], default);
        });

        var result = await _fixture.CreateLibrary(history: history).DeleteJobHistoryAsync(job.Id, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(LibraryFailureCode.HistoryDeleteFailed, issue.Code);
        Assert.Contains("explicitly retry", issue.Message);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Equal(foreign ? replacementRecording.Id : job.Recording.Id,
            Assert.Single(await _fixture.Jobs.LoadAsync(default)).Recording.Id);
        Assert.Equal("Transcript attempt 1", await File.ReadAllTextAsync(_fixture.Outputs[0].Outputs.TextPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_EntryIsRemovedDuringPlaybackRelease_ReportsUnsavedProgressAndRetainsFiles()
    {
        await _fixture.InitializeAsync();
        var library = _fixture.CreateLibrary(release: (id, token) => _fixture.History.RemoveAsync(id, token));

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, issue => Assert.Equal(LibraryFailureCode.PersistenceFailed, issue.Code));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("latest deletion outcome could not be persisted"));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("InvalidDataException"));
        Assert.Empty(_fixture.DeletedJobs);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal("Transcript attempt 1", await File.ReadAllTextAsync(_fixture.Outputs[0].Outputs.TextPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_OwnershipChangesDuringPlaybackRelease_RejectsBeforeAnyArtifactIsRemoved()
    {
        await _fixture.InitializeAsync();
        var foreignSnapshot = _fixture.Recording with { AudioPath = Path.Combine(_fixture.Root, "foreign.wav") };
        var intentsWithoutFailure = 0;
        var history = new AfterUpdateHistory(_fixture.History, recording =>
        {
            if (recording.PendingDeletion?.Failure is null)
                intentsWithoutFailure++;
            return Task.CompletedTask;
        });
        var library = _fixture.CreateLibrary(history: history, release: async (_, token) =>
        {
            var job = Assert.Single(await _fixture.Jobs.LoadAsync(token));
            await _fixture.Jobs.SaveAsync(
                [new(job.Id, foreignSnapshot, job.CreatedAt, job.Status, job.Outputs, job.Error, job.ModelId, job.InterruptionReason)], token);
        });

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Contains("ownership changed", result.Issues[0].Message);
        Assert.Equal(1, intentsWithoutFailure);
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("Deletion ownership validation failed"));
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal("Transcript attempt 1", await File.ReadAllTextAsync(_fixture.Outputs[0].Outputs.TextPath));
        Assert.Empty(_fixture.DeletedJobs);
    }

    [Fact]
    public async Task DeleteRecordingAsync_AudioIntentIsAlreadyDeleted_OverridesAnOlderPendingAudioReceipt()
    {
        await _fixture.InitializeAsync();
        var recording = Assert.Single(await _fixture.History.LoadAsync(default));
        File.Delete(recording.AudioPath);
        Directory.CreateDirectory(recording.AudioPath);
        var replacement = Path.Combine(recording.AudioPath, "protected.txt");
        await File.WriteAllTextAsync(replacement, "Replacement directory is not the old audio");
        var output = _fixture.Outputs[0];
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Pending, null),
            new(RecordingArtifactKind.Text, output.Outputs.TextPath, output.JobId, ArtifactDeletionState.Pending, null),
            new(RecordingArtifactKind.Subtitle, output.Outputs.SubtitlePath, output.JobId, ArtifactDeletionState.Pending, null),
        ], [output.JobId], null);
        await _fixture.History.UpdateAsync(recording with { AudioDeleted = true, PendingDeletion = pending }, default);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal("Replacement directory is not the old audio", await File.ReadAllTextAsync(replacement));
        Assert.Empty(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_CanceledAfterSnapshotWithUnavailableOrchestration_DoesNotReturnARejection()
    {
        await _fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var history = new AfterLoadHistory(_fixture.History, () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        var library = new RecordingLibrary(history, logger: _fixture.Logger);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AudioOnly, cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Empty(_fixture.Logger.Entries);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_CanceledAfterRefreshingFinishedIntent_DoesNotRemoveTheEntry()
    {
        await _fixture.InitializeAsync();
        var recording = Assert.Single(await _fixture.History.LoadAsync(default));
        var output = _fixture.Outputs[0];
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Text, output.Outputs.TextPath, output.JobId, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Subtitle, output.Outputs.SubtitlePath, output.JobId, ArtifactDeletionState.Removed, null),
        ], [], null);
        foreach (var artifact in pending.Artifacts)
            File.Delete(artifact.Path);
        await _fixture.History.UpdateAsync(recording with { AudioDeleted = true, PendingDeletion = pending }, default);
        await _fixture.Jobs.SaveAsync([], default);
        using var cancellation = new CancellationTokenSource();
        var updates = 0;
        var history = new InterceptingHistory(_fixture.History, _ =>
        {
            if (++updates == 2)
                cancellation.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.CreateLibrary(history: history)
            .DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, cancellation.Token));

        var saved = Assert.Single(await new JsonRecordingHistory(_fixture.HistoryPath).LoadAsync(default));
        Assert.Equal(pending.OperationId, saved.PendingDeletion!.OperationId);
        Assert.Equal(LibraryFailureCode.DeletionCanceled, saved.PendingDeletion.Failure!.Code);
        Assert.All(saved.PendingDeletion.Artifacts, artifact => Assert.Equal(ArtifactDeletionState.Removed, artifact.State));
        Assert.Empty(_fixture.DeletedJobs);
    }

    [Theory]
    [InlineData(TranscriptionJobStatus.Succeeded)]
    [InlineData(TranscriptionJobStatus.Failed)]
    public async Task DeleteRecordingAsync_UnrelatedTerminalOutputs_DoNotBecomePartOfThisRemoval(
        TranscriptionJobStatus status)
    {
        await _fixture.InitializeAsync();
        var foreign = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "foreign.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        var pair = new TranscriptOutputs(Path.Combine(_fixture.Root, "foreign.txt"), Path.Combine(_fixture.Root, "foreign.srt"));
        await File.WriteAllTextAsync(foreign.AudioPath, "Foreign audio bytes");
        await File.WriteAllTextAsync(pair.TextPath, "Foreign text");
        await File.WriteAllTextAsync(pair.SubtitlePath, "Foreign subtitles");
        await _fixture.History.AddAsync(foreign, default);
        var job = new TranscriptionJob(Guid.NewGuid(), foreign, DateTimeOffset.UtcNow, status, pair, null);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), job], default);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal(job.Id, Assert.Single(await _fixture.Jobs.LoadAsync(default)).Id);
        Assert.Equal(foreign.Id, Assert.Single(await _fixture.History.LoadAsync(default)).Id);
        Assert.Equal("Foreign audio bytes", await File.ReadAllTextAsync(foreign.AudioPath));
        Assert.Equal("Foreign text", await File.ReadAllTextAsync(pair.TextPath));
        Assert.Equal("Foreign subtitles", await File.ReadAllTextAsync(pair.SubtitlePath));
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("text")]
    [InlineData("subtitle")]
    public async Task DeleteRecordingAsync_ForeignJobReferencesOneLiveArtifact_RejectsBeforeRelease(string kind)
    {
        await _fixture.InitializeAsync();
        var foreign = new SavedRecording(Guid.NewGuid(),
            kind == "audio" ? _fixture.Recording.AudioPath : Path.Combine(_fixture.Root, "foreign.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        var pair = kind == "audio" ? null : new TranscriptOutputs(
            kind == "text" ? _fixture.Outputs[0].Outputs.TextPath : Path.Combine(_fixture.Root, "foreign.txt"),
            kind == "subtitle" ? _fixture.Outputs[0].Outputs.SubtitlePath : Path.Combine(_fixture.Root, "foreign.srt"));
        var job = new TranscriptionJob(Guid.NewGuid(), foreign, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Failed, pair, null);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), job], default);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.ReleasedPlayback);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal("Transcript attempt 1", await File.ReadAllTextAsync(_fixture.Outputs[0].Outputs.TextPath));
        Assert.Equal(2, (await _fixture.Jobs.LoadAsync(default)).Count);
    }

    [Fact]
    public async Task DeleteRecordingAsync_PendingJobIsOwnedOnlyByAnotherLibraryEntry_RejectsIndependentForeignOwnership()
    {
        await _fixture.InitializeAsync();
        var foreign = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "foreign.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await _fixture.History.AddAsync(foreign, default);
        var pair = new OwnedTranscriptOutputs(Guid.NewGuid(),
            new(Path.Combine(_fixture.Root, "foreign.txt"), Path.Combine(_fixture.Root, "foreign.srt")));
        await File.WriteAllTextAsync(pair.Outputs.TextPath, "Independently retained foreign text");
        await File.WriteAllTextAsync(pair.Outputs.SubtitlePath, "Independently retained foreign subtitles");
        await _fixture.History.RegisterOutputsAsync(foreign.Id, pair, default);
        var recording = Assert.Single(await _fixture.History.LoadAsync(default), item => item.Id == _fixture.Recording.Id);
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
            [new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Pending, null)], [pair.JobId], null);
        await _fixture.History.UpdateAsync(recording with { PendingDeletion = pending }, default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Equal("Independently retained foreign text", await File.ReadAllTextAsync(pair.Outputs.TextPath));
    }

    [Theory]
    [InlineData("finished", true)]
    [InlineData("failed-text", false)]
    [InlineData("missing-text", false)]
    [InlineData("missing-subtitle", false)]
    [InlineData("unowned-paths", false)]
    public async Task DeleteRecordingAsync_ResidualHistoryIsAbsent_RequiresCompleteOwnedReceiptPair(
        string proof, bool completed)
    {
        await _fixture.InitializeAsync();
        var output = _fixture.Outputs[0];
        File.Delete(_fixture.Recording.AudioPath);
        var text = proof == "unowned-paths" ? Path.Combine(_fixture.Root, "unrelated.txt") : output.Outputs.TextPath;
        var subtitle = proof == "unowned-paths" ? Path.Combine(_fixture.Root, "unrelated.srt") : output.Outputs.SubtitlePath;
        await File.WriteAllTextAsync(text, "Replacement text must not be removed without ownership");
        await File.WriteAllTextAsync(subtitle, "Replacement subtitle must not be removed without ownership");
        var receipts = new List<ArtifactDeletion>
        {
            new(RecordingArtifactKind.Audio, _fixture.Recording.AudioPath, null, ArtifactDeletionState.Removed, null),
        };
        if (proof != "missing-text")
            receipts.Add(new(RecordingArtifactKind.Text, text, output.JobId,
                proof == "failed-text" ? ArtifactDeletionState.Failed : ArtifactDeletionState.Removed, null));
        if (proof != "missing-subtitle")
            receipts.Add(new(RecordingArtifactKind.Subtitle, subtitle, output.JobId, ArtifactDeletionState.AlreadyMissing, null));
        var recording = _fixture.Recording with
        {
            AudioDeleted = true,
            PendingDeletion = new(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts, receipts, [], null),
        };
        await File.WriteAllTextAsync(_fixture.HistoryPath, JsonSerializer.Serialize(new[] { recording }));
        await _fixture.Jobs.SaveAsync([], default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(completed ? LibraryDeletionStatus.Completed : LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal("Replacement text must not be removed without ownership", await File.ReadAllTextAsync(text));
        Assert.Equal("Replacement subtitle must not be removed without ownership", await File.ReadAllTextAsync(subtitle));
        if (completed)
        {
            Assert.Empty(result.Issues);
            Assert.Empty(await _fixture.History.LoadAsync(default));
        }
        else
        {
            Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
            Assert.Empty(_fixture.CancelledRecordings);
            Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        }
    }

    [Fact]
    public async Task DeleteRecordingAsync_AudioRemovalFails_ReportsAudioPresentAfterSuccessfulTranscriptRemoval()
    {
        await _fixture.InitializeAsync();
        using var locked = File.Open(_fixture.Recording.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(LibraryFailureCode.ArtifactDeleteFailed, issue.Code);
        Assert.Contains("Close programs", issue.Message);
        Assert.Contains("retry", issue.Message);
        var recording = Assert.Single(await new JsonRecordingHistory(_fixture.HistoryPath).LoadAsync(default));
        Assert.False(recording.AudioDeleted);
        Assert.True(File.Exists(recording.AudioPath));
        Assert.Equal(new[] { ArtifactDeletionState.Failed, ArtifactDeletionState.Removed, ArtifactDeletionState.Removed },
            recording.PendingDeletion!.Artifacts.Select(artifact => artifact.State));
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
    }

    [Theory]
    [InlineData(RecordingArtifactKind.Text)]
    [InlineData(RecordingArtifactKind.Subtitle)]
    public async Task DeleteRecordingAsync_RemovedTranscriptIsNowReferencedByForeignJob_ProtectsTheReplacement(
        RecordingArtifactKind removedKind)
    {
        await _fixture.InitializeAsync();
        var output = _fixture.Outputs[0];
        var held = removedKind == RecordingArtifactKind.Text ? output.Outputs.SubtitlePath : output.Outputs.TextPath;
        var removed = removedKind == RecordingArtifactKind.Text ? output.Outputs.TextPath : output.Outputs.SubtitlePath;
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var partial = await _fixture.CreateLibrary().DeleteRecordingAsync(
                _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
            Assert.Equal(LibraryDeletionStatus.Incomplete, partial.Status);
        }
        await File.WriteAllTextAsync(removed, "Foreign replacement must survive");
        var foreign = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "foreign.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        var foreignOutputs = removedKind == RecordingArtifactKind.Text
            ? new TranscriptOutputs(removed, Path.Combine(_fixture.Root, "foreign.srt"))
            : new TranscriptOutputs(Path.Combine(_fixture.Root, "foreign.txt"), removed);
        var foreignJob = new TranscriptionJob(Guid.NewGuid(), foreign, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded, foreignOutputs, null);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), foreignJob], default);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal("Foreign replacement must survive", await File.ReadAllTextAsync(removed));
        Assert.Equal(foreignJob.Id, Assert.Single(await _fixture.Jobs.LoadAsync(default)).Id);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("playback")]
    [InlineData("delete-job")]
    [InlineData("repository")]
    public async Task DeleteRecordingAsync_MissingCapability_RefusesBeforeCreatingIntentOrCallingOtherCallbacks(string missing)
    {
        await _fixture.InitializeAsync();
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        var library = new RecordingLibrary(_fixture.History, missing == "repository" ? null : _fixture.Jobs,
            _fixture.Logger, missing == "cancel" ? null : _fixture.CancelAsync,
            missing == "delete-job" ? null : _fixture.DeleteJobAsync,
            missing == "playback" ? null : _fixture.ReleaseAsync);

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(LibraryFailureCode.OrchestrationUnavailable, issue.Code);
        Assert.Contains("unavailable", issue.Message);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.ReleasedPlayback);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("orchestration is not available"));
        Assert.All(_fixture.Logger.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Message)));
    }

    [Fact]
    public async Task DeleteRecordingAsync_AudioOnlyWithoutJobRepositoryOrDeletionCapability_KeepsOwnedOutputs()
    {
        await _fixture.InitializeAsync();
        var library = new RecordingLibrary(_fixture.History, null, _fixture.Logger,
            _fixture.CancelAsync, null, _fixture.ReleaseAsync);
        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);
        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteJobHistoryAsync_UnknownJob_ReturnsNotFoundWithoutErrorsOrCallbacks()
    {
        await _fixture.InitializeAsync();
        var result = await _fixture.CreateLibrary().DeleteJobHistoryAsync(Guid.NewGuid(), default);
        Assert.Equal(LibraryDeletionStatus.NotFound, result.Status);
        Assert.Empty(result.Issues);
        Assert.Null(result.PendingDeletion);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteJobHistoryAsync_RecordingEntryIsMissing_ExplainsWhyOwnershipCannotBeRetained()
    {
        await _fixture.InitializeAsync();
        await _fixture.History.RemoveAsync(_fixture.Recording.Id, default);
        var result = await _fixture.CreateLibrary().DeleteJobHistoryAsync(_fixture.Outputs[0].JobId, default);
        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, issue.Code);
        Assert.Contains("recording entry is required", issue.Message);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_CorruptMetadata_ReportsPreparationPersistenceFailureWithoutCancellation()
    {
        await _fixture.InitializeAsync();
        await File.WriteAllTextAsync(_fixture.HistoryPath, "{broken");
        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(LibraryFailureCode.PersistenceFailed, issue.Code);
        Assert.Contains("metadata", issue.Message);
        Assert.Contains("retry", issue.Message);
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("preparation failed"));
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Equal("{broken", await File.ReadAllTextAsync(_fixture.HistoryPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_HistoricalOutputsConflict_ReportsOwnershipFailureWithoutRewritingHistory()
    {
        await _fixture.InitializeAsync();
        var output = _fixture.Outputs[0];
        var conflicting = new TranscriptionJob(output.JobId, _fixture.Recording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded,
            new(Path.Combine(_fixture.Root, "meeting.transcript-2.txt"), Path.Combine(_fixture.Root, "meeting.transcript-2.srt")), null);
        await _fixture.Jobs.SaveAsync([conflicting], default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Contains("retry", result.Issues[0].Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("preparation failed"));
    }

    [Fact]
    public async Task DeleteRecordingAsync_AllRelatedTerminalAttempts_RemovesOnlyTheirHistoryIncludingAttemptsWithoutOutputs()
    {
        await _fixture.InitializeAsync();
        var failed = new TranscriptionJob(Guid.NewGuid(), _fixture.Recording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Failed, null, "Failed attempt");
        var canceled = new TranscriptionJob(Guid.NewGuid(), _fixture.Recording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Canceled, null, null);
        var foreign = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "other.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await _fixture.History.AddAsync(foreign, default);
        var foreignJob = new TranscriptionJob(Guid.NewGuid(), foreign, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Canceled, null, null);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), failed, canceled, foreignJob], default);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
        Assert.Equal(new[] { _fixture.Outputs[0].JobId, failed.Id, canceled.Id }.Order(), _fixture.DeletedJobs.Order());
        Assert.Equal(foreignJob.Id, Assert.Single(await _fixture.Jobs.LoadAsync(default)).Id);
        Assert.Equal(foreign.Id, Assert.Single(await _fixture.History.LoadAsync(default)).Id);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("kind")]
    [InlineData("job")]
    [InlineData("foreign-path")]
    public async Task DeleteRecordingAsync_PendingIdentityDoesNotMatchOwnedManifest_RefusesBeforeCancel(string invalid)
    {
        await _fixture.InitializeAsync();
        var recording = Assert.Single(await _fixture.History.LoadAsync(default));
        var output = _fixture.Outputs[0];
        var path = invalid == "foreign-path" ? Path.Combine(_fixture.Root, "unrelated.txt") : output.Outputs.TextPath;
        await File.WriteAllTextAsync(Path.Combine(_fixture.Root, "unrelated.txt"), "Unrelated protected bytes");
        var artifact = new ArtifactDeletion(invalid == "kind" ? RecordingArtifactKind.Subtitle : RecordingArtifactKind.Text,
            path, invalid == "job" ? Guid.NewGuid() : output.JobId, ArtifactDeletionState.Pending, null);
        var pending = new PendingRecordingDeletion(invalid == "operation" ? Guid.Empty : Guid.NewGuid(),
            RecordingDeletionScope.AllArtifacts,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Pending, null),
            artifact,
        ], [output.JobId], null);
        await _fixture.History.UpdateAsync(recording with { PendingDeletion = pending }, default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Contains("path", result.Issues[0].Message);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.True(File.Exists(recording.AudioPath));
        Assert.Equal("Unrelated protected bytes", await File.ReadAllTextAsync(Path.Combine(_fixture.Root, "unrelated.txt")));
    }

    [Fact]
    public async Task DeleteRecordingAsync_AbsentArtifacts_ReportsAlreadyMissingBeforeAnyHistoryRemoval()
    {
        await _fixture.InitializeAsync();
        File.Delete(_fixture.Recording.AudioPath);
        File.Delete(_fixture.Outputs[0].Outputs.TextPath);
        var library = _fixture.CreateLibrary(deleteJob: async (id, token) =>
        {
            var pending = Assert.Single(await _fixture.History.LoadAsync(token)).PendingDeletion!;
            Assert.Equal(ArtifactDeletionState.AlreadyMissing, pending.Artifacts[0].State);
            Assert.Equal(ArtifactDeletionState.AlreadyMissing, pending.Artifacts[1].State);
            Assert.Equal(ArtifactDeletionState.Removed, pending.Artifacts[2].State);
            await _fixture.DeleteJobAsync(id, token);
        });
        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task DeleteRecordingAsync_NonCancellationFailureAfterCallerCancellation_StillReportsTheActualFailure()
    {
        await _fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var library = _fixture.CreateLibrary(cancel: (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException(new IOException("Controlled worker failure after caller cancellation"));
        });
        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AudioOnly, cancellation.Token);
        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(LibraryFailureCode.WorkerReleaseFailed, Assert.Single(result.Issues).Code);
        Assert.Contains("retry", result.Issues[0].Message);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    public void Dispose() => _fixture.Dispose();
}

internal sealed class ImportedRecordingHistory(IRecordingHistory primary, IRecordingHistory imported) : IRecordingHistory
{
    public async Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token) =>
        [.. await primary.LoadAsync(token), .. await imported.LoadAsync(token)];

    public Task AddAsync(SavedRecording recording, CancellationToken token) => primary.AddAsync(recording, token);
    public Task UpdateAsync(SavedRecording recording, CancellationToken token) => primary.UpdateAsync(recording, token);
    public Task RemoveAsync(Guid id, CancellationToken token) => primary.RemoveAsync(id, token);
    public Task RegisterOutputsAsync(Guid id, OwnedTranscriptOutputs outputs, CancellationToken token) =>
        primary.RegisterOutputsAsync(id, outputs, token);
}

internal sealed class AfterUpdateHistory(IRecordingHistory inner, Func<SavedRecording, Task> afterUpdate) : IRecordingHistory
{
    public Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
    public Task AddAsync(SavedRecording recording, CancellationToken token) => inner.AddAsync(recording, token);
    public Task RemoveAsync(Guid id, CancellationToken token) => inner.RemoveAsync(id, token);
    public Task RegisterOutputsAsync(Guid id, OwnedTranscriptOutputs outputs, CancellationToken token) =>
        inner.RegisterOutputsAsync(id, outputs, token);
    public async Task UpdateAsync(SavedRecording recording, CancellationToken token)
    {
        await inner.UpdateAsync(recording, token);
        await afterUpdate(recording);
    }
}
