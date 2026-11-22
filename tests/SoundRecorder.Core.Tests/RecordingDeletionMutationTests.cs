using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingDeletionMutationTests : IDisposable
{
    private readonly LibraryDeletionFixture _fixture = new();

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
