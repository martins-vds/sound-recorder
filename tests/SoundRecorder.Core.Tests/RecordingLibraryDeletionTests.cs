using NAudio.Wave;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingLibraryDeletionTests : IDisposable
{
    private readonly LibraryDeletionFixture _fixture = new();

    [Fact]
    public async Task DeleteRecordingAsync_AudioOnly_RetainsAllAttemptsAndHistory()
    {
        await _fixture.InitializeAsync(2);
        var before = await _fixture.OutputBytesAsync();

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        var saved = Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.True(saved.AudioDeleted);
        Assert.Null(saved.PendingDeletion);
        Assert.Equal(_fixture.Outputs, saved.TranscriptOutputs);
        Assert.Equal(2, (await _fixture.Jobs.LoadAsync(default)).Count);
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        Assert.Equal(new[] { saved.Id }, _fixture.CancelledRecordings);
        Assert.Equal(new[] { saved.Id }, _fixture.ReleasedPlayback);
        Assert.Empty(_fixture.DeletedJobs);
    }

    [Fact]
    public async Task DeleteRecordingAsync_AllArtifacts_RemovesOnlyOwnedFilesAndAssociatedHistory()
    {
        await _fixture.InitializeAsync(2);
        var neighbor = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "neighbor.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await _fixture.History.AddAsync(neighbor, default);
        var protectedFiles = new Dictionary<string, byte[]>
        {
            [neighbor.AudioPath] = [9, 8, 7],
            [Path.Combine(_fixture.Root, "unrelated.txt")] = [6, 5, 4],
            [Path.Combine(_fixture.Root, "downloaded-model.bin")] = [3, 2, 1],
        };
        foreach (var (path, bytes) in protectedFiles)
        {
            await File.WriteAllBytesAsync(path, bytes);
        }

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        foreach (var output in _fixture.Outputs)
        {
            Assert.False(File.Exists(output.Outputs.TextPath));
            Assert.False(File.Exists(output.Outputs.SubtitlePath));
        }
        Assert.Equal(neighbor.Id, Assert.Single(await _fixture.History.LoadAsync(default)).Id);
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
        Assert.Equal(_fixture.Outputs.Select(output => output.JobId).Order(), _fixture.DeletedJobs.Order());
        Assert.True(Directory.Exists(_fixture.Root));
        foreach (var (path, bytes) in protectedFiles)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task DeleteJobHistoryAsync_LegacyOutputs_PersistsOwnershipBeforeRemovingHistory()
    {
        await _fixture.InitializeAsync();
        var recording = _fixture.Recording;
        await File.WriteAllTextAsync(_fixture.HistoryPath, JsonSerializer.Serialize(new[]
        {
            new { recording.Id, recording.AudioPath, recording.Language, recording.Outcome, recording.CapturedDuration },
        }));
        var output = Assert.Single(_fixture.Outputs);
        var library = _fixture.CreateLibrary(deleteJob: async (jobId, token) =>
        {
            Assert.Equal(output, Assert.Single(Assert.Single(await _fixture.History.LoadAsync(token)).TranscriptOutputs));
            await _fixture.DeleteJobAsync(jobId, token);
        });

        var result = await library.DeleteJobHistoryAsync(output.JobId, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Empty(result.Issues);
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
        var retained = await library.ReadTranscriptAsync(recording.Id, output.JobId, default);
        Assert.Equal("Transcript attempt 1", retained.Text);
        var full = await library.DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Completed, full.Status);
        Assert.False(File.Exists(output.Outputs.TextPath));
        Assert.False(File.Exists(output.Outputs.SubtitlePath));
        Assert.Empty(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_PartialFailure_ReloadsWithoutDeletingAndRetriesOnlyRemainingFiles()
    {
        await _fixture.InitializeAsync();
        var output = Assert.Single(_fixture.Outputs);
        LibraryDeletionResult? result = null;
        using (File.Open(output.Outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(async () => result = await _fixture.CreateLibrary()
                .DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default));
            Assert.Null(error);
            Assert.NotNull(result);
            Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
            Assert.Equal(output.Outputs.TextPath, Assert.Single(result.Issues).Path);
            Assert.Equal(LibraryFailureCode.ArtifactDeleteFailed, Assert.Single(result.Issues).Code);
            var saved = Assert.Single(await _fixture.History.LoadAsync(default));
            Assert.True(saved.AudioDeleted);
            Assert.NotNull(saved.PendingDeletion);
            Assert.Equal(ArtifactDeletionState.Removed, saved.PendingDeletion.Artifacts[0].State);
            Assert.Equal(ArtifactDeletionState.Failed, saved.PendingDeletion.Artifacts[1].State);
            Assert.Equal(ArtifactDeletionState.Removed, saved.PendingDeletion.Artifacts[2].State);
            Assert.Single(await _fixture.Jobs.LoadAsync(default));
            Assert.Empty(_fixture.DeletedJobs);
        }

        await File.WriteAllTextAsync(_fixture.Recording.AudioPath, "Replacement file is not the deleted audio");
        var beforeReload = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        var reloaded = new RecordingLibrary(new JsonRecordingHistory(_fixture.HistoryPath), _fixture.Jobs);
        var entry = Assert.Single(await reloaded.LoadAsync(default));
        Assert.NotNull(entry.Recording.PendingDeletion);
        Assert.Equal(beforeReload, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.True(File.Exists(output.Outputs.TextPath));

        var retry = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, retry.Status);
        Assert.False(File.Exists(output.Outputs.TextPath));
        Assert.Equal("Replacement file is not the deleted audio", await File.ReadAllTextAsync(_fixture.Recording.AudioPath));
        Assert.Empty(await _fixture.History.LoadAsync(default));
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteRecordingAsync_ForeignPendingJobReference_RejectsBeforeCancellationOrRemoval()
    {
        await _fixture.InitializeAsync();
        var foreignRecording = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "foreign.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await _fixture.History.AddAsync(foreignRecording, default);
        var foreignJob = new TranscriptionJob(Guid.NewGuid(), foreignRecording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Canceled, null, null);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), foreignJob], default);
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
            [new(RecordingArtifactKind.Audio, _fixture.Recording.AudioPath, null, ArtifactDeletionState.Pending, null)],
            [foreignJob.Id], null);
        await _fixture.History.UpdateAsync(_fixture.Recording with { PendingDeletion = pending }, default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.ReleasedPlayback);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Contains(await _fixture.Jobs.LoadAsync(default), job => job.Id == foreignJob.Id);
    }

    [Fact]
    public async Task DeleteRecordingAsync_DirectoryDisguisedAsOutput_RejectsBeforeAnyCancellation()
    {
        await _fixture.InitializeAsync();
        var output = Assert.Single(_fixture.Outputs);
        File.Delete(output.Outputs.TextPath);
        Directory.CreateDirectory(output.Outputs.TextPath);
        var neighbor = Path.Combine(output.Outputs.TextPath, "unrelated.txt");
        await File.WriteAllTextAsync(neighbor, "Protected directory content");

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.ReleasedPlayback);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal("Protected directory content", await File.ReadAllTextAsync(neighbor));
    }

    [Theory]
    [InlineData(false, LibraryFailureCode.WorkerReleaseFailed)]
    [InlineData(true, LibraryFailureCode.PlaybackReleaseFailed)]
    public async Task DeleteRecordingAsync_ReleaseFailure_PersistsFailureWithoutRemovingAnyArtifact(
        bool playback, LibraryFailureCode expected)
    {
        await _fixture.InitializeAsync();
        var before = await _fixture.OutputBytesAsync();
        Func<Guid, CancellationToken, Task> fail = (_, _) => Task.FromException(new TimeoutException("Controlled release failure"));
        var library = playback ? _fixture.CreateLibrary(release: fail) : _fixture.CreateLibrary(cancel: fail);

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(expected, Assert.Single(result.Issues).Code);
        Assert.Contains(playback ? "Playback could not be closed" : "work could not be released",
            result.Issues[0].Message);
        Assert.Contains("retry", result.Issues[0].Message);
        var saved = Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.False(saved.AudioDeleted);
        Assert.Equal(expected, saved.PendingDeletion!.Failure!.Code);
        Assert.True(File.Exists(saved.AudioPath));
        Assert.Empty(_fixture.DeletedJobs);
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteRecordingAsync_RunningAndQueuedAttempts_AwaitsWorkerAndPlaybackReleaseBeforeRemoval()
    {
        await _fixture.InitializeAsync();
        var worker = new ControlledDeletionTranscriber();
        var queue = new TranscriptionQueue(worker, new AtomicTranscriptWriter(), _fixture.Jobs, history: _fixture.History);
        await queue.RestoreAsync(default);
        await queue.EnqueueAsync(_fixture.Recording, default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.EnqueueAsync(_fixture.Recording, default);
        using var playback = File.Open(_fixture.Recording.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var capturePath = Path.Combine(_fixture.Root, "unrelated-capture.wav");
        using var capture = File.Open(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        capture.Write([1, 2, 3]);
        var library = _fixture.CreateLibrary(cancel: async (id, token) =>
        {
            Assert.NotNull(Assert.Single(await _fixture.History.LoadAsync(token)).PendingDeletion);
            await queue.CancelRecordingAsync(id, token);
            Assert.True(worker.FileReleased);
            Assert.True(File.Exists(_fixture.Recording.AudioPath));
        }, release: (_, _) =>
        {
            Assert.True(worker.FileReleased);
            Assert.True(File.Exists(_fixture.Recording.AudioPath));
            playback.Dispose();
            return Task.CompletedTask;
        }, deleteJob: queue.DeleteJobAsync);
        var deletion = library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);
        try
        {
            await worker.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(deletion.IsCompleted);
            Assert.True(File.Exists(_fixture.Recording.AudioPath));
            capture.Write([4, 5, 6]);
        }
        finally { worker.Release.TrySetResult(); }

        var result = await deletion.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(default);
        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[1].Status);
        Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[2].Status);
        Assert.False(File.Exists(Path.Combine(_fixture.Root, "meeting.transcript-2.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(_fixture.Recording, default));
        capture.Flush();
        Assert.Equal(6, new FileInfo(capturePath).Length);
    }

    [Fact]
    public async Task ReadTranscriptAsync_DeletedOutputIsReplaced_DoesNotReadOrSearchTheReplacement()
    {
        await _fixture.InitializeAsync();
        var output = _fixture.Outputs[0];
        var library = _fixture.CreateLibrary();
        using (File.Open(output.Outputs.SubtitlePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
            Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        }
        await File.WriteAllTextAsync(output.Outputs.TextPath, "Unrelated replacement content");

        var transcript = await library.ReadTranscriptAsync(_fixture.Recording.Id, output.JobId, default);

        Assert.Null(transcript.Text);
        Assert.NotNull(transcript.Subtitles);
        Assert.Equal(output.Outputs.TextPath, Assert.Single(transcript.Issues).Path);
        Assert.Empty(await library.SearchAsync("Unrelated replacement", default));
        var retry = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Completed, retry.Status);
        Assert.Equal("Unrelated replacement content", await File.ReadAllTextAsync(output.Outputs.TextPath));
    }

    [Fact]
    public async Task DeleteJobHistoryAsync_CallbackFailure_ReportsFailureAndKeepsOwnedOutputs()
    {
        await _fixture.InitializeAsync();
        var library = _fixture.CreateLibrary(deleteJob: (_, _) => Task.FromException(new IOException("Controlled history failure")));
        LibraryDeletionResult? result = null;

        var error = await Record.ExceptionAsync(async () => result =
            await library.DeleteJobHistoryAsync(_fixture.Outputs[0].JobId, default));

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(LibraryFailureCode.HistoryDeleteFailed, Assert.Single(result.Issues).Code);
        Assert.Contains("Job history could not be removed", result.Issues[0].Message);
        Assert.Contains("retry", result.Issues[0].Message);
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
        Assert.Equal(_fixture.Outputs, Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs);
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_HistoryCallbackDidNotPersistRemoval_DoesNotReportComplete(bool allArtifacts)
    {
        await _fixture.InitializeAsync();
        var library = _fixture.CreateLibrary(deleteJob: (_, _) => Task.CompletedTask);

        var result = allArtifacts
            ? await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default)
            : await library.DeleteJobHistoryAsync(_fixture.Outputs[0].JobId, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(LibraryFailureCode.HistoryDeleteFailed, Assert.Single(result.Issues).Code);
        Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_CheckpointCannotBeSaved_StopsAndReportsTheActualAudioOutcome()
    {
        await _fixture.InitializeAsync();
        FileStream? blocked = null;
        var history = new InterceptingHistory(_fixture.History, recording =>
        {
            if (recording.AudioDeleted && blocked is null)
                blocked = File.Open(_fixture.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        });
        try
        {
            var result = await _fixture.CreateLibrary(history: history).DeleteRecordingAsync(
                _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
            Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
            Assert.Contains(result.Issues, issue => issue.Code == LibraryFailureCode.PersistenceFailed);
            Assert.Equal(2, result.Issues.Count);
            Assert.Contains(result.Issues, issue => issue.Message.Contains("progress could not be saved"));
            Assert.Contains(result.Issues, issue => issue.Message.Contains("latest deletion outcome could not be persisted"));
            Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("Recording deletion failed"));
            Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("Deletion failure persistence failed"));
            Assert.Equal(ArtifactDeletionState.Removed, result.PendingDeletion!.Artifacts[0].State);
            Assert.False(File.Exists(_fixture.Recording.AudioPath));
            Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
            Assert.True(File.Exists(_fixture.Outputs[0].Outputs.SubtitlePath));
            Assert.Single(await _fixture.Jobs.LoadAsync(default));
            Assert.NotNull(Assert.Single(await _fixture.History.LoadAsync(default)).PendingDeletion);
        }
        finally { blocked?.Dispose(); }
        var retry = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Completed, retry.Status);
        Assert.Empty(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_HistoryCleanupPartiallyFails_RetriesOnlyRemainingHistory()
    {
        await _fixture.InitializeAsync(2);
        var second = _fixture.Outputs[1].JobId;
        var fail = true;
        var calls = new List<Guid>();
        IReadOnlyList<Guid>? remainingBeforeSecondRemoval = null;
        var library = _fixture.CreateLibrary(deleteJob: async (id, token) =>
        {
            calls.Add(id);
            if (id == second && fail)
            {
                remainingBeforeSecondRemoval = Assert.Single(await _fixture.History.LoadAsync(token))
                    .PendingDeletion!.RemainingJobIds.ToArray();
                throw new IOException("Controlled remaining-history failure");
            }
            await _fixture.DeleteJobAsync(id, token);
        });
        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(second, Assert.Single(result.PendingDeletion!.RemainingJobIds));
        Assert.Equal(new[] { second }, remainingBeforeSecondRemoval);
        Assert.Equal(LibraryFailureCode.HistoryDeleteFailed, Assert.Single(result.Issues).Code);
        Assert.Contains("Associated job history could not be removed", result.Issues[0].Message);
        Assert.Contains("retry", result.Issues[0].Message);
        Assert.Equal(second, Assert.Single(await _fixture.Jobs.LoadAsync(default)).Id);
        fail = false;

        var retry = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, retry.Status);
        Assert.Equal(new[] { _fixture.Outputs[0].JobId, second, second }, calls);
        Assert.Empty(await _fixture.History.LoadAsync(default));
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_RemovedAudioPathIsReusedByAnotherEntry_LeavesTheNewAudioUntouched()
    {
        await _fixture.InitializeAsync();
        var library = _fixture.CreateLibrary();
        await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);
        var replacement = _fixture.Recording with { Id = Guid.NewGuid() };
        await _fixture.History.AddAsync(replacement, default);
        await File.WriteAllTextAsync(replacement.AudioPath, "Another recording owns this replacement");

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.Equal(replacement.Id, Assert.Single(await _fixture.History.LoadAsync(default)).Id);
        Assert.Equal("Another recording owns this replacement", await File.ReadAllTextAsync(replacement.AudioPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterOutputsAsync_RecordingDeletionIsPendingOrFinished_RejectsLateNewPairsButKeepsKnownOwnership(bool audioDeleted)
    {
        await _fixture.InitializeAsync();
        var current = Assert.Single(await _fixture.History.LoadAsync(default));
        current = current with
        {
            AudioDeleted = audioDeleted,
            PendingDeletion = audioDeleted ? null : new(Guid.NewGuid(), RecordingDeletionScope.AudioOnly,
                [new(RecordingArtifactKind.Audio, current.AudioPath, null, ArtifactDeletionState.Pending, null)], [], null),
        };
        await _fixture.History.UpdateAsync(current, default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        var late = new OwnedTranscriptOutputs(Guid.NewGuid(),
            new(Path.Combine(_fixture.Root, "meeting.transcript-2.txt"), Path.Combine(_fixture.Root, "meeting.transcript-2.srt")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fixture.History.RegisterOutputsAsync(current.Id, late, default));
        Assert.Contains("publication is unavailable", error.Message);
        await _fixture.History.RegisterOutputsAsync(current.Id, _fixture.Outputs[0], default);

        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Equal(_fixture.Outputs, Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs);
    }

    [Fact]
    public async Task DeleteRecordingAsync_OwnershipChangesAfterAudioCheckpoint_StopsBeforeRemovingReferencedOutputs()
    {
        await _fixture.InitializeAsync();
        var changed = false;
        var history = new InterceptingHistory(_fixture.History, async recording =>
        {
            if (!recording.AudioDeleted || changed)
                return;
            changed = true;
            var foreign = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "foreign.wav"),
                "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
            var foreignJob = new TranscriptionJob(Guid.NewGuid(), foreign, DateTimeOffset.UtcNow,
                TranscriptionJobStatus.Succeeded, _fixture.Outputs[0].Outputs, null);
            await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), foreignJob], default);
        });

        var result = await _fixture.CreateLibrary(history: history).DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Contains("ownership changed", result.Issues[0].Message);
        Assert.Contains("shared references", result.Issues[0].Message);
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.TextPath));
        Assert.True(File.Exists(_fixture.Outputs[0].Outputs.SubtitlePath));
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Single(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_PreCancelledRequest_DoesNotCreateIntentOrCallRelease()
    {
        await _fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.CreateLibrary()
            .DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_DifferentScopeIsRequestedDuringPendingDeletion_RequiresOriginalRetry()
    {
        await _fixture.InitializeAsync();
        await _fixture.CreateLibrary(cancel: (_, _) => Task.FromException(new TimeoutException()))
            .DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);
        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.DeletionConflict, Assert.Single(result.Issues).Code);
        Assert.Contains("retry the original deletion scope", result.Issues[0].Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    [Fact]
    public async Task DeleteJobHistoryAsync_QueuedJob_RefusesWithoutCancellingOtherAttempts()
    {
        await _fixture.InitializeAsync();
        var queued = new TranscriptionJob(Guid.NewGuid(), _fixture.Recording);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), queued], default);
        var result = await _fixture.CreateLibrary().DeleteJobHistoryAsync(queued.Id, default);
        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.WorkerReleaseFailed, Assert.Single(result.Issues).Code);
        Assert.Contains("wait for worker release", result.Issues[0].Message);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Equal(2, (await _fixture.Jobs.LoadAsync(default)).Count);
    }

    [Fact]
    public async Task DeleteRecordingAsync_RelatedJobHasForeignAudio_RejectsBeforeCancellation()
    {
        await _fixture.InitializeAsync();
        var foreignSnapshot = _fixture.Recording with { AudioPath = Path.Combine(_fixture.Root, "foreign.wav") };
        await _fixture.Jobs.SaveAsync(
            [new(Guid.NewGuid(), foreignSnapshot, DateTimeOffset.UtcNow, TranscriptionJobStatus.Canceled, null, null)], default);
        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);
        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_WorkerDeadlineCancelsItsOwnToken_ReportsReleaseFailure()
    {
        await _fixture.InitializeAsync();
        LibraryDeletionResult? result = null;
        var library = _fixture.CreateLibrary(cancel: (_, _) =>
            Task.FromException(new OperationCanceledException("Controlled worker deadline, not caller cancellation")));
        var error = await Record.ExceptionAsync(async () => result = await library.DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(LibraryFailureCode.WorkerReleaseFailed, Assert.Single(result.Issues).Code);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Equal(LibraryFailureCode.WorkerReleaseFailed,
            Assert.Single(await _fixture.History.LoadAsync(default)).PendingDeletion!.Failure!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRecordingAsync_InconsistentPendingMetadata_RejectsWithoutCancelling(bool duplicateArtifact)
    {
        await _fixture.InitializeAsync();
        var recording = Assert.Single(await _fixture.History.LoadAsync(default));
        var audio = new ArtifactDeletion(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Pending, null);
        recording = recording with
        {
            PendingDeletion = new(Guid.NewGuid(), RecordingDeletionScope.AudioOnly,
                duplicateArtifact ? [audio, audio with { State = ArtifactDeletionState.Removed }] : [audio],
                duplicateArtifact ? [] : [_fixture.Outputs[0].JobId], null),
        };
        var json = JsonSerializer.Serialize(new[] { recording });
        await File.WriteAllTextAsync(_fixture.HistoryPath, json);

        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AudioOnly, default);

        Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.ReleasedPlayback);
        Assert.True(File.Exists(recording.AudioPath));
        Assert.Equal(json, await File.ReadAllTextAsync(_fixture.HistoryPath));
    }

    [Fact]
    public async Task DeleteRecordingAsync_CallbackReturnsBeforePersistedWorkerRelease_RefusesFileRemoval()
    {
        await _fixture.InitializeAsync();
        await _fixture.Jobs.SaveAsync(
            [new(Guid.NewGuid(), _fixture.Recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Running, null, null)], default);
        var result = await _fixture.CreateLibrary().DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AudioOnly, default);
        Assert.Equal(LibraryDeletionStatus.Incomplete, result.Status);
        Assert.Equal(LibraryFailureCode.WorkerReleaseFailed, Assert.Single(result.Issues).Code);
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Empty(_fixture.ReleasedPlayback);
    }

    [Fact]
    public async Task DeleteRecordingAsync_CompletedAttemptAppearsDuringRelease_IncludesItsFilesAndHistory()
    {
        await _fixture.InitializeAsync();
        var output = new OwnedTranscriptOutputs(Guid.NewGuid(),
            new(Path.Combine(_fixture.Root, "meeting.transcript-2.txt"), Path.Combine(_fixture.Root, "meeting.transcript-2.srt")));
        await File.WriteAllTextAsync(output.Outputs.TextPath, "Already published second attempt");
        await File.WriteAllTextAsync(output.Outputs.SubtitlePath, "Already published second subtitles");
        var library = _fixture.CreateLibrary(release: async (_, token) =>
        {
            await _fixture.Jobs.SaveAsync(
                [.. await _fixture.Jobs.LoadAsync(token),
                    new(output.JobId, _fixture.Recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded, output.Outputs, null)], token);
        });

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Completed, result.Status);
        Assert.False(File.Exists(output.Outputs.TextPath));
        Assert.False(File.Exists(output.Outputs.SubtitlePath));
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
        Assert.Empty(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_EntryAlreadyRemoved_RetryDoesNotInvokeDestructiveCallbacks()
    {
        await _fixture.InitializeAsync();
        var library = _fixture.CreateLibrary();
        await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        _fixture.CancelledRecordings.Clear();
        _fixture.ReleasedPlayback.Clear();
        _fixture.DeletedJobs.Clear();

        var result = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.NotFound, result.Status);
        Assert.Empty(result.Issues);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.ReleasedPlayback);
        Assert.Empty(_fixture.DeletedJobs);
        Assert.Empty(await _fixture.History.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteRecordingAsync_CallerCancelsAfterIntent_PreservesIntentAndAllArtifacts()
    {
        await _fixture.InitializeAsync();
        var before = await _fixture.OutputBytesAsync();
        using var cancellation = new CancellationTokenSource();
        var library = _fixture.CreateLibrary(cancel: (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.DeleteRecordingAsync(
            _fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, cancellation.Token));

        var recording = Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.False(recording.AudioDeleted);
        Assert.NotNull(recording.PendingDeletion);
        Assert.Equal(LibraryFailureCode.DeletionCanceled, recording.PendingDeletion.Failure!.Code);
        Assert.Contains("interrupted", recording.PendingDeletion.Failure.Message);
        Assert.Contains("retry", recording.PendingDeletion.Failure.Message);
        Assert.All(recording.PendingDeletion.Artifacts, artifact => Assert.Equal(ArtifactDeletionState.Pending, artifact.State));
        Assert.True(File.Exists(recording.AudioPath));
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
        Assert.Empty(_fixture.ReleasedPlayback);
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReadTranscriptAsync_ColdLegacyHistory_ImportsTheExactAttemptWithoutASeparateLoad()
    {
        await _fixture.InitializeAsync(2);
        var recording = _fixture.Recording;
        await File.WriteAllTextAsync(_fixture.HistoryPath, JsonSerializer.Serialize(new[]
        {
            new { recording.Id, recording.AudioPath, recording.Language, recording.Outcome, recording.CapturedDuration },
        }));
        var jobsBefore = await File.ReadAllBytesAsync(_fixture.JobsPath);

        var transcript = await _fixture.CreateLibrary().ReadTranscriptAsync(
            recording.Id, _fixture.Outputs[1].JobId, default);

        Assert.Equal("Transcript attempt 2", transcript.Text);
        Assert.Equal("1\r\n00:00:00,000 --> 00:00:04,000\r\nSubtitle attempt 2\r\n", transcript.Subtitles);
        Assert.Empty(transcript.Issues);
        Assert.Equal(_fixture.Outputs, Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs);
        Assert.Equal(jobsBefore, await File.ReadAllBytesAsync(_fixture.JobsPath));
    }

    public void Dispose() => _fixture.Dispose();
}

internal sealed class InterceptingHistory(IRecordingHistory inner, Func<SavedRecording, Task> beforeUpdate) : IRecordingHistory
{
    public Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
    public Task AddAsync(SavedRecording recording, CancellationToken token) => inner.AddAsync(recording, token);
    public Task RemoveAsync(Guid id, CancellationToken token) => inner.RemoveAsync(id, token);
    public Task RegisterOutputsAsync(Guid id, OwnedTranscriptOutputs outputs, CancellationToken token) =>
        inner.RegisterOutputsAsync(id, outputs, token);
    public async Task UpdateAsync(SavedRecording recording, CancellationToken token)
    {
        await beforeUpdate(recording);
        await inner.UpdateAsync(recording, token);
    }
}

internal sealed class ControlledDeletionTranscriber : ILocalTranscriber
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FileReleased { get; private set; }

    public async Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token)
    {
        using (File.Open(recording.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (token.Register(() => CancellationSeen.TrySetResult()))
        {
            Started.TrySetResult();
            await Release.Task;
        }
        FileReleased = true;
        return new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Late cancelled result")]);
    }
}

internal sealed class LibraryDeletionFixture : IDisposable
{
    private readonly StorageTestDirectory _directory = new();
    public string Root => _directory.Root;
    public string HistoryPath => _directory.PathFor("history.json");
    public string JobsPath => _directory.PathFor("jobs.json");
    public JsonRecordingHistory History { get; }
    public JsonTranscriptionJobRepository Jobs { get; }
    public SavedRecording Recording { get; private set; } = null!;
    public List<OwnedTranscriptOutputs> Outputs { get; } = [];
    public List<Guid> CancelledRecordings { get; } = [];
    public List<Guid> ReleasedPlayback { get; } = [];
    public List<Guid> DeletedJobs { get; } = [];
    public StorageTestLogger<RecordingLibrary> Logger { get; } = new();

    public LibraryDeletionFixture()
    {
        History = new JsonRecordingHistory(HistoryPath);
        Jobs = new JsonTranscriptionJobRepository(JobsPath);
    }

    public async Task InitializeAsync(int attempts = 1)
    {
        Recording = new(Guid.NewGuid(), _directory.PathFor("meeting.wav"), "English",
            RecordingOutcome.Complete, TimeSpan.FromSeconds(8));
        using (var wave = new WaveFileWriter(Recording.AudioPath, new WaveFormat(16000, 16, 1)))
        {
            var samples = new byte[16000 * 2 * 8];
            wave.Write(samples, 0, samples.Length);
        }

        await History.AddAsync(Recording, default);
        var jobs = new List<TranscriptionJob>();
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var stem = attempt == 1 ? "meeting" : $"meeting.transcript-{attempt}";
            var pair = new TranscriptOutputs(_directory.PathFor($"{stem}.txt"), _directory.PathFor($"{stem}.srt"));
            await File.WriteAllTextAsync(pair.TextPath, $"Transcript attempt {attempt}");
            await File.WriteAllTextAsync(pair.SubtitlePath, $"1\r\n00:00:00,000 --> 00:00:04,000\r\nSubtitle attempt {attempt}\r\n");
            var output = new OwnedTranscriptOutputs(Guid.NewGuid(), pair);
            Outputs.Add(output);
            await History.RegisterOutputsAsync(Recording.Id, output, default);
            jobs.Add(new(output.JobId, Recording, DateTimeOffset.Parse("2026-11-21T12:00:00Z").AddMinutes(attempt),
                TranscriptionJobStatus.Succeeded, pair, null));
        }
        await Jobs.SaveAsync(jobs, default);
    }

    public RecordingLibrary CreateLibrary(
        Func<Guid, CancellationToken, Task>? cancel = null,
        Func<Guid, CancellationToken, Task>? release = null,
        Func<Guid, CancellationToken, Task>? deleteJob = null,
        IRecordingHistory? history = null) =>
        new(history ?? History, Jobs, Logger, cancel ?? CancelAsync,
            deleteJob ?? DeleteJobAsync, release ?? ReleaseAsync);

    public Task CancelAsync(Guid recordingId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CancelledRecordings.Add(recordingId);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(Guid recordingId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReleasedPlayback.Add(recordingId);
        return Task.CompletedTask;
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken token)
    {
        var jobs = await Jobs.LoadAsync(token);
        await Jobs.SaveAsync(jobs.Where(job => job.Id != jobId).ToArray(), token);
        DeletedJobs.Add(jobId);
    }

    public async Task<Dictionary<string, byte[]>> OutputBytesAsync()
    {
        var bytes = new Dictionary<string, byte[]>();
        foreach (var output in Outputs)
        {
            bytes[output.Outputs.TextPath] = await File.ReadAllBytesAsync(output.Outputs.TextPath);
            bytes[output.Outputs.SubtitlePath] = await File.ReadAllBytesAsync(output.Outputs.SubtitlePath);
        }
        return bytes;
    }

    public void Dispose() => _directory.Dispose();
}
