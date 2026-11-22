using SoundRecorder.Core;
using System.Text.Json;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingResidualDeletionTests : IDisposable
{
    private readonly LibraryDeletionFixture _fixture = new();

    [Fact]
    public async Task DeleteRecordingAsync_FailedCleanupLeavesLockedResidual_RetriesOwnedRemnantsWithoutMakingThemTranscripts()
    {
        await _fixture.InitializeAsync();
        using var writer = new LockPublishedTextWriter();
        var queue = new TranscriptionQueue(new ImmediateTranscriber(), writer, _fixture.Jobs,
            history: new RejectNewPublicationHistory(_fixture.History, _fixture.Outputs[0].JobId));
        await queue.RestoreAsync(default);
        await queue.EnqueueAsync(_fixture.Recording, default);
        await queue.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        var failed = Assert.Single(queue.Jobs, job => job.Status == TranscriptionJobStatus.Failed);
        Assert.NotNull(failed.Outputs);
        Assert.True(File.Exists(failed.Outputs.TextPath));
        Assert.False(File.Exists(failed.Outputs.SubtitlePath));
        Assert.Equal(_fixture.Outputs, Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs);
        var library = _fixture.CreateLibrary(cancel: queue.CancelRecordingAsync, deleteJob: queue.DeleteJobAsync);

        var partial = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.Equal(LibraryDeletionStatus.Incomplete, partial.Status);
        var issue = Assert.Single(partial.Issues);
        Assert.Equal(LibraryFailureCode.ArtifactDeleteFailed, issue.Code);
        Assert.Equal(failed.Outputs.TextPath, issue.Path);
        var saved = Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.Equal(_fixture.Outputs, saved.TranscriptOutputs);
        var residualText = Assert.Single(saved.PendingDeletion!.Artifacts,
            artifact => artifact.JobId == failed.Id && artifact.Kind == RecordingArtifactKind.Text);
        var residualSubtitles = Assert.Single(saved.PendingDeletion.Artifacts,
            artifact => artifact.JobId == failed.Id && artifact.Kind == RecordingArtifactKind.Subtitle);
        Assert.Equal(ArtifactDeletionState.Failed, residualText.State);
        Assert.Equal(ArtifactDeletionState.AlreadyMissing, residualSubtitles.State);
        Assert.Equal(2, (await _fixture.Jobs.LoadAsync(default)).Count);
        Assert.Empty(await library.SearchAsync("Failed residual phrase", default));
        var notTranscript = await library.ReadTranscriptAsync(_fixture.Recording.Id, failed.Id, default);
        Assert.Null(notTranscript.Text);
        Assert.Null(notTranscript.Subtitles);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(notTranscript.Issues).Code);
        writer.Release();
        var afterReload = new RecordingLibrary(_fixture.History, _fixture.Jobs);
        Assert.NotNull(Assert.Single(await afterReload.LoadAsync(default)).Recording.PendingDeletion);
        Assert.True(File.Exists(failed.Outputs.TextPath));

        var complete = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.True(complete.Status == LibraryDeletionStatus.Completed,
            JsonSerializer.Serialize(complete) + Environment.NewLine + string.Join(Environment.NewLine, _fixture.Logger.Entries.Select(entry => entry.Message)));
        Assert.Empty(complete.Issues);
        Assert.False(File.Exists(failed.Outputs.TextPath));
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        Assert.Empty(await _fixture.History.LoadAsync(default));
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
        var restored = new TranscriptionQueue(new ImmediateTranscriber(), new AtomicTranscriptWriter(), _fixture.Jobs,
            history: _fixture.History);
        await restored.RestoreAsync(default);
        Assert.Empty(restored.Jobs);
    }

    [Fact]
    public async Task DeleteRecordingAsync_OneResidualJobHistoryWasRemoved_RecoversItsDurableArtifactProof()
    {
        await _fixture.InitializeAsync();
        var residualJobs = new List<TranscriptionJob>();
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            var pair = new TranscriptOutputs(Path.Combine(_fixture.Root, $"meeting.transcript-{attempt}.txt"),
                Path.Combine(_fixture.Root, $"meeting.transcript-{attempt}.srt"));
            await File.WriteAllTextAsync(pair.TextPath, "Failed residual phrase");
            await File.WriteAllTextAsync(pair.SubtitlePath, "Failed residual subtitle");
            residualJobs.Add(new(Guid.NewGuid(), _fixture.Recording, DateTimeOffset.UtcNow,
                TranscriptionJobStatus.Failed, pair, "Cleanup failed"));
        }
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), .. residualJobs], default);
        var queue = new TranscriptionQueue(new ImmediateTranscriber(), new AtomicTranscriptWriter(), _fixture.Jobs,
            history: _fixture.History);
        await queue.RestoreAsync(default);
        var rejectLast = true;
        var library = _fixture.CreateLibrary(cancel: queue.CancelRecordingAsync, deleteJob: async (id, token) =>
        {
            if (id == residualJobs[1].Id && rejectLast)
                throw new IOException("Controlled remaining job-history failure");
            await queue.DeleteJobAsync(id, token);
        });
        var incomplete = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.Equal(LibraryDeletionStatus.Incomplete, incomplete.Status);
        Assert.Equal(residualJobs[1].Id, Assert.Single(incomplete.PendingDeletion!.RemainingJobIds));
        Assert.Equal(residualJobs[1].Id, Assert.Single(await _fixture.Jobs.LoadAsync(default)).Id);
        Assert.Equal(_fixture.Outputs, Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs);
        rejectLast = false;

        var complete = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);

        Assert.True(complete.Status == LibraryDeletionStatus.Completed,
            JsonSerializer.Serialize(complete) + Environment.NewLine + string.Join(Environment.NewLine, _fixture.Logger.Entries.Select(entry => entry.Message)));
        Assert.Empty(await _fixture.History.LoadAsync(default));
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
    }

    [Fact]
    public async Task ReadTranscriptAsync_StopAfterOwnershipRegistration_FailedResidualIsNotCompletedReview()
    {
        await _fixture.InitializeAsync();
        using var writer = new LockPublishedTextWriter();
        TranscriptionQueue? queue = null;
        var history = new RegisterThenStopHistory(_fixture.History, _fixture.Outputs[0].JobId,
            output => queue!.StopAsync(output.JobId, default));
        queue = new(new ImmediateTranscriber(), writer, _fixture.Jobs, history: history);
        await queue.RestoreAsync(default);
        await queue.EnqueueAsync(_fixture.Recording, default);
        await queue.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        var failed = Assert.Single(queue.Jobs, job => job.Status == TranscriptionJobStatus.Failed);
        Assert.NotNull(failed.Outputs);
        Assert.Contains(Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs,
            output => output.JobId == failed.Id);
        var library = _fixture.CreateLibrary(cancel: queue.CancelRecordingAsync, deleteJob: queue.DeleteJobAsync);

        var transcript = await library.ReadTranscriptAsync(_fixture.Recording.Id, failed.Id, default);

        Assert.Null(transcript.Text);
        Assert.Null(transcript.Subtitles);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(transcript.Issues).Code);
        Assert.Empty(await library.SearchAsync("Failed residual phrase", default));
        Assert.DoesNotContain(Assert.Single(await library.LoadAsync(default)).Recording.TranscriptOutputs,
            output => output.JobId == failed.Id);
        Assert.Contains(Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs,
            output => output.JobId == failed.Id);
        writer.Release();
        var complete = await library.DeleteRecordingAsync(_fixture.Recording.Id, RecordingDeletionScope.AllArtifacts, default);
        Assert.True(complete.Status == LibraryDeletionStatus.Completed,
            JsonSerializer.Serialize(complete) + Environment.NewLine + string.Join(Environment.NewLine, _fixture.Logger.Entries.Select(entry => entry.Message)));
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
    }

    private sealed class RegisterThenStopHistory(
        IRecordingHistory inner, Guid completedJobId, Func<OwnedTranscriptOutputs, Task> stop) : IRecordingHistory
    {
        public Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public Task AddAsync(SavedRecording recording, CancellationToken token) => inner.AddAsync(recording, token);
        public Task UpdateAsync(SavedRecording recording, CancellationToken token) => inner.UpdateAsync(recording, token);
        public Task RemoveAsync(Guid id, CancellationToken token) => inner.RemoveAsync(id, token);
        public async Task RegisterOutputsAsync(Guid id, OwnedTranscriptOutputs output, CancellationToken token)
        {
            await inner.RegisterOutputsAsync(id, output, token);
            if (output.JobId != completedJobId)
                await stop(output);
        }
    }

    private sealed class ImmediateTranscriber : ILocalTranscriber
    {
        public Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token) =>
            Task.FromResult(new TranscriptDocument(recording, recording.Language,
                [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Failed residual phrase")]));
    }

    private sealed class LockPublishedTextWriter : ITranscriptWriter, IDisposable
    {
        private FileStream? _held;
        public async Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken token)
        {
            var outputs = await new AtomicTranscriptWriter().WriteAsync(document, token);
            _held = File.Open(outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return outputs;
        }
        public void Release() { _held?.Dispose(); _held = null; }
        public void Dispose() => Release();
    }

    private sealed class RejectNewPublicationHistory(IRecordingHistory inner, Guid completedJobId) : IRecordingHistory
    {
        public Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public Task AddAsync(SavedRecording recording, CancellationToken token) => inner.AddAsync(recording, token);
        public Task UpdateAsync(SavedRecording recording, CancellationToken token) => inner.UpdateAsync(recording, token);
        public Task RemoveAsync(Guid id, CancellationToken token) => inner.RemoveAsync(id, token);
        public Task RegisterOutputsAsync(Guid id, OwnedTranscriptOutputs outputs, CancellationToken token) =>
            outputs.JobId == completedJobId ? inner.RegisterOutputsAsync(id, outputs, token) :
                Task.FromException(new IOException("Controlled publication-registration failure"));
    }

    public void Dispose() => _fixture.Dispose();
}
