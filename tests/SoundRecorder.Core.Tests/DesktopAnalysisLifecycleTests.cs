using SoundRecorder.Analysis;
using SoundRecorder.Core;
using SoundRecorder.Desktop;
using SoundRecorder.Infrastructure.Analysis;
using SoundRecorder.Infrastructure.Analysis.Storage;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class DesktopAnalysisLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnalysisSetupCommand_TransferHeld_CancelsAndDrainsWithoutStartingInferenceOrLosingSelection(
        bool download, bool close)
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = new ReadySetup
        {
            Install = async (package, progress, token) =>
            {
                Assert.Equal(download ? null : "explicit-fixture.zip", package);
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.SetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
                return "must-not-be-selected";
            }
        };
        var adapter = new PersistentTranscriptAnalysis(store, repository, setup,
            _ => throw new InvalidOperationException("Setup must not start inference."));
        var vm = Reviewed(files, files.Job(files.Recording(Guid.NewGuid()), request.Outputs), adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        var work = download ? vm.DownloadAnalysisModelsCommand.ExecuteAsync(null) :
            vm.ImportAnalysisModelsCommand.ExecuteAsync("explicit-fixture.zip");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task closing = Task.CompletedTask;
        try
        {
            Assert.True(vm.IsSettingUpAnalysis);
            Assert.True(vm.HasPendingViewWork);
            Assert.False(vm.CanAnalyzeTranscript);
            Assert.False(vm.DownloadAnalysisModelsCommand.CanExecute(null));
            Assert.False(vm.ImportAnalysisModelsCommand.CanExecute("another.zip"));
            if (close) closing = vm.PrepareViewCloseAsync();
            else vm.CancelAnalysisSetupCommand.Execute(null);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(work.IsCompleted);
            if (close) Assert.False(closing.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(work, closing).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsSettingUpAnalysis);
        Assert.Equal(!close, vm.CanSetupAnalysis);
        Assert.Equal("pinned-fixture-directory", setup.Directory);
        Assert.Empty(await repository.LoadAsync(default));
        Assert.Contains("cancelled", vm.AnalysisReadiness, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AnalysisModelReadinessState.Invalid, "unverified-directory")]
    [InlineData(AnalysisModelReadinessState.ResourceUnavailable, "unverified-directory")]
    [InlineData(AnalysisModelReadinessState.Ready, null)]
    public async Task RunAsync_UnusableModelReadiness_ReportsSetupFailureWithoutPersistingOrStartingWork(
        AnalysisModelReadinessState state, string? directory)
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var setup = new ReadySetup { State = state, Directory = directory, Error = "Verify the selected local profile." };
        var adapter = new PersistentTranscriptAnalysis(store, repository, setup,
            _ => throw new InvalidOperationException("Inference must not start."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.AnalyzeAsync(request, new Progress<string>(), default));

        Assert.Contains(setup.Error, error.Message);
        Assert.Empty(adapter.Jobs);
        Assert.Empty(await repository.LoadAsync(default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.LoadAsync(AnalysisProcessingNative.PinAsync(request, default).GetAwaiter().GetResult(), default));
    }

    [Fact]
    public async Task VerifySourceAsync_OtherAttemptIsAlsoRetained_AcceptsOnlyTheExactOwnedSource()
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution =>
            new AnalysisProcessingService(store, execution.Revision, files.Directory, new Generation(), new Vectors(),
                AnalysisProcessingMode.Summary));
        await adapter.AnalyzeAsync(request, new Progress<string>(), default);
        await adapter.AnalyzeAsync(request with { AttemptId = Guid.NewGuid() }, new Progress<string>(), default);

        await adapter.VerifySourceAsync(request, default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.VerifySourceAsync(request with { AttemptId = Guid.NewGuid() }, default));
        await File.WriteAllTextAsync(request.Outputs.TextPath, "Changed original bytes.");
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.VerifySourceAsync(request, default));

        Assert.Equal(2, (await repository.LoadAsync(default)).Count);
    }

    [Theory]
    [InlineData(true, "attempt")]
    [InlineData(false, "attempt")]
    [InlineData(true, "language")]
    [InlineData(false, "language")]
    [InlineData(true, "source")]
    [InlineData(false, "source")]
    public async Task ContinueAsync_DifferentSelectedIdentity_RejectsWithoutResumingOrReplacingThePinnedAttempt(
        bool resume, string changed)
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var identity = await AnalysisProcessingNative.PinAsync(request, default);
        var revision = await store.BeginAsync(identity, default);
        await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, default);
        var original = new AnalysisJob(Guid.NewGuid(), new(request, identity, "original-cache"),
            DateTimeOffset.UtcNow, AnalysisJobStatus.Cancelled, revision);
        await repository.SaveAsync([original], default);
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(),
            _ => throw new InvalidOperationException("Inference must not start."));
        await adapter.RecoverAsync();
        var target = changed switch
        {
            "attempt" => request with { AttemptId = Guid.NewGuid() },
            "language" => request with { Language = "Brazilian Portuguese" },
            _ => request
        };
        if (changed == "source")
            await File.WriteAllTextAsync(request.Outputs.TextPath, "Changed original bytes.");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.ContinueAsync(original.Id, target, resume, new Progress<string>(), default));

        Assert.Equal(original, Assert.Single(await repository.LoadAsync(default)));
        var retained = await SqliteTranscriptAnalysisStore.Open(Path.Combine(files.Directory, "analysis.sqlite"))
            .LoadAsync(identity, default);
        Assert.Equal(AnalysisStoreStatus.Cancelled, retained.Status);
        Assert.Equal(revision, retained.Unfinished);
        Assert.Null(retained.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateSummaryCommand_BackendFails_PreservesActionableFailureAndReleasesForExplicitRetry(bool throws)
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var job = files.Job(files.Recording(Guid.NewGuid()), request.Outputs);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), _ => new Worker(token =>
            throws ? Task.FromException<TranscriptAnalysisResult>(new IOException("fixture local cache unavailable")) :
            Task.FromResult(new TranscriptAnalysisResult("TokenLimited", "fixture generation budget exhausted", "", []))));
        var vm = Reviewed(files, job, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);

        await vm.GenerateSummaryCommand.ExecuteAsync(null);

        Assert.False(vm.IsAnalyzing);
        Assert.True(vm.GenerateSummaryCommand.CanExecute(null));
        Assert.Empty(vm.AnalysisClaims);
        Assert.Contains(throws ? "fixture local cache unavailable" : "fixture generation budget exhausted", vm.AnalysisStatus);
        Assert.Equal(AnalysisJobStatus.Failed, Assert.Single(await repository.LoadAsync(default)).Status);
        Assert.Null((await store.LoadAsync(adapter.Jobs[0].Request.Identity, default)).Completed);
    }

    [Fact]
    public async Task RestoreAsync_BoundedPresentation_RetainsFullPersistedHistoryAndOriginalCandidates()
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var identity = await AnalysisProcessingNative.PinAsync(request, default);
        var revision = await store.BeginAsync(identity, default);
        var outputs = Enumerable.Range(0, 201).Select(index => new AnalysisStoreOutput(index, "Discussion",
            $"user-question-{index:D3}", $"retained-content-{index:D3}",
            index == 0 ? [] :
            [
                new($"candidate-{index:D3}-a", [new("original excerpt", 1000, "Ana sends the draft.")]),
                new($"candidate-{index:D3}-b", [new("original excerpt", null, "Ana sends the draft.")])
            ])).ToArray();
        await store.CompleteAsync(revision, outputs.ToAsyncEnumerable(), default);
        await repository.SaveAsync([new(Guid.NewGuid(), new(request, identity, "pinned-cache"),
            DateTimeOffset.UtcNow, AnalysisJobStatus.Complete, revision)], default);
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(),
            _ => throw new InvalidOperationException("Restoration must not start inference."));
        await adapter.RecoverAsync();

        var result = await adapter.RestoreAsync(request, default);

        Assert.Equal(200, result.Claims.Count);
        Assert.Equal("candidate-001-a", result.Claims[0].Text);
        Assert.Equal("candidate-100-b", result.Claims[^1].Text);
        Assert.Contains("user-question-000", adapter.History);
        Assert.Contains("retained-content-000", adapter.History);
        Assert.Contains("user-question-199", adapter.History);
        Assert.Contains("candidate-199-b", adapter.History);
        Assert.DoesNotContain("user-question-200", adapter.History);
        Assert.DoesNotContain("retained-content-001", adapter.History);
        var retained = new List<AnalysisStoreOutput>();
        await foreach (var output in store.ReadOutputsAsync(revision, default)) retained.Add(output);
        Assert.Equal(201, retained.Count);
        Assert.Equal("candidate-200-b", retained[^1].Claims[^1].Text);
        Assert.Single(await repository.LoadAsync(default));
    }

    [Fact]
    public async Task CancelAndReleaseAsync_WorkerIgnoresCancellation_WaitsForSafeReleaseAndPersistsCancellation()
    {
        using var files = new DesktopTestContext();
        var request = await AnalysisSourceAsync(files);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution => new Worker(async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            using var lease = await execution.EnterInferenceAsync(token);
            entered.SetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            return new("Completed", null, "", []);
        }));
        var work = adapter.AnalyzeAsync(request, new Progress<string>(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = adapter.CancelAndReleaseAsync();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(work.IsCompleted);
            Assert.False(stopping.IsCompleted);
        }
        finally { release.TrySetResult(); }

        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Null(adapter.ReleaseError);
        Assert.Equal(AnalysisJobStatus.Cancelled, Assert.Single(await repository.LoadAsync(default)).Status);
        Assert.Null((await store.LoadAsync(adapter.Jobs[0].Request.Identity, default)).Completed);
    }

    private static async Task<TranscriptAnalysisRequest> AnalysisSourceAsync(DesktopTestContext files)
    {
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        return new(Guid.NewGuid(), new(text, subtitles), "English", null);
    }

    [Fact]
    public async Task UpdateAsync_StaleSnapshotAfterAttemptLanguageRetention_DoesNotDuplicateOwnedTranscript()
    {
        using var files = new DesktopTestContext();
        var history = new SoundRecorder.Infrastructure.Persistence.JsonRecordingHistory(Path.Combine(files.Directory, "history.json"));
        var attempt = Guid.NewGuid();
        var stale = files.Recording(Guid.NewGuid()) with
        {
            AudioDeleted = true,
            TranscriptOutputs = [new(attempt, new("owned.txt", "owned.srt"))]
        };
        await history.AddAsync(stale, default);
        await history.RegisterOutputsAsync(stale.Id, new(attempt, new("owned.txt", "owned.srt")) { Language = "English" }, default);

        await history.UpdateAsync(stale, default);

        var output = Assert.Single(Assert.Single(await history.LoadAsync(default)).TranscriptOutputs);
        Assert.Equal(attempt, output.JobId);
        Assert.Equal("English", output.Language);
    }

    [Fact]
    public async Task SelectAnalysisModelDirectoryCommand_SetupRequired_VerifiesExplicitlyWithoutStartingInference()
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var job = files.Job(files.Recording(Guid.NewGuid()), new(text, subtitles));
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var setup = new ReadySetup { State = AnalysisModelReadinessState.SetupRequired };
        var adapter = new PersistentTranscriptAnalysis(store, repository, setup, execution =>
            new AnalysisProcessingService(store, execution.Revision, files.Directory, new Generation(), new Vectors(),
                AnalysisProcessingMode.Summary));
        var vm = Reviewed(files, job, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        Assert.False(vm.CanAnalyzeTranscript);
        Assert.Empty(adapter.Jobs);
        Assert.Equal(0, setup.SelectCalls);
        vm.AnalysisModelDirectory = "explicit-selected-cache";
        await vm.SelectAnalysisModelDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(1, setup.SelectCalls);
        Assert.Empty(adapter.Jobs);
        Assert.True(vm.CanAnalyzeTranscript);
        vm.Language = "Brazilian Portuguese";
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.Equal("explicit-selected-cache", Assert.Single(adapter.Jobs).Request.ModelDirectory);
        Assert.Equal("English", adapter.Jobs[0].Request.Identity.Language);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("close")]
    [InlineData("selection")]
    [InlineData("journal failure")]
    public async Task AnalysisLifecycle_NativeLeaseHeld_CancelsBeforeNextCallAndWaitsForOwnedRelease(string operation)
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var job = files.Job(files.Recording(Guid.NewGuid()), new(text, subtitles));
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCalls = 0;
        CancellationToken observed = default;
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution => new Worker(async token =>
        {
            observed = token;
            using var registration = token.Register(() => cancelled.TrySetResult());
            using (await execution.EnterInferenceAsync(token))
            {
                entered.TrySetResult();
                await release.Task;
            }
            using (await execution.EnterInferenceAsync(token)) nextCalls++;
            return new("Completed", null, "", []);
        }));
        var vm = Reviewed(files, job, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        var analysis = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (operation == "journal failure") File.Delete(Path.Combine(files.Directory, "journal.sqlite"));
        vm.PermissionConfirmed = true;
        vm.SelectedMicrophoneId = "microphone";
        vm.DestinationFolder = files.Directory;
        Task action;
        if (operation is "capture" or "journal failure") action = vm.StartRecordingAsync(false);
        else if (operation == "close") action = vm.PrepareViewCloseAsync();
        else
        {
            vm.SelectedRecording = files.Recording(Guid.NewGuid());
            action = Task.CompletedTask;
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observed.IsCancellationRequested);
        Assert.Null(files.Engine.Request);
        Assert.True(vm.IsAnalyzing);
        if (operation != "selection") Assert.False(action.IsCompleted);
        release.SetResult();
        await Task.WhenAll(analysis, action).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, nextCalls);
        Assert.Empty(vm.AnalysisClaims);
        if (operation == "journal failure")
            Assert.Contains("unavailable", vm.AnalysisStatus, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Equal(AnalysisJobStatus.Cancelled, Assert.Single(await repository.LoadAsync(default)).Status);
        if (operation is "capture" or "journal failure")
        {
            Assert.NotNull(files.Engine.Request);
            await vm.StopAsync(false);
        }
        else Assert.Null(files.Engine.Request);
    }

    [Fact]
    public Task Composition_MissingExistingAnalysisJournal_PreservesStoreAndKeepsCaptureAvailable() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var files = new DesktopTestContext();
            var database = Path.Combine(files.Directory, "analysis.sqlite");
            _ = SqliteTranscriptAnalysisStore.Create(database);
            var recording = files.Recording(Guid.NewGuid());
            await File.WriteAllTextAsync(recording.AudioPath, "owned disposable audio fixture");
            await new SoundRecorder.Infrastructure.Persistence.JsonRecordingHistory(Path.Combine(files.Directory, "history.json"))
                .AddAsync(recording, default);
            var bytes = await File.ReadAllBytesAsync(database);
            var window = new MainWindow(files.Directory, new AppLoggerFactory(), () => new DesktopDialogs(),
                () => new DesktopEnvironment(), _ => new MainWindowTests.DesktopLifecycleEvents(),
                files, (_, _, _) => files.Transcriber);
            try
            {
                var vm = Assert.IsType<MainViewModel>(window.DataContext);
                await vm.InitializeCommand.ExecuteAsync(null);
                Assert.True(vm.StartCommand.CanExecute(null));
                Assert.False(vm.CanAnalyzeTranscript);
                Assert.Contains("unavailable", vm.AnalysisReadiness, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(Path.Combine(files.Directory, "analysis-jobs.sqlite")));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(database));
                vm.SelectedRecording = vm.Recordings.Single(item => item.Id == recording.Id);
                await vm.DeleteRecordingAsync(true);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Contains("Analysis storage unavailable", vm.Message);
            }
            finally { await window.RequestExitAsync(); }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContinueAnalysisCommand_RecoveredRunningJob_RequiresExplicitRequestBeforeCompatibleCompletion(bool resume)
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var transcription = files.Job(files.Recording(Guid.NewGuid()), new(text, subtitles));
        var target = new TranscriptAnalysisRequest(transcription.Id, transcription.Outputs!, transcription.Language, null);
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var identity = await AnalysisProcessingNative.PinAsync(target, default);
        var revision = await store.BeginAsync(identity, default);
        var job = new AnalysisJob(Guid.NewGuid(), new(target, identity, "original-pinned-directory"),
            DateTimeOffset.UtcNow, AnalysisJobStatus.Running, revision);
        await repository.SaveAsync([job], default);
        var calls = 0;
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution =>
        {
            calls++;
            Assert.Equal("original-pinned-directory", execution.Request.ModelDirectory);
            return new AnalysisProcessingService(store, execution.Revision, files.Directory, new Generation(), new Vectors(),
                AnalysisProcessingMode.Summary, enterInferenceAsync: execution.EnterInferenceAsync);
        });
        var vm = Reviewed(files, transcription, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        await vm.RestoreAnalysisCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        Assert.Equal(AnalysisJobStatus.Interrupted, Assert.Single(vm.AnalysisJobs).Status);
        Assert.Empty(vm.AnalysisClaims);
        vm.SelectedAnalysisJob = vm.AnalysisJobs[0];
        Assert.True(vm.CanContinueAnalysis);

        await (resume ? vm.ResumeAnalysisCommand : vm.RetryAnalysisCommand).ExecuteAsync(null);

        Assert.Equal(resume, adapter.Jobs[^1].Id == job.Id);
        Assert.Equal(AnalysisJobStatus.Complete, adapter.Jobs[^1].Status);
        Assert.Equal(1, calls);
        Assert.Contains("Ana sends the draft.", vm.AnalysisEvidence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreAnalysisCommand_MissingOrChangedExactSource_DoesNotExposeStaleCitations(bool missing)
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var job = files.Job(files.Recording(Guid.NewGuid()), new(text, subtitles));
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution =>
            new AnalysisProcessingService(store, execution.Revision, files.Directory, new Generation(), new Vectors(),
                AnalysisProcessingMode.Summary));
        var vm = Reviewed(files, job, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.AnalysisClaims);
        if (missing) File.Delete(text);
        else await File.WriteAllTextAsync(text, "[00:00:01.000] Different exact bytes.");

        await vm.RestoreAnalysisCommand.ExecuteAsync(null);

        Assert.Empty(vm.AnalysisClaims);
        Assert.DoesNotContain("Ana sends the draft.", vm.AnalysisEvidence);
        Assert.Contains("Exact source/history unavailable", vm.AnalysisStatus);
        Assert.Equal(job.Id, vm.CompletedTranscriptJobId);
        Assert.Single(await repository.LoadAsync(default));
    }

    [Fact]
    public async Task DeleteDiagnosticsAsync_OwnedAttempt_RemovesStoreAndJournalButRetainsOtherAnalysisAndSources()
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution =>
            new AnalysisProcessingService(store, execution.Revision, files.Directory, new Generation(), new Vectors(),
                AnalysisProcessingMode.Summary));
        var owned = new TranscriptAnalysisRequest(Guid.NewGuid(), new(text, subtitles), "English", null);
        var other = owned with { AttemptId = Guid.NewGuid() };
        await adapter.AnalyzeAsync(owned, new Progress<string>(), default);
        await adapter.AnalyzeAsync(other, new Progress<string>(), default);

        await ((ILocalAnalysisDiagnostics)(object)adapter).DeleteDiagnosticsAsync([owned.AttemptId], default);

        Assert.Equal(other.AttemptId, Assert.Single(await repository.LoadAsync(default)).Request.Identity.AttemptId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.LoadAsync(AnalysisProcessingNative.PinAsync(owned, default).GetAwaiter().GetResult(), default));
        Assert.Equal("Completed", (await adapter.RestoreAsync(other, default)).Status);
        Assert.Equal("[00:00:01.000] Ana sends the draft.", await File.ReadAllTextAsync(text));
        Assert.True(File.Exists(subtitles));
    }

    [Fact]
    public async Task AskTranscriptCommand_ExplicitLocalLookup_PersistsCoverageAndDiscussionAfterRestart()
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var job = files.Job(files.Recording(Guid.NewGuid()), new(text, subtitles));
        var store = SqliteTranscriptAnalysisStore.Create(Path.Combine(files.Directory, "analysis.sqlite"));
        var repository = AnalysisQueueRepository.Create(Path.Combine(files.Directory, "journal.sqlite"));
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(), execution =>
            new AnalysisProcessingService(store, execution.Revision, files.Directory, new Generation(), new Vectors(),
                execution.Request.Mode == AnalysisJobMode.LocalQuestion
                    ? AnalysisProcessingMode.LocalQuestion : AnalysisProcessingMode.GlobalQuestion));
        var vm = Reviewed(files, job, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        vm.AnalysisQuestion = "Who sends the draft?";
        vm.AnalysisUseLocalLookup = true;
        await vm.AskTranscriptCommand.ExecuteAsync(null);

        Assert.Equal(AnalysisJobMode.LocalQuestion, Assert.Single(adapter.Jobs).Request.Mode);
        Assert.Contains("Retrieval-limited local lookup", vm.AnalysisHistory);
        var restored = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(),
            _ => throw new InvalidOperationException("No inference on restart."));
        await restored.RecoverAsync();
        var result = await restored.RestoreAsync(new(job.Id, job.Outputs!, job.Language, null), default);
        Assert.Contains("Who sends the draft?", restored.History);
        Assert.Contains("Ana sends the draft.", Assert.Single(Assert.Single(result.Claims).Evidence).Text);
    }

    [Fact]
    public async Task GenerateSummaryCommand_ReopenedDesktop_RestoresExactAttemptOutputWithoutInference()
    {
        using var files = new DesktopTestContext();
        var text = Path.Combine(files.Directory, "source.txt");
        var subtitles = Path.Combine(files.Directory, "source.srt");
        await File.WriteAllTextAsync(text, "[00:00:01.000] Ana sends the draft.");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:02,000\nAna sends the draft.\n");
        var recording = files.Recording(Guid.NewGuid());
        var job = files.Job(recording, new(text, subtitles));
        var database = Path.Combine(files.Directory, "analysis.sqlite");
        var journal = Path.Combine(files.Directory, "analysis-jobs.sqlite");
        var store = SqliteTranscriptAnalysisStore.Create(database);
        var repository = AnalysisQueueRepository.Create(journal);
        var calls = 0;
        var adapter = new PersistentTranscriptAnalysis(store, repository, new ReadySetup(),
            execution =>
            {
                calls++;
                return new AnalysisProcessingService(store, execution.Revision, files.Directory,
                    new Generation(), new Vectors(), AnalysisProcessingMode.Summary,
                    enterInferenceAsync: execution.EnterInferenceAsync);
            });
        var vm = Reviewed(files, job, adapter);
        await vm.InitializeAnalysisAsync();
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        await vm.GenerateSummaryCommand.ExecuteAsync(null);

        Assert.Single(adapter.Jobs);
        Assert.Equal(AnalysisJobStatus.Complete, adapter.Jobs[0].Status);
        Assert.Contains("Ana sends the draft.", vm.AnalysisEvidence);
        Assert.Equal(1, calls);

        var reopened = new PersistentTranscriptAnalysis(SqliteTranscriptAnalysisStore.Open(database),
            AnalysisQueueRepository.Open(journal), new ReadySetup(),
            _ => throw new InvalidOperationException("Restoration must not infer."));
        var restored = Reviewed(files, job, reopened);
        await restored.InitializeAnalysisAsync();
        await restored.ViewTranscriptCommand.ExecuteAsync(null);
        await restored.RestoreAnalysisCommand.ExecuteAsync(null);

        Assert.Equal(vm.AnalysisEvidence, restored.AnalysisEvidence);
        Assert.Equal(AnalysisJobStatus.Complete, Assert.Single(restored.AnalysisJobs).Status);
        Assert.Contains("UNVERIFIED", restored.AnalysisStatus);
        Assert.Equal(await AnalysisProcessingNative.PinAsync(new(job.Id, job.Outputs!, job.Language, null), default),
            reopened.Jobs[0].Request.Identity);
    }

    private static MainViewModel Reviewed(DesktopTestContext files, TranscriptionJob job,
        ILocalTranscriptAnalysis analysis)
    {
        var vm = new MainViewModel(files.Recorder, files.Engine, files.Queue, files.Models, files.History,
            new RecoveryManager(files.Recovery), files, files.Logger, analysis: analysis);
        vm.Recordings.Add(job.Recording);
        vm.Jobs.Add(job);
        vm.SelectedJob = job;
        return vm;
    }

    private sealed class ReadySetup : IAnalysisModelSetup
    {
        public AnalysisModelReadinessState State { get; set; } = AnalysisModelReadinessState.Ready;
        public string? Directory { get; set; } = "pinned-fixture-directory";
        public string? Error { get; set; }
        public Func<string?, IProgress<AnalysisModelSetupProgress>?, CancellationToken, Task<string>>? Install { get; init; }
        public int SelectCalls { get; private set; }
        public AnalysisModelProfile Profile => new("fixture", "controlled CPU", []);
        public Task<AnalysisModelReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AnalysisModelReadiness(State, Directory, Error));
        public Task<string> ImportAsync(string packagePath, IProgress<AnalysisModelSetupProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Install?.Invoke(packagePath, progress, cancellationToken) ?? throw new NotSupportedException();
        public Task<string> DownloadAsync(IProgress<AnalysisModelSetupProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Install?.Invoke(null, progress, cancellationToken) ?? throw new NotSupportedException();
        public Task SelectDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        {
            SelectCalls++;
            Directory = directory;
            State = AnalysisModelReadinessState.Ready;
            return Task.CompletedTask;
        }
    }

    private sealed class Generation : IGenerationBackend
    {
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string input) => input.Length / 4;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken token) =>
            Task.FromResult(new GenerationResult("""
                {"claims":[{"kind":"Action","statement":"Send the draft","owner":{"state":"Stated","value":"Ana"},
                "deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}
                """, GenerationFinish.Completed, 50));
    }

    private sealed class Vectors : IEmbeddingBackend
    {
        public int CountTokens(string text) => 10;
        public Task<float[]> EmbedAsync(string text, CancellationToken token)
        {
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(vector);
        }

    }

    private sealed class Worker(Func<CancellationToken, Task<TranscriptAnalysisResult>> run) : ILocalTranscriptAnalysis
    {
        public Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
            IProgress<string> progress, CancellationToken cancellationToken) => run(cancellationToken);
    }
}
