using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SoundRecorder.Infrastructure.Analysis.Storage;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class AnalysisJobCoordinatorTests
{
    [Fact]
    public async Task RecoverAsync_NewModelRequestQueued_PreservesPinsWithoutAdoptingPreviousCompletion()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var original = Request();
        var completed = await store.BeginAsync(original.Identity, default);
        await store.CompleteAsync(completed, Output(), default);
        var repository = new MemoryRepository();
        await repository.SaveAsync([new(Guid.NewGuid(), original, DateTimeOffset.UtcNow, AnalysisJobStatus.Complete, completed)], default);
        var queue = new AnalysisJobCoordinator(repository, store, _ => throw new InvalidOperationException("Must not run during capture"));
        await queue.RecoverAsync();
        await queue.SetRecordingIntentAsync(true);
        var replacement = original with { ModelDirectory = "new-verified-directory", Identity = original.Identity with
        {
            Generation = original.Identity.Generation with { Id = "new-generation", Sha256 = new('E', 64) }
        } };
        var pending = await queue.EnqueueAsync(replacement);
        await queue.WaitForIdleAsync();
        AnalysisJobExecution? observed = null;
        var recovered = new AnalysisJobCoordinator(repository, store, execution => new Worker(async token =>
        {
            observed = execution;
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }));
        await recovered.RecoverAsync();
        Assert.Equal(AnalysisJobStatus.Complete, recovered.Jobs[0].Status);
        Assert.Equal(AnalysisJobStatus.Interrupted, recovered.Jobs[1].Status);
        Assert.Null(recovered.Jobs[1].Revision);
        await recovered.RetryAsync(pending.Id);
        await recovered.WaitForIdleAsync();
        Assert.Equal(replacement, observed!.Request);
        Assert.Equal(completed, (await store.LoadAsync(original.Identity, default)).Completed);
        Assert.NotEqual(completed, (await store.LoadAsync(replacement.Identity, default)).Completed);
    }

    [Fact]
    public async Task RecoverAsync_KnownRevisionCorruptCheckpoint_ReportsStorageFailureInsteadOfDiscardingIt()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var request = Request();
        var revision = await store.BeginAsync(request.Identity, default);
        await store.SaveCheckpointAsync(revision, new(0, "Index", "QUEUE_CHECKPOINT_TOKEN"), EmptyChunks(), default);
        var repository = new MemoryRepository();
        await repository.SaveAsync([new(Guid.NewGuid(), request, DateTimeOffset.UtcNow, AnalysisJobStatus.Running, revision)], default);
        var bytes = await File.ReadAllBytesAsync(files.Database);
        var offset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("QUEUE_CHECKPOINT_TOKEN"));
        Assert.True(offset >= 0);
        bytes[offset] = (byte)'Z';
        await File.WriteAllBytesAsync(files.Database, bytes);
        var recovered = new AnalysisJobCoordinator(repository, SqliteTranscriptAnalysisStore.Open(files.Database),
            _ => throw new InvalidOperationException("Must not run"));
        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.RecoverAsync());
        Assert.Empty(recovered.Jobs);
        Assert.Equal(AnalysisJobStatus.Running, Assert.Single(repository.Jobs).Status);
    }

    [Fact]
    public async Task RecoverAsync_RetryWasQueued_DoesNotAdoptPreviousFailedRevision()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var request = Request();
        var previousRevision = await store.BeginAsync(request.Identity, default);
        await store.SetUnfinishedAsync(previousRevision, AnalysisStoreStatus.Failed, default);
        var previous = new AnalysisJob(Guid.NewGuid(), request, DateTimeOffset.UtcNow, AnalysisJobStatus.Failed, previousRevision);
        var retry = new AnalysisJob(Guid.NewGuid(), request, previous.CreatedAt.AddSeconds(1), AnalysisJobStatus.Queued, PreviousJobId: previous.Id);
        var repository = new MemoryRepository();
        await repository.SaveAsync([previous, retry], default);
        var queue = new AnalysisJobCoordinator(repository, store, _ => throw new InvalidOperationException("Must not run"));
        await queue.RecoverAsync();
        Assert.Equal(AnalysisJobStatus.Interrupted, queue.Jobs[1].Status);
        Assert.Null(queue.Jobs[1].Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync(retry.Id, request.Identity));
        Assert.Equal(previousRevision, (await store.LoadAsync(request.Identity, default)).Unfinished);
    }

    [Fact]
    public async Task ProcessAsync_AdapterThrowsAfterCommit_RetainsAuthoritativeCompletedOutput()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, execution => new Worker(async token =>
        {
            await store.CompleteAsync(execution.Revision, Output(), token);
            throw new IOException("Controlled postcommit failure");
        }));
        var job = await queue.EnqueueAsync(Request());
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(queue.Jobs).Status);
        Assert.Equal(AnalysisStoreStatus.Complete, (await store.LoadAsync(job.Request.Identity, default)).Status);
    }

    [Fact]
    public async Task SetRecordingIntentAsync_JournalWriteHeld_AnnouncesCaptureWithoutWaitingForStorage()
    {
        using var files = new Files();
        var repository = new HeldRepository();
        var queue = new AnalysisJobCoordinator(repository, SqliteTranscriptAnalysisStore.Create(files.Database),
            _ => throw new InvalidOperationException("Must not run during capture"));
        var enqueue = queue.EnqueueAsync(Request());
        await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var capture = queue.SetRecordingIntentAsync(true);
        try { Assert.True(capture.IsCompleted); }
        finally
        {
            repository.Release.TrySetResult();
            await enqueue;
            await capture;
            await queue.WaitForIdleAsync();
        }
        Assert.Equal(AnalysisJobStatus.Queued, Assert.Single(queue.Jobs).Status);
    }

    [Fact]
    public async Task WaitForIdleAsync_CompletionJournalWriteFails_SurfacesStorageFaultAndRecoveryKeepsCommittedOutput()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var repository = new MemoryRepository();
        var queue = new AnalysisJobCoordinator(repository, store, execution => new Worker(async token =>
        {
            await store.CompleteAsync(execution.Revision, Output(), token);
            repository.FailSave = true;
            return new("Completed", null, "", []);
        }));
        var job = await queue.EnqueueAsync(Request());
        await Assert.ThrowsAsync<IOException>(() => queue.WaitForIdleAsync());
        Assert.Equal(AnalysisStoreStatus.Complete, (await store.LoadAsync(job.Request.Identity, default)).Status);
        repository.FailSave = false;
        var recovered = new AnalysisJobCoordinator(repository, store, _ => throw new InvalidOperationException("Must not run"));
        await recovered.RecoverAsync();
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(recovered.Jobs).Status);
    }

    [Fact]
    public async Task EnqueueAsync_InvalidStoreIdentity_FailsThatJobAndReleasesWorkerForNextRequest()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var calls = 0;
        var logger = new CapturingLogger();
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, execution => new Worker(async token =>
        {
            calls++;
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }), logger: logger);
        var invalid = Request();
        var failed = await queue.EnqueueAsync(invalid with { Identity = invalid.Identity with { TextSha256 = "invalid" } });
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Failed, Assert.Single(queue.Jobs).Status);
        Assert.Null(queue.Jobs[0].Revision);
        Assert.Equal("ArgumentException", queue.Jobs[0].ErrorCode);
        Assert.Contains("ArgumentException", Assert.Single(logger.Events));
        Assert.Contains(failed.Id.ToString(), logger.Events[0]);
        await queue.EnqueueAsync(Request());
        await queue.WaitForIdleAsync();
        Assert.Equal(1, calls);
        Assert.Equal(AnalysisJobStatus.Complete, queue.Jobs[1].Status);
        Assert.Equal(failed.Id, queue.Jobs[0].Id);
    }

    [Fact]
    public async Task CancelAsync_RegisteredReleaseCallback_DoesNotReturnBeforeCallbackFinishes()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, _ => new Worker(async token =>
        {
            captured = token;
            using var registration = token.Register(() =>
            {
                callbackEntered.SetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            entered.SetResult();
            await releaseCallback.Task;
            token.ThrowIfCancellationRequested();
            return new("Completed", null, "", []);
        }));
        var job = await queue.EnqueueAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancellation = queue.CancelAsync(job.Id);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(cancellation.IsCompleted);
        releaseCallback.SetResult();
        await cancellation;
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Cancelled, Assert.Single(queue.Jobs).Status);
        Assert.Throws<ObjectDisposedException>(() => captured.WaitHandle);
    }

    [Theory]
    [InlineData("queued", AnalysisJobStatus.Interrupted)]
    [InlineData("orphan", AnalysisJobStatus.Interrupted)]
    [InlineData("complete", AnalysisJobStatus.Complete)]
    [InlineData("cancelled", AnalysisJobStatus.Cancelled)]
    public async Task RecoverAsync_ReopenedRealJournal_ReconcilesCrashWithoutStartingWork(string phase, AnalysisJobStatus expected)
    {
        using var files = new Files();
        var request = Request();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        AnalysisStoreRevision? revision = null;
        if (phase != "queued") revision = await store.BeginAsync(request.Identity, default);
        if (phase == "complete") await store.CompleteAsync(revision!, Output(), default);
        if (phase == "cancelled") await store.SetUnfinishedAsync(revision!, AnalysisStoreStatus.Cancelled, default);
        var status = phase == "queued" ? AnalysisJobStatus.Queued :
            phase == "cancelled" ? AnalysisJobStatus.Cancelled : AnalysisJobStatus.Running;
        var job = new AnalysisJob(Guid.NewGuid(), request, DateTimeOffset.UtcNow, status, phase == "orphan" ? null : revision);
        var repository = AnalysisQueueRepository.Create(files.Journal);
        await repository.SaveAsync([job], default);
        var queue = new AnalysisJobCoordinator(AnalysisQueueRepository.Open(files.Journal),
            SqliteTranscriptAnalysisStore.Open(files.Database), _ => throw new InvalidOperationException("Recovery must not run inference"));
        await queue.RecoverAsync();
        await queue.WaitForIdleAsync();
        Assert.Equal(expected, Assert.Single(queue.Jobs).Status);
        Assert.Equal(revision, queue.Jobs[0].Revision);
        Assert.Equal(queue.Jobs, await AnalysisQueueRepository.Open(files.Journal).LoadAsync(default));
    }

    [Fact]
    public async Task ResumeAsync_SupersededRevision_RejectsWithoutChangingTheNewWriter()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var request = Request();
        var stale = await store.BeginAsync(request.Identity, default);
        await store.SetUnfinishedAsync(stale, AnalysisStoreStatus.Cancelled, default);
        var current = await store.BeginAsync(request.Identity, default);
        var repository = new MemoryRepository();
        var job = new AnalysisJob(Guid.NewGuid(), request, DateTimeOffset.UtcNow, AnalysisJobStatus.Cancelled, stale);
        await repository.SaveAsync([job], default);
        var queue = new AnalysisJobCoordinator(repository, store, _ => throw new InvalidOperationException("Must not run"));
        await queue.RecoverAsync();
        Assert.Equal("Resume requires the current retained revision.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync(job.Id, request.Identity))).Message);
        Assert.Equal(current, (await store.LoadAsync(request.Identity, default)).Unfinished);
        await repository.SaveAsync([job with { Status = AnalysisJobStatus.Running }], default);
        var staleRestore = new AnalysisJobCoordinator(repository, store, _ => throw new InvalidOperationException("Must not run"));
        Assert.Equal("Recovered job no longer owns the current revision.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => staleRestore.RecoverAsync())).Message);
        Assert.Empty(staleRestore.Jobs);
        Assert.Equal(AnalysisStoreStatus.Processing, (await store.LoadAsync(request.Identity, default)).Status);
    }

    [Fact]
    public async Task RetryAsync_OriginalAttemptAlreadyRetried_RejectsConcurrentRetryOrResume()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var request = Request();
        var revision = await store.BeginAsync(request.Identity, default);
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, default);
        var repository = new MemoryRepository();
        var original = new AnalysisJob(Guid.NewGuid(), request, DateTimeOffset.UtcNow, AnalysisJobStatus.Cancelled, revision);
        await repository.SaveAsync([original], default);
        var queue = new AnalysisJobCoordinator(repository, store, _ => throw new InvalidOperationException("Must not run"));
        await queue.RecoverAsync();
        await queue.SetRecordingIntentAsync(true);
        var retry = await queue.RetryAsync(original.Id);
        Assert.Equal("This transcript already has an active analysis request.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryAsync(original.Id))).Message);
        Assert.Equal("This transcript already has an active analysis request.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync(original.Id, request.Identity))).Message);
        Assert.Equal("Only released unfinished jobs can be retried or resumed.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryAsync(retry.Id))).Message);
        await queue.CancelAsync(retry.Id);
        await queue.ResumeAsync(original.Id, request.Identity);
        await queue.CancelAsync(original.Id);
        Assert.Equal(AnalysisStoreStatus.Cancelled, (await store.LoadAsync(request.Identity, default)).Status);
    }

    [Fact]
    public async Task EnqueueAsync_StorageFailure_KeepsUncommittedJobOutOfMemoryAndLogsOnlyOperationalIdentity()
    {
        using var files = new Files();
        var repository = new MemoryRepository { FailSave = true };
        var logger = new CapturingLogger();
        var clock = new FixedClock();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var queue = new AnalysisJobCoordinator(repository, store, _ => new Worker(token => throw new IOException("SECRET transcript path")),
            clock, logger);
        await Assert.ThrowsAsync<IOException>(() => queue.EnqueueAsync(Request()));
        Assert.Empty(queue.Jobs);
        repository.FailSave = false;
        var job = await queue.EnqueueAsync(Request());
        Assert.Equal(clock.GetUtcNow(), job.CreatedAt);
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Failed, Assert.Single(queue.Jobs).Status);
        Assert.Contains(job.Id.ToString(), Assert.Single(logger.Events));
        Assert.Equal("IOException", queue.Jobs[0].ErrorCode);
        Assert.Contains("IOException", logger.Events[0]);
        Assert.DoesNotContain("SECRET", logger.Events[0]);
    }

    [Fact]
    public async Task EnterInferenceAsync_ConcurrentCallsAndCancellation_ReleaseIsExclusiveAndIdempotent()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, execution => new Worker(async token =>
        {
            var first = await execution.EnterInferenceAsync(token);
            var second = execution.EnterInferenceAsync(token);
            Assert.False(second.IsCompleted);
            first.Dispose();
            using var held = await second.WaitAsync(TimeSpan.FromSeconds(10));
            first.Dispose();
            using var cancel = new CancellationTokenSource();
            var waiting = execution.EnterInferenceAsync(cancel.Token);
            Assert.False(waiting.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            held.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.EnterInferenceAsync(cancel.Token));
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }));
        await queue.EnqueueAsync(Request());
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(queue.Jobs).Status);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("empty-attempt")]
    [InlineData("language")]
    [InlineData("unsupported-language")]
    [InlineData("model-directory")]
    [InlineData("source")]
    [InlineData("subtitle")]
    [InlineData("mode")]
    [InlineData("summary-question")]
    [InlineData("question-empty")]
    [InlineData("question-whitespace")]
    [InlineData("question-long")]
    [InlineData("antecedent-long")]
    [InlineData("antecedent-empty")]
    public async Task EnqueueAsync_IncompatibleRequest_RejectsBeforePersistence(string invalid)
    {
        using var files = new Files();
        var repository = new MemoryRepository();
        var queue = new AnalysisJobCoordinator(repository, SqliteTranscriptAnalysisStore.Create(files.Database),
            _ => throw new InvalidOperationException("Must not run"));
        var request = Request();
        request = invalid switch
        {
            "attempt" => request with { Transcript = request.Transcript with { AttemptId = Guid.NewGuid() } },
            "empty-attempt" => request with { Identity = request.Identity with { AttemptId = Guid.Empty },
                Transcript = request.Transcript with { AttemptId = Guid.Empty } },
            "language" => request with { Transcript = request.Transcript with { Language = "Brazilian Portuguese" } },
            "unsupported-language" => request with { Identity = request.Identity with { Language = "Spanish" },
                Transcript = request.Transcript with { Language = "Spanish" } },
            "model-directory" => request with { ModelDirectory = "" },
            "source" => request with { Transcript = request.Transcript with { Outputs = new("", "one.srt") } },
            "subtitle" => request with { Transcript = request.Transcript with { Outputs = new("one.txt", "") } },
            "mode" => request with { Mode = (AnalysisJobMode)99 },
            "summary-question" => request with { Transcript = request.Transcript with { Question = "Question" } },
            "question-empty" => request with { Mode = AnalysisJobMode.GlobalQuestion, Transcript = request.Transcript with { Question = "" } },
            "question-whitespace" => request with { Mode = AnalysisJobMode.LocalQuestion, Transcript = request.Transcript with { Question = " " } },
            "question-long" => request with { Mode = AnalysisJobMode.GlobalQuestion, Transcript = request.Transcript with { Question = new('Q', 513) } },
            "antecedent-long" => request with { Discussion = new(true, new('Q', 129)) },
            _ => request with { Discussion = new(true, "") }
        };
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() => queue.EnqueueAsync(request));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        if (invalid == "mode") Assert.Equal("Select an explicit analysis coverage mode.", error.Message);
        Assert.Empty(repository.Jobs);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task EnqueueAsync_SameAttemptAlreadyQueued_RejectsCompetingOwner()
    {
        using var files = new Files();
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), SqliteTranscriptAnalysisStore.Create(files.Database),
            _ => throw new InvalidOperationException("Must not run"));
        await queue.SetRecordingIntentAsync(true);
        var request = Request();
        var job = await queue.EnqueueAsync(request);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(request with
        {
            Identity = request.Identity with { AlgorithmVersion = "other-version" }
        }));
        Assert.Equal("This transcript already has an active analysis request.", busy.Message);
        await queue.CancelAsync(job.Id);
        await queue.CancelAsync(job.Id);
        Assert.Equal(AnalysisJobStatus.Cancelled, Assert.Single(queue.Jobs).Status);
        Assert.Equal("Analysis job is not available.", (await Assert.ThrowsAsync<KeyNotFoundException>(() => queue.CancelAsync(Guid.NewGuid()))).Message);
        Assert.Equal("Resume requires the exact retained transcript/model identity.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync(job.Id, request.Identity))).Message);
    }

    [Theory]
    [InlineData(AnalysisJobMode.GlobalQuestion, 512)]
    [InlineData(AnalysisJobMode.LocalQuestion, 1)]
    public async Task EnqueueAsync_ExplicitQuestionMode_PreservesModeAndBoundedQuestion(AnalysisJobMode mode, int length)
    {
        using var files = new Files();
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), SqliteTranscriptAnalysisStore.Create(files.Database),
            _ => throw new InvalidOperationException("Must not run"));
        await queue.SetRecordingIntentAsync(true);
        var request = Request();
        request = request with { Mode = mode, Transcript = request.Transcript with { Question = new('Q', length), Language = "Brazilian Portuguese" },
            Identity = request.Identity with { Language = "Brazilian Portuguese" }, Discussion = new(true, new('A', 128)) };
        var job = await queue.EnqueueAsync(request);
        Assert.Equal(request, job.Request);
        Assert.Equal(mode, job.Request.Mode);
        await queue.CancelAsync(job.Id);
    }

    [Fact]
    public async Task ProcessAsync_ReturnedSuccessWithoutCommit_FailsHonestlyAndDoesNotAutomaticallyRetry()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var calls = 0;
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, _ => new Worker(token =>
        {
            calls++;
            return Task.FromResult(new TranscriptAnalysisResult("Completed", null, "", []));
        }));
        var job = await queue.EnqueueAsync(Request());
        await queue.WaitForIdleAsync();
        Assert.Equal(1, calls);
        Assert.Equal(AnalysisJobStatus.Failed, Assert.Single(queue.Jobs).Status);
        Assert.Equal(AnalysisStoreStatus.Failed, (await store.LoadAsync(job.Request.Identity, default)).Status);
        Assert.Null((await store.LoadAsync(job.Request.Identity, default)).Completed);
    }

    [Fact]
    public async Task CancelAsync_CommitAlreadyDurable_DoesNotDemoteCompletedOutput()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, execution => new Worker(async token =>
        {
            await store.CompleteAsync(execution.Revision, Output(), token);
            committed.SetResult();
            await release.Task;
            return new("Completed", null, "", []);
        }));
        var job = await queue.EnqueueAsync(Request());
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.CancelAsync(job.Id);
        release.SetResult();
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(queue.Jobs).Status);
        Assert.Equal(AnalysisStoreStatus.Complete, (await store.LoadAsync(job.Request.Identity, default)).Status);
    }

    [Fact]
    public async Task RecoverAsync_RunningCheckpoint_RestoresInterruptedAndResumesOnlyExplicitly()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var request = Request();
        var revision = await store.BeginAsync(request.Identity, default);
        await store.SaveCheckpointAsync(revision, new(0, "Index", "saved-cursor"), EmptyChunks(), default);
        var repository = new MemoryRepository();
        await repository.SaveAsync([new(Guid.NewGuid(), request, DateTimeOffset.UtcNow, AnalysisJobStatus.Running, revision)], default);
        var calls = 0;
        var queue = new AnalysisJobCoordinator(repository, store, execution => new Worker(async token =>
        {
            calls++;
            Assert.Equal(revision, execution.Revision);
            Assert.Equal("saved-cursor", (await store.LoadAsync(request.Identity, token)).Checkpoint!.Cursor);
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }));
        await queue.RecoverAsync();
        await queue.SetRecordingIntentAsync(false);
        await queue.WaitForIdleAsync();
        Assert.Equal(0, calls);
        Assert.Equal(AnalysisJobStatus.Interrupted, Assert.Single(queue.Jobs).Status);
        Assert.Equal(AnalysisStoreStatus.Interrupted, (await store.LoadAsync(request.Identity, default)).Status);
        var different = request.Identity with { AlgorithmVersion = "v2" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync(queue.Jobs[0].Id, different));
        await queue.ResumeAsync(queue.Jobs[0].Id, request.Identity);
        await queue.WaitForIdleAsync();
        Assert.Equal(1, calls);
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(queue.Jobs).Status);
        Assert.Equal("Restore requires a fresh idle coordinator.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RecoverAsync())).Message);
    }

    [Fact]
    public async Task CancelAsync_NativeCallHeld_PersistsPendingAndRetriesOnlyAfterRelease()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var repository = new MemoryRepository();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new AnalysisJobCoordinator(repository, store, execution => new Worker(async token =>
        {
            entered.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }));
        var original = await queue.EnqueueAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.CancelAsync(original.Id);
        Assert.Equal(AnalysisJobStatus.CancellationPending, Assert.Single(repository.Jobs).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryAsync(original.Id));
        release.SetResult();
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Cancelled, Assert.Single(queue.Jobs).Status);
        Assert.Equal(AnalysisStoreStatus.Cancelled, (await store.LoadAsync(original.Request.Identity, default)).Status);
        var retry = await queue.RetryAsync(original.Id);
        await queue.WaitForIdleAsync();
        Assert.NotEqual(original.Id, retry.Id);
        Assert.Equal(original.Id, retry.PreviousJobId);
        Assert.Equal(original.Request, retry.Request);
        Assert.NotEqual(queue.Jobs[0].Revision, queue.Jobs[1].Revision);
        Assert.Equal(AnalysisJobStatus.Complete, queue.Jobs[1].Status);
    }

    [Fact]
    public async Task SetRecordingIntentAsync_ActiveCapture_BlocksNewCallsUntilCaptureEnds()
    {
        using var files = new Files();
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new AnalysisJobCoordinator(new MemoryRepository(), store, execution => new Worker(async token =>
        {
            using (await execution.EnterInferenceAsync(token))
            {
                firstCall.SetResult();
                await release.Task;
            }
            waiting.SetResult();
            using (await execution.EnterInferenceAsync(token)) secondCall.SetResult();
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }));
        await queue.SetRecordingIntentAsync(true);
        await queue.EnqueueAsync(Request());
        await queue.WaitForIdleAsync();
        Assert.Equal(AnalysisJobStatus.Queued, Assert.Single(queue.Jobs).Status);
        Assert.False(firstCall.Task.IsCompleted);
        await queue.SetRecordingIntentAsync(false);
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.SetRecordingIntentAsync(true);
        release.SetResult();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(secondCall.Task.IsCompleted);
        await queue.SetRecordingIntentAsync(false);
        await queue.WaitForIdleAsync();
        Assert.True(secondCall.Task.IsCompleted);
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(queue.Jobs).Status);
    }

    [Fact]
    public async Task EnqueueAsync_HeldWorker_PersistsPinsAndRunsOneJobInFifoOrder()
    {
        using var files = new Files();
        var repository = new MemoryRepository();
        var calls = new List<AnalysisJobExecution>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = SqliteTranscriptAnalysisStore.Create(files.Database);
        var queue = new AnalysisJobCoordinator(repository, store, execution => new Worker(async token =>
        {
            lock (calls) calls.Add(execution);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            await store.CompleteAsync(execution.Revision, Output(), token);
            return new("Completed", null, "", []);
        }));
        var first = await queue.EnqueueAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await queue.EnqueueAsync(Request());
        Assert.Single(calls);
        Assert.Equal(first.Request, calls[0].Request);
        Assert.Equal(AnalysisJobStatus.Queued, repository.Jobs.Single(j => j.Id == second.Id).Status);
        release.SetResult();
        await queue.WaitForIdleAsync();
        Assert.Equal(new[] { first.Id, second.Id }, queue.Jobs.Select(j => j.Id));
        Assert.All(queue.Jobs, j => Assert.Equal(AnalysisJobStatus.Complete, j.Status));
        Assert.Equal(new[] { first.Request, second.Request }, calls.Select(c => c.Request));
    }

    private static AnalysisJobRequest Request()
    {
        var attempt = Guid.NewGuid();
        var configuration = "{}";
        var configurationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));
        var identity = new AnalysisStoreIdentity(attempt, new('A', 64), new('B', 64), "English",
            new("phi", new('C', 64), configurationHash, configuration),
            new("nomic", new('D', 64), configurationHash, configuration), "v1", 2);
        return new(new(attempt, new("immutable.txt", "immutable.srt"), "English", null), identity, "verified-immutable-model-directory");
    }

    private static async IAsyncEnumerable<AnalysisStoreOutput> Output()
    {
        await Task.CompletedTask;
        yield return new(0, "Summary", null, "Explicit empty evidence outcome", []);
    }

    private static async IAsyncEnumerable<AnalysisStoreChunk> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class Worker(Func<CancellationToken, Task<TranscriptAnalysisResult>> run) : ILocalTranscriptAnalysis
    {
        public Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
            IProgress<string> progress, CancellationToken cancellationToken) => run(cancellationToken);
    }

    private sealed class MemoryRepository : IAnalysisQueueRepository
    {
        public bool FailSave { get; set; }
        public IReadOnlyList<AnalysisJob> Jobs { get; private set; } = [];
        public Task<IReadOnlyList<AnalysisJob>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Jobs);
        public Task SaveAsync(IReadOnlyList<AnalysisJob> jobs, CancellationToken cancellationToken)
        {
            if (FailSave) throw new IOException("Controlled scheduling journal failure.");
            Jobs = jobs.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-24T00:01:02Z");
    }

    private sealed class HeldRepository : IAnalysisQueueRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AnalysisJob>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnalysisJob>>([]);
        public async Task SaveAsync(IReadOnlyList<AnalysisJob> jobs, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<AnalysisJobCoordinator>
    {
        public List<string> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Add(formatter(state, exception));
    }

    private sealed class Files : IDisposable
    {
        private readonly string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "scheduling-tests", Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(directory);
        public string Database => Path.Combine(directory, "analysis.sqlite");
        public string Journal => Path.Combine(directory, "jobs.sqlite");
        public void Dispose() => Directory.Delete(directory, true);
    }
}
