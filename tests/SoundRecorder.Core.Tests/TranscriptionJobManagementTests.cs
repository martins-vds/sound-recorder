using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;
using System.Collections.Concurrent;

namespace SoundRecorder.Core.Tests;

public sealed class TranscriptionJobManagementTests
{
    [Fact]
    public async Task Failed_residual_outputs_can_leave_job_history_after_durable_full_deletion_without_becoming_a_transcript()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"residual-deletion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var audio = Path.Combine(directory, "owned.wav");
        using (var wave = new NAudio.Wave.WaveFileWriter(audio, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
            wave.Write(new byte[96_000], 0, 96_000);
        var recording = Recording() with { AudioPath = audio };
        var historyPath = Path.Combine(directory, "history.json");
        var jobsPath = Path.Combine(directory, "jobs.json");
        var history = new JsonRecordingHistory(historyPath);
        await history.AddAsync(recording, default);
        var repository = new JsonTranscriptionJobRepository(jobsPath);
        using var writer = new LockedOutputWriter(directory);
        var worker = new ControlledTranscriber();
        worker.Release.TrySetResult();
        var queue = new TranscriptionQueue(worker, writer, repository, models: new Models(), history: history);
        try
        {
            await queue.EnqueueAsync(recording, default);
            await writer.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var job = Assert.Single(queue.Jobs);
            await queue.StopAsync(job.Id, default);
            writer.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);
            Assert.Equal(TranscriptionJobStatus.Failed, job.Status);
            Assert.Equal(writer.Outputs, job.Outputs);
            Assert.Empty(Assert.Single(await history.LoadAsync(default)).TranscriptOutputs);

            File.Delete(audio);
            var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
            [
                new(RecordingArtifactKind.Audio, audio, null, ArtifactDeletionState.Removed, null),
                new(RecordingArtifactKind.Text, writer.Outputs.TextPath, job.Id, ArtifactDeletionState.Failed,
                    new(LibraryFailureCode.ArtifactDeleteFailed, "Output is locked.", writer.Outputs.TextPath, job.Id)),
                new(RecordingArtifactKind.Subtitle, writer.Outputs.SubtitlePath, job.Id, ArtifactDeletionState.AlreadyMissing, null),
            ], [job.Id], null);
            await history.UpdateAsync(recording with { AudioDeleted = true, PendingDeletion = pending }, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DeleteJobAsync(job.Id, default));
            Assert.Contains(queue.Jobs, item => item.Id == job.Id);

            writer.Dispose();
            File.Delete(writer.Outputs.TextPath);
            pending = pending with
            {
                Artifacts = pending.Artifacts.Select(artifact => artifact.Kind == RecordingArtifactKind.Text
                    ? artifact with { State = ArtifactDeletionState.Removed, Failure = null } : artifact).ToArray(),
            };
            await history.UpdateAsync(recording with { AudioDeleted = true, PendingDeletion = pending }, default);

            await queue.DeleteJobAsync(job.Id, default);

            Assert.Empty(queue.Jobs);
            Assert.Empty(Assert.Single(await new JsonRecordingHistory(historyPath).LoadAsync(default)).TranscriptOutputs);
            var restarted = new TranscriptionQueue(new ControlledTranscriber(), new Writer(),
                new JsonTranscriptionJobRepository(jobsPath), history: new JsonRecordingHistory(historyPath));
            await restarted.RestoreAsync(default);
            await restarted.WaitForIdleAsync(default);
            Assert.Empty(restarted.Jobs);
        }
        finally { writer.Release.TrySetResult(); writer.Dispose(); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("audio-only")]
    [InlineData("other-job")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-path")]
    [InlineData("wrong-kind")]
    [InlineData("missing-text")]
    [InlineData("missing-subtitle")]
    [InlineData("pending-text")]
    [InlineData("failed-subtitle")]
    [InlineData("missing-recording")]
    public async Task Failed_residual_history_cannot_be_removed_or_promoted_with_incomplete_or_unrelated_deletion_proof(string invalidProof)
    {
        var id = Guid.NewGuid();
        var outputs = new TranscriptOutputs("residual.txt", "residual.srt");
        var recording = Recording();
        var artifacts = new List<ArtifactDeletion>
        {
            new(RecordingArtifactKind.Text, outputs.TextPath, id, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Subtitle, outputs.SubtitlePath, id, ArtifactDeletionState.AlreadyMissing, null),
        };
        if (invalidProof == "wrong-owner") artifacts[0] = artifacts[0] with { JobId = Guid.NewGuid() };
        if (invalidProof == "wrong-path") artifacts[0] = artifacts[0] with { Path = "unrelated.txt" };
        if (invalidProof == "wrong-kind") artifacts[0] = artifacts[0] with { Kind = RecordingArtifactKind.Audio };
        if (invalidProof == "missing-text") artifacts.RemoveAt(0);
        if (invalidProof == "missing-subtitle") artifacts.RemoveAt(1);
        if (invalidProof == "pending-text") artifacts[0] = artifacts[0] with { State = ArtifactDeletionState.Pending };
        if (invalidProof == "failed-subtitle") artifacts[1] = artifacts[1] with { State = ArtifactDeletionState.Failed };
        var pending = new PendingRecordingDeletion(Guid.NewGuid(),
            invalidProof == "audio-only" ? RecordingDeletionScope.AudioOnly : RecordingDeletionScope.AllArtifacts,
            artifacts, [invalidProof == "other-job" ? Guid.NewGuid() : id], null);
        var history = new History { Recording = recording with { PendingDeletion = invalidProof == "none" ? null : pending } };
        if (invalidProof == "missing-recording") history.Recording = history.Recording with { Id = Guid.NewGuid() };
        var job = new TranscriptionJob(id, recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Failed, outputs, "Cleanup failed.");
        var queue = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), new Repository { Stored = [job] }, history: history);
        await queue.RestoreAsync(default);
        await queue.WaitForIdleAsync(default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DeleteJobAsync(id, default));

        Assert.Contains("Residual output artifacts", error.Message);
        Assert.Contains(queue.Jobs, item => item.Id == id);
        Assert.Empty(history.Recording.TranscriptOutputs);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("mixed", true)]
    [InlineData("none", false)]
    [InlineData("other-job", false)]
    [InlineData("other-pair", false)]
    public async Task History_deletion_without_a_store_requires_this_attempts_independent_output_ownership(
        string ownership, bool canDelete)
    {
        var id = Guid.NewGuid();
        var pair = new TranscriptOutputs("retained.txt", "retained.srt");
        OwnedTranscriptOutputs[] owned = ownership switch
        {
            "matching" => [new(id, pair)],
            "mixed" => [new(id, pair), new(Guid.NewGuid(), new("another.txt", "another.srt"))],
            "other-job" => [new(Guid.NewGuid(), pair)],
            "other-pair" => [new(id, new("another.txt", "another.srt"))],
            _ => [],
        };
        var recording = Recording() with { TranscriptOutputs = owned };
        var job = new TranscriptionJob(id, recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded, pair, null);
        var repository = new Repository { Stored = [job] };
        var queue = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository);
        await queue.RestoreAsync(default);
        await queue.WaitForIdleAsync(default);

        if (canDelete)
        {
            await queue.DeleteJobAsync(id, default);
            await queue.DeleteJobAsync(id, default);
            await queue.DeleteJobAsync(Guid.NewGuid(), default);
            Assert.Empty(queue.Jobs);
            Assert.Equal(owned, recording.TranscriptOutputs);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DeleteJobAsync(id, default));
            Assert.Contains("Register the output pair independently", error.Message);
            Assert.Equal(id, Assert.Single(queue.Jobs).Id);
        }
    }

    [Fact]
    public async Task Job_refresh_observers_receive_queued_retry_progress_and_termination_even_if_another_observer_throws()
    {
        var worker = new ControlledTranscriber { IgnoreCancellation = true };
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository());
        var seen = new ConcurrentQueue<JobObservation>();
        queue.JobChanged += (_, _) => throw new InvalidOperationException("Observer failure must not stop inference.");
        queue.JobChanged += (_, job) => seen.Enqueue(new(job.Id, job.Status, job.IsTerminationPending, job.Progress));
        try
        {
            await queue.EnqueueAsync(Recording(), default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = queue.Jobs[0];
            await queue.EnqueueAsync(Recording(), default);
            var queued = queue.Jobs[1];
            var retry = await queue.RetryAsync(Recording(), default);
            await WaitUntilAsync(() => seen.Any(value => value.Id == queued.Id && value.Status == TranscriptionJobStatus.Queued) &&
                seen.Any(value => value.Id == retry.Id && value.Status == TranscriptionJobStatus.Queued));
            worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, 37));
            await WaitUntilAsync(() => seen.Any(value => value.Id == running.Id && value.Progress?.Percent == 37));
            await queue.CancelAsync(running.Id, default);
            await WaitUntilAsync(() => seen.Any(value => value.Id == running.Id && value.Pending));
            worker.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);
            await WaitUntilAsync(() => seen.Any(value => value.Id == running.Id && value.Status == TranscriptionJobStatus.Canceled && !value.Pending));
            Assert.All(queue.Jobs.Skip(1), job => Assert.Equal(TranscriptionJobStatus.Succeeded, job.Status));
        }
        finally
        {
            worker.Release.TrySetResult();
            foreach (var job in queue.Jobs) await queue.CancelAsync(job.Id, default);
            await queue.WaitForIdleAsync(default);
        }
    }

    private sealed record JobObservation(Guid Id, TranscriptionJobStatus Status, bool Pending, TranscriptionProgress? Progress);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_rejects_queued_and_already_canceled_work_without_changing_its_state(bool cancelFirst)
    {
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository());
        try
        {
            await queue.EnqueueAsync(Recording(), default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.EnqueueAsync(Recording(), default);
            var selected = queue.Jobs[1];
            if (cancelFirst) await queue.CancelAsync(selected.Id, default);
            var expectedStatus = selected.Status;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync(selected.Id, default));
            Assert.Contains("Only a running job can be stopped", error.Message);
            Assert.Equal(expectedStatus, selected.Status);
            Assert.Null(selected.InterruptionReason);
        }
        finally
        {
            foreach (var job in queue.Jobs) await queue.CancelAsync(job.Id, default);
            worker.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);
        }
    }

    [Fact]
    public async Task An_unregistered_recording_is_rejected_before_any_job_can_enter_the_queue()
    {
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository(), history: new History());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Recording(), default));
        Assert.Contains("Register the recording in history before enqueueing", error.Message);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recording_cancellation_during_enqueue_validation_blocks_both_enqueue_and_retry(bool retry)
    {
        var recording = Recording();
        var history = new GatedHistory(recording);
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository(), history: history);
        var request = retry ? queue.RetryAsync(recording, default) : queue.EnqueueAsync(recording, default);
        await history.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.CancelRecordingAsync(recording.Id, default);
        history.Release.TrySetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => request);

        Assert.Contains("deletion or worker termination is pending", error.Message);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, worker.Calls);
    }

    private sealed class GatedHistory(SavedRecording recording) : IRecordingHistory
    {
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token)
        {
            LoadEntered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return [recording];
        }
        public Task AddAsync(SavedRecording entry, CancellationToken token) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_checks_current_recording_availability_before_starting_a_worker(bool audioDeleted)
    {
        var recording = Recording();
        var queued = new TranscriptionJob(Guid.NewGuid(), recording);
        var history = new History
        {
            Recording = audioDeleted ? recording with { AudioDeleted = true } : Recording(),
        };
        var worker = new ControlledTranscriber();
        worker.Release.TrySetResult();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository { Stored = [queued] }, history: history);

        await queue.RestoreAsync(default);
        await queue.WaitForIdleAsync(default);

        Assert.Equal(TranscriptionJobStatus.Failed, queued.Status);
        Assert.Contains(audioDeleted ? "audio is deleted" : "Register the recording", queued.Error);
        Assert.Equal(0, worker.Calls);
        Assert.Null(queued.Outputs);
    }

    [Fact]
    public async Task Legacy_queued_jobs_require_explicit_resolution_and_do_not_invent_a_selected_model()
    {
        var queued = new TranscriptionJob(Guid.NewGuid(), Recording());
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository { Stored = [queued] }, models: new Models());

        await queue.RestoreAsync(default);
        await queue.WaitForIdleAsync(default);

        Assert.Equal(TranscriptionJobStatus.Failed, queued.Status);
        Assert.Contains("'unknown'", queued.Error);
        Assert.Contains("Select a downloaded model and request a new attempt", queued.Error);
        Assert.Null(queued.ModelId);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Partial_output_cleanup_failure_retains_recoverable_ownership_and_an_actionable_error(bool stop, bool lockBoth)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"locked-outputs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recording = Recording() with { AudioPath = Path.Combine(directory, "saved.wav") };
        await File.WriteAllTextAsync(recording.AudioPath, "unchanged saved audio");
        using var writer = new LockedOutputWriter(directory, lockBoth);
        var worker = new ControlledTranscriber();
        worker.Release.TrySetResult();
        var history = new History { Recording = recording };
        if (!stop) history.RegisterFailure = new IOException("Ownership storage failed.");
        var queue = new TranscriptionQueue(worker, writer, new Repository(), history: history);
        try
        {
            await queue.EnqueueAsync(recording, default);
            await writer.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop) await queue.StopAsync(queue.Jobs[0].Id, default);
            writer.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);

            var job = Assert.Single(queue.Jobs);
            Assert.Equal(TranscriptionJobStatus.Failed, job.Status);
            Assert.Equal(writer.Outputs, job.Outputs);
            Assert.Contains("Could not remove this attempt's output", job.Error);
            Assert.Contains(writer.Outputs.TextPath, job.Error);
            if (!stop) Assert.Contains("Ownership storage failed", job.Error);
            Assert.True(File.Exists(writer.Outputs.TextPath));
            Assert.Equal(lockBoth, File.Exists(writer.Outputs.SubtitlePath));
            if (lockBoth) Assert.Contains(" Could not remove this attempt's output", job.Error);
            Assert.Equal("unchanged saved audio", await File.ReadAllTextAsync(recording.AudioPath));
            Assert.False(job.IsTerminationPending);
        }
        finally
        {
            writer.Release.TrySetResult();
            writer.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private sealed class LockedOutputWriter(string directory, bool lockBoth = false) : ITranscriptWriter, IDisposable
    {
        private FileStream? _locked;
        private FileStream? _lockedSubtitle;
        public TranscriptOutputs Outputs { get; } = new(Path.Combine(directory, "owned.txt"), Path.Combine(directory, "owned.srt"));
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken token)
        {
            await File.WriteAllTextAsync(Outputs.TextPath, "text");
            await File.WriteAllTextAsync(Outputs.SubtitlePath, "subtitles");
            _locked = new FileStream(Outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.None);
            if (lockBoth) _lockedSubtitle = new FileStream(Outputs.SubtitlePath, FileMode.Open, FileAccess.Read, FileShare.None);
            Ready.TrySetResult();
            await Release.Task;
            return Outputs;
        }
        public void Dispose() { _locked?.Dispose(); _lockedSubtitle?.Dispose(); }
    }

    [Fact]
    public async Task Queued_cancellation_is_durable_before_the_call_returns()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"durable-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jobs.json");
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new JsonTranscriptionJobRepository(path));
        TranscriptionQueue? restored = null;
        try
        {
            await queue.EnqueueAsync(Recording(), default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.EnqueueAsync(Recording(), default);
            var queued = queue.Jobs[1];
            await queue.CancelAsync(queued.Id, default);

            restored = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), new JsonTranscriptionJobRepository(path));
            await restored.RestoreAsync(default);
            Assert.Equal(TranscriptionJobStatus.Canceled, restored.Jobs.Single(job => job.Id == queued.Id).Status);
        }
        finally
        {
            foreach (var job in queue.Jobs) await queue.CancelAsync(job.Id, default);
            worker.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);
            if (restored is not null)
            {
                foreach (var job in restored.Jobs) await restored.CancelAsync(job.Id, default);
                await restored.WaitForIdleAsync(default);
            }
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Duplicate_concurrent_history_deletion_is_idempotent_even_if_a_redundant_write_would_fail()
    {
        var job = new TranscriptionJob(Guid.NewGuid(), Recording(), DateTimeOffset.UtcNow, TranscriptionJobStatus.Canceled, null, null);
        var repository = new ConcurrentDeletionRepository(job);
        var queue = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository);
        await queue.RestoreAsync(default);
        await queue.WaitForIdleAsync(default);
        var first = queue.DeleteJobAsync(job.Id, default);
        await repository.Deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.DeleteJobAsync(job.Id, default);
        repository.Release.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Empty(queue.Jobs);
    }

    private sealed class ConcurrentDeletionRepository(TranscriptionJob job) : ITranscriptionJobRepository
    {
        private IReadOnlyList<TranscriptionJob> _stored = [job];
        private bool _committed;
        public TaskCompletionSource Deleting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken token) => Task.FromResult(_stored);
        public async Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken token)
        {
            if (_committed) throw new IOException("Storage failed after the first durable deletion.");
            if (jobs.All(item => item.Id != job.Id))
            {
                Deleting.TrySetResult();
                await Release.Task.WaitAsync(token);
                _committed = true;
            }
            _stored = jobs.ToArray();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_cancellation_handle_is_released_when_the_worker_attempt_finishes(bool callbackFails)
    {
        using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ControlledTranscriber
        {
            CaptureCancellationHandle = true,
            CancellationCallback = () =>
            {
                callbackEntered.TrySetResult();
                callbackRelease.Wait(TimeSpan.FromSeconds(5));
                if (callbackFails) throw new InvalidOperationException("Cancellation callback failed.");
            },
        };
        var logger = new RecordingLogger<TranscriptionQueue>();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository(), logger);
        try
        {
            await queue.EnqueueAsync(Recording(), default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var handle = Assert.IsAssignableFrom<WaitHandle>(worker.CancellationHandle);
            Assert.False(handle.SafeWaitHandle.IsClosed);
            var cancellation = queue.CancelAsync(queue.Jobs[0].Id, default);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await worker.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var idle = queue.WaitForIdleAsync(default);
            Assert.NotSame(idle, await Task.WhenAny(idle, Task.Delay(100)));
            Assert.True(queue.Jobs[0].IsTerminationPending);
            callbackRelease.Set();
            if (callbackFails) await Assert.ThrowsAsync<AggregateException>(() => cancellation);
            else await cancellation;
            await idle;
            Assert.True(handle.SafeWaitHandle.IsClosed);
            Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[0].Status);
            if (callbackFails)
            {
                worker.CancellationCallback = null;
                worker.CaptureCancellationHandle = false;
                worker.Release.TrySetResult();
                await queue.EnqueueAsync(Recording(), default);
                await queue.WaitForIdleAsync(default);
                Assert.Equal(TranscriptionJobStatus.Succeeded, queue.Jobs.Last().Status);
                Assert.Equal(1, logger.EventIds.Count(id => id == 903));
            }
        }
        finally { callbackRelease.Set(); worker.Release.TrySetResult(); worker.CancellationHandle?.Dispose(); }
    }

    [Fact]
    public async Task Recording_cancellation_blocks_publication_even_while_an_older_terminal_history_write_is_delayed()
    {
        var recording = Recording();
        var terminal = new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded, null, null);
        var repository = new DelayedRepository { Stored = [terminal] };
        var worker = new ControlledTranscriber();
        var writer = new Writer();
        var queue = new TranscriptionQueue(worker, writer, repository);
        await queue.RestoreAsync(default);
        await queue.EnqueueAsync(recording, default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = queue.Jobs.Last();
        repository.Block = true;
        var cancel = queue.CancelRecordingAsync(recording.Id, default);
        try
        {
            await repository.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Release.TrySetResult();
            await WaitUntilAsync(() => running.Status != TranscriptionJobStatus.Running);
            Assert.Equal(0, writer.Calls);
            Assert.Contains("deletion or worker termination is pending", running.Error);
            Assert.Null(running.Outputs);
        }
        finally
        {
            repository.Release.TrySetResult();
            worker.Release.TrySetResult();
            await cancel;
            await queue.WaitForIdleAsync(default);
        }
    }

    [Fact]
    public async Task Legacy_queue_contracts_report_unsupported_deletion_instead_of_silent_success()
    {
        ITranscriptionQueue queue = new EnqueueHook(_ => Task.CompletedTask);
        var cancel = await Assert.ThrowsAsync<NotSupportedException>(() => queue.CancelRecordingAsync(Guid.NewGuid(), default));
        var delete = await Assert.ThrowsAsync<NotSupportedException>(() => queue.DeleteJobAsync(Guid.NewGuid(), default));
        Assert.Contains("does not support recording cancellation", cancel.Message);
        Assert.Contains("does not support job-history deletion", delete.Message);
    }

    [Fact]
    public async Task Stop_during_independent_output_registration_cannot_publish_success_or_leave_new_files()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"register-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = new History
        {
            Registration = async _ => { entered.TrySetResult(); await finish.Task; },
        };
        var writer = new LateWriter(directory);
        writer.Release.TrySetResult();
        var worker = new ControlledTranscriber();
        worker.Release.TrySetResult();
        var queue = new TranscriptionQueue(worker, writer, new Repository(), history: history);
        try
        {
            await queue.EnqueueAsync(history.Recording, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var job = Assert.Single(queue.Jobs);
            await queue.StopAsync(job.Id, default);
            finish.TrySetResult();
            await queue.WaitForIdleAsync(default);

            Assert.Equal(TranscriptionJobStatus.Interrupted, job.Status);
            Assert.Null(job.Outputs);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { finish.TrySetResult(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Audio_deletion_intent_during_output_writing_discards_the_attempt_before_ownership_registration()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"writing-deletion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var history = new History();
        var writer = new LateWriter(directory);
        var worker = new ControlledTranscriber();
        worker.Release.TrySetResult();
        var queue = new TranscriptionQueue(worker, writer, new Repository(), history: history);
        try
        {
            await queue.EnqueueAsync(history.Recording, default);
            await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            history.Recording = history.Recording with { AudioDeleted = true };
            writer.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);

            var job = Assert.Single(queue.Jobs);
            Assert.Equal(TranscriptionJobStatus.Failed, job.Status);
            Assert.Contains("audio is deleted", job.Error);
            Assert.Null(job.Outputs);
            Assert.Empty(history.Recording.TranscriptOutputs);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { writer.Release.TrySetResult(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Cancellation_persistence_failure_is_logged_and_final_release_still_persists_a_retryable_attempt()
    {
        var worker = new ControlledTranscriber { IgnoreCancellation = true };
        var repository = new TransientFailureRepository();
        var logger = new RecordingLogger<TranscriptionQueue>();
        var queue = new TranscriptionQueue(worker, new Writer(), repository, logger);
        try
        {
            await queue.EnqueueAsync(Recording(), default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            repository.FailNextSave = true;
            var job = queue.Jobs[0];
            await queue.CancelAsync(job.Id, default);
            await WaitUntilAsync(() => logger.EventIds.Contains(903));
            Assert.True(job.IsTerminationPending);
            worker.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);
            var restored = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository);
            await restored.RestoreAsync(default);
            await restored.WaitForIdleAsync(default);
            Assert.Equal(TranscriptionJobStatus.Canceled, Assert.Single(restored.Jobs).Status);
            Assert.Null(job.Outputs);
        }
        finally
        {
            worker.Release.TrySetResult();
            foreach (var job in queue.Jobs) await queue.CancelAsync(job.Id, default);
            await queue.WaitForIdleAsync(default);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_history_deletion_remains_retryable_and_success_survives_queue_restart(bool retryAfterRestart)
    {
        var id = Guid.NewGuid();
        var outputs = new TranscriptOutputs("earlier.txt", "earlier.srt");
        var recording = Recording() with { TranscriptOutputs = [new(id, outputs)] };
        var completed = new TranscriptionJob(id, recording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded, outputs, null, "base-original");
        var unrelated = new TranscriptionJob(Guid.NewGuid(), Recording(), DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Canceled, null, null);
        var history = new History { Recording = recording };
        var repository = new TransientFailureRepository { Stored = [completed, unrelated] };
        var queue = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository, history: history);
        await queue.RestoreAsync(default);
        await queue.WaitForIdleAsync(default);
        repository.FailNextSave = true;

        await Assert.ThrowsAsync<IOException>(() => queue.DeleteJobAsync(id, default));

        Assert.Contains(queue.Jobs, job => job.Id == id);
        var afterFailure = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository, history: history);
        await afterFailure.RestoreAsync(default);
        await afterFailure.WaitForIdleAsync(default);
        Assert.Contains(afterFailure.Jobs, job => job.Id == id);
        Assert.Equal(outputs, Assert.Single(history.Recording.TranscriptOutputs).Outputs);

        var retryQueue = retryAfterRestart ? afterFailure : queue;
        await retryQueue.DeleteJobAsync(id, default);

        Assert.DoesNotContain(retryQueue.Jobs, job => job.Id == id);
        var afterSuccess = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository, history: history);
        await afterSuccess.RestoreAsync(default);
        await afterSuccess.WaitForIdleAsync(default);
        Assert.Equal(unrelated.Id, Assert.Single(afterSuccess.Jobs).Id);
        Assert.Equal(outputs, Assert.Single(history.Recording.TranscriptOutputs).Outputs);
    }

    private sealed class TransientFailureRepository : ITranscriptionJobRepository
    {
        public IReadOnlyList<TranscriptionJob> Stored { get; set; } = [];
        public bool FailNextSave { get; set; }
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken token) => Task.FromResult(Stored);
        public Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken token)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Transient job-history save failure.");
            }
            Stored = jobs.ToArray();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Recorder_persists_history_before_the_public_queue_enqueue_boundary()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"enqueue-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "history.json");
            var recording = Recording();
            var observed = false;
            var queue = new EnqueueHook(async saved =>
            {
                var persisted = await new JsonRecordingHistory(path).LoadAsync(default);
                Assert.Contains(persisted, item => item.Id == saved.Id && item.AudioPath == saved.AudioPath);
                observed = true;
            });
            var application = new RecorderApplication(
                new RecordingEngine(recording), queue, new Models(), new JsonRecordingHistory(path));

            await application.AcceptCompletedRecordingAsync(recording);

            Assert.True(observed);
            Assert.Null(application.TranscriptionNotice);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class EnqueueHook(Func<SavedRecording, Task> enqueue) : ITranscriptionQueue
    {
        public Task EnqueueAsync(SavedRecording recording, CancellationToken token) => enqueue(recording);
    }

    private sealed class RecordingEngine(SavedRecording recording) : IRecordingEngine
    {
        public string? LastFailure => null;
        public Task StartAsync(RecordingRequest request, CancellationToken token) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken token) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<SavedRecording> StopAsync(CancellationToken token) => Task.FromResult(recording);
    }

    [Fact]
    public async Task A_backlogged_observer_does_not_receive_recognition_after_the_attempt_is_terminated()
    {
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository());
        using var unblock = new ManualResetEventSlim();
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.ProgressChanged += (_, value) =>
        {
            if (value.Progress.Phase == TranscriptionPhase.Preparing)
            {
                observing.TrySetResult();
                unblock.Wait(TimeSpan.FromSeconds(5));
            }
            if (value.Progress.Phase == TranscriptionPhase.Recognizing) stale.TrySetResult();
        };
        try
        {
            await queue.EnqueueAsync(Recording(), default);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await observing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, 37));
            await queue.StopAsync(queue.Jobs[0].Id, default);
            await queue.WaitForIdleAsync(default);
            unblock.Set();
            await Task.WhenAny(stale.Task, Task.Delay(100));
            Assert.False(stale.Task.IsCompleted);
        }
        finally { unblock.Set(); worker.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Stop_acknowledgment_does_not_wait_for_an_unrelated_slow_history_write()
    {
        var repository = new DelayedRepository();
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), repository);
        await queue.EnqueueAsync(Recording(), default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.Block = true;
        var enqueue = queue.EnqueueAsync(Recording(), default);
        await repository.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = queue.StopAsync(queue.Jobs[0].Id, default);
        try
        {
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(1))));
            await stop;
        }
        finally
        {
            repository.Release.TrySetResult();
            worker.Release.TrySetResult();
            await Task.WhenAll(enqueue, stop);
            await queue.WaitForIdleAsync(default);
        }
    }

    [Fact]
    public async Task History_deletion_waits_for_prior_queue_persistence_so_an_old_snapshot_cannot_resurrect_it()
    {
        var terminal = new TranscriptionJob(Guid.NewGuid(), Recording(), DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Canceled, null, null);
        var repository = new DelayedRepository { Stored = [terminal] };
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), repository);
        await queue.RestoreAsync(default);
        repository.Block = true;
        var enqueue = queue.EnqueueAsync(Recording(), default);
        await repository.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deletion = queue.DeleteJobAsync(terminal.Id, default);
        await Task.Delay(50);
        try { Assert.False(deletion.IsCompleted); }
        finally { repository.Release.TrySetResult(); worker.Release.TrySetResult(); }
        await Task.WhenAll(enqueue, deletion);
        await queue.WaitForIdleAsync(default);
        Assert.DoesNotContain(repository.Stored, job => job.Id == terminal.Id);
    }

    private sealed class DelayedRepository : ITranscriptionJobRepository
    {
        public IReadOnlyList<TranscriptionJob> Stored { get; set; } = [];
        public bool Block { get; set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken token) => Task.FromResult(Stored);
        public async Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken token)
        {
            if (Block)
            {
                Block = false;
                Blocked.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            Stored = jobs;
        }
    }

    [Fact]
    public async Task Recording_cancellation_awaits_related_release_cancels_queued_attempts_and_keeps_unrelated_work()
    {
        var worker = new ControlledTranscriber { IgnoreCancellation = true };
        var writer = new Writer();
        var queue = new TranscriptionQueue(worker, writer, new Repository());
        var recording = Recording();
        await queue.EnqueueAsync(recording, default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.EnqueueAsync(recording, default);
        var unrelated = Recording();
        await queue.EnqueueAsync(unrelated, default);
        var cancellation = queue.CancelRecordingAsync(recording.Id, default);
        Assert.False(cancellation.IsCompleted);
        Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[1].Status);
        Assert.Equal(TranscriptionJobStatus.Queued, queue.Jobs[2].Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryAsync(recording, default));
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(recording, default));
        Assert.Contains("deletion or worker termination is pending", blocked.Message);
        worker.Release.TrySetResult();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(default);
        Assert.Equal(TranscriptionJobStatus.Canceled, queue.Jobs[0].Status);
        Assert.Equal(TranscriptionJobStatus.Succeeded, queue.Jobs[2].Status);
        Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public async Task Stop_during_output_publication_cannot_turn_into_success_or_leave_new_outputs()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"late-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var worker = new ControlledTranscriber();
            worker.Release.TrySetResult();
            var writer = new LateWriter(directory);
            var queue = new TranscriptionQueue(worker, writer, new Repository());
            await queue.EnqueueAsync(Recording(), default);
            await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var job = Assert.Single(queue.Jobs);
            await queue.StopAsync(job.Id, default);
            writer.Release.TrySetResult();
            await queue.WaitForIdleAsync(default);
            Assert.Equal(TranscriptionJobStatus.Interrupted, job.Status);
            Assert.Null(job.Outputs);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class LateWriter(string directory) : ITranscriptWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task;
            var outputs = new TranscriptOutputs(Path.Combine(directory, "owned.txt"), Path.Combine(directory, "owned.srt"));
            await File.WriteAllTextAsync(outputs.TextPath, "new text");
            await File.WriteAllTextAsync(outputs.SubtitlePath, "new subtitles");
            return outputs;
        }
    }

    [Fact]
    public async Task A_legacy_transcriber_cannot_silently_replace_a_pinned_artifact()
    {
        var queue = new TranscriptionQueue(new LegacyTranscriber(), new Writer(), new Repository(), models: new Models());
        await queue.EnqueueAsync(Recording(), default);
        await queue.WaitForIdleAsync(default);
        Assert.Equal(TranscriptionJobStatus.Failed, Assert.Single(queue.Jobs).Status);
        Assert.Contains("pinned", queue.Jobs[0].Error);
    }

    private sealed class LegacyTranscriber : ILocalTranscriber
    {
        public Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token) =>
            Task.FromResult(new TranscriptDocument(recording, recording.Language, []));
    }

    [Fact]
    public async Task Public_progress_events_keep_job_correlation_and_order_without_blocking_inference()
    {
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository());
        var seen = new System.Collections.Concurrent.ConcurrentQueue<TranscriptionJobProgress>();
        queue.ProgressChanged += (_, value) => seen.Enqueue(value);
        await queue.EnqueueAsync(Recording(), default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var percent in new[] { 0, 37, 84 }) worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, percent));
        await Task.Delay(100);
        var recognition = seen.Where(value => value.Progress.Phase == TranscriptionPhase.Recognizing).ToArray();
        Assert.Equal(new int?[] { 0, 37, 84 }, recognition.Select(value => value.Progress.Percent));
        Assert.All(recognition, value => Assert.Equal(queue.Jobs[0].Id, value.JobId));
        worker.Release.TrySetResult();
        await queue.WaitForIdleAsync(default);
    }

    [Fact]
    public async Task Legacy_outputs_cannot_lose_their_only_ownership_when_job_history_is_deleted()
    {
        var history = new History();
        var job = new TranscriptionJob(Guid.NewGuid(), history.Recording, DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded, new("earlier.txt", "earlier.srt"), null);
        var repository = new Repository { Stored = [job] };
        var queue = new TranscriptionQueue(new ControlledTranscriber(), new Writer(), repository, history: history);
        await queue.RestoreAsync(default);
        await queue.DeleteJobAsync(job.Id, default);
        Assert.Empty(queue.Jobs);
        var owned = Assert.Single(history.Recording.TranscriptOutputs);
        Assert.Equal(job.Id, owned.JobId);
        Assert.Equal(job.Outputs, owned.Outputs);
    }

    [Fact]
    public async Task Pending_audio_deletion_during_recognition_prevents_output_publication()
    {
        var history = new History { Recording = Recording() };
        var worker = new ControlledTranscriber();
        var writer = new Writer();
        var queue = new TranscriptionQueue(worker, writer, new Repository(), history: history);
        await queue.EnqueueAsync(history.Recording, default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        history.Recording = history.Recording with { AudioDeleted = true };
        worker.Release.TrySetResult();
        await queue.WaitForIdleAsync(default);
        Assert.Equal(0, writer.Calls);
        Assert.Equal(TranscriptionJobStatus.Failed, Assert.Single(queue.Jobs).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Recording() with {
            Id = history.Recording.Id }, default));
    }

    [Fact]
    public async Task Persisted_attempt_retains_choices_and_user_stop_reason_but_legacy_provenance_stays_unknown()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"job-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new JsonTranscriptionJobRepository(Path.Combine(directory, "jobs.json"));
            var job = new TranscriptionJob(Guid.NewGuid(), Recording(), DateTimeOffset.UtcNow,
                TranscriptionJobStatus.Interrupted, null, null, "tiny-q5", "User requested Stop.");
            await repository.SaveAsync([job], default);
            var restored = Assert.Single(await repository.LoadAsync(default));
            Assert.Equal("tiny-q5", restored.ModelId);
            Assert.Equal("User requested Stop.", restored.InterruptionReason);
            Assert.Equal(job.Id, restored.Id);
            Assert.Equal("English", restored.Language);
            await File.WriteAllTextAsync(Path.Combine(directory, "jobs.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { new {
                    job.Id, job.Recording, job.CreatedAt, Status = TranscriptionJobStatus.Succeeded,
                    Outputs = new TranscriptOutputs("earlier.txt", "earlier.srt"), Error = (string?)null } }));
            Assert.Null(Assert.Single(await repository.LoadAsync(default)).ModelId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Stop_acknowledges_but_keeps_attempt_active_until_worker_release()
    {
        var worker = new ControlledTranscriber { IgnoreCancellation = true };
        var writer = new Writer();
        var queue = new TranscriptionQueue(worker, writer, new Repository());
        var recording = Recording();
        await queue.EnqueueAsync(recording, default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var job = Assert.Single(queue.Jobs);
        await queue.StopAsync(job.Id, default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(job.IsTerminationPending);
        Assert.Equal(TranscriptionJobStatus.Running, job.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DeleteJobAsync(job.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryAsync(recording, default));
        worker.Release.TrySetResult();
        await queue.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(job.IsTerminationPending);
        Assert.Equal(TranscriptionJobStatus.Interrupted, job.Status);
        Assert.Equal("User requested Stop.", job.InterruptionReason);
        Assert.Equal(0, writer.Calls);
        worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, 84));
        Assert.NotEqual(new(TranscriptionPhase.Recognizing, 84), job.Progress);
    }

    [Fact]
    public async Task Recognition_100_is_not_success_and_writing_stays_indeterminate()
    {
        var worker = new ControlledTranscriber();
        var writer = new Writer { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var queue = new TranscriptionQueue(worker, writer, new Repository());
        await queue.EnqueueAsync(Recording(), default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        worker.Reporter?.Report(new(TranscriptionPhase.Recognizing, 100));
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(new(TranscriptionPhase.Recognizing, 100), job.Progress);
        Assert.Equal(TranscriptionJobStatus.Running, job.Status);
        Assert.Null(job.Outputs);
        worker.Release.TrySetResult();
        await WaitUntilAsync(() => writer.Calls == 1);
        Assert.Equal(new(TranscriptionPhase.Writing), job.Progress);
        Assert.Equal(TranscriptionJobStatus.Running, job.Status);
        writer.Release.TrySetResult();
        await queue.WaitForIdleAsync(default);
        Assert.Equal(TranscriptionJobStatus.Succeeded, job.Status);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Missing_pinned_model_fails_without_substitution()
    {
        var models = new Models { Missing = true };
        var worker = new ControlledTranscriber();
        worker.Release.TrySetResult();
        var writer = new Writer();
        var queue = new TranscriptionQueue(worker, writer, new Repository(), models: models);
        await queue.EnqueueAsync(Recording(), default);
        await queue.WaitForIdleAsync(default);
        var job = Assert.Single(queue.Jobs);
        Assert.Equal(TranscriptionJobStatus.Failed, job.Status);
        Assert.Contains("model", job.Error);
        Assert.Contains("'base-original'", job.Error);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(0, writer.Calls);
    }

    [Fact]
    public async Task New_attempt_snapshots_selected_model_and_language()
    {
        var models = new Models { SelectedModelId = "base-original" };
        var worker = new ControlledTranscriber();
        var queue = new TranscriptionQueue(worker, new Writer(), new Repository(), models: models);
        var recording = Recording();
        await queue.EnqueueAsync(recording, default);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        models.SelectedModelId = "small-q5";
        var first = Assert.Single(queue.Jobs);
        Assert.Equal("base-original", first.ModelId);
        Assert.Equal("English", first.Language);
        await queue.EnqueueAsync(recording with { Language = "Brazilian Portuguese" }, default);
        Assert.Equal("small-q5", queue.Jobs[1].ModelId);
        worker.Release.TrySetResult();
        await queue.WaitForIdleAsync(default);
    }

    internal static SavedRecording Recording() =>
        new(Guid.NewGuid(), "saved.wav", "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));

    internal sealed class Models : ITranscriptionModelStore
    {
        public string? SelectedModelId { get; set; } = "base-original";
        public bool Missing { get; set; }
        public bool HasCompatibleModel(string language) => true;
        public string? GetModelPath(string language) => "selected.bin";
        public string? GetModelPath(string language, string? pinnedModelId) => Missing ? null : $"{pinnedModelId}.bin";
    }

    internal sealed class ControlledTranscriber : IJobTranscriber
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<TranscriptionProgress>? Reporter { get; private set; }
        public TranscriptionRequest? Request { get; private set; }
        public bool IgnoreCancellation { get; set; }
        public bool CaptureCancellationHandle { get; set; }
        public Action? CancellationCallback { get; set; }
        public WaitHandle? CancellationHandle { get; private set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token) =>
            TranscribeAsync(new(Guid.Empty, recording, null, null), null, token);
        public async Task<TranscriptDocument> TranscribeAsync(
            TranscriptionRequest request, IProgress<TranscriptionProgress>? progress, CancellationToken token)
        {
            Request = request;
            Reporter = progress;
            if (CaptureCancellationHandle) CancellationHandle = token.WaitHandle;
            if (CancellationCallback is not null) token.Register(CancellationCallback);
            Calls++;
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : token);
                return new(request.Recording, request.Recording.Language, []);
            }
            finally { Finished.TrySetResult(); }
        }
    }

    internal sealed class Writer : ITranscriptWriter
    {
        public int Calls { get; private set; }
        public TaskCompletionSource? Release { get; set; }
        public async Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken token)
        {
            Calls++;
            if (Release is not null) await Release.Task.WaitAsync(token);
            return new("unique.txt", "unique.srt");
        }
    }

    internal sealed class Repository : ITranscriptionJobRepository
    {
        public IReadOnlyList<TranscriptionJob> Stored { get; set; } = [];
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken token) => Task.FromResult(Stored);
        public Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken token)
        {
            Stored = jobs.ToArray();
            return Task.CompletedTask;
        }
    }

    internal sealed class History : IRecordingHistory
    {
        public Exception? RegisterFailure { get; set; }
        public Func<OwnedTranscriptOutputs, Task>? Registration { get; set; }
        public SavedRecording Recording { get; set; } = TranscriptionJobManagementTests.Recording();
        public Task<IReadOnlyList<SavedRecording>> LoadAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SavedRecording>>([Recording]);
        public Task AddAsync(SavedRecording recording, CancellationToken token) { Recording = recording; return Task.CompletedTask; }
        public async Task RegisterOutputsAsync(Guid id, OwnedTranscriptOutputs outputs, CancellationToken token)
        {
            if (RegisterFailure is not null) throw RegisterFailure;
            if (Registration is not null) await Registration(outputs);
            if (Recording.TranscriptOutputs.Contains(outputs)) return;
            Recording = Recording with { TranscriptOutputs = [.. Recording.TranscriptOutputs, outputs] };
        }
    }
}
