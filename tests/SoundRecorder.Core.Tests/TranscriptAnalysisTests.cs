using Models.Harness;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Analysis;
using SoundRecorder.Desktop;
using SoundRecorder.Infrastructure.Transcription;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

public sealed class TranscriptAnalysisTests
{
    [Fact]
    public async Task LoadAsync_RegisteredLegacyOutput_DoesNotRewriteHistoryDuringReview()
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.NewGuid());
        await File.WriteAllBytesAsync(recording.AudioPath, [1]);
        var path = Path.Combine(context.Directory, "legacy-language.json");
        var history = new JsonRecordingHistory(path);
        await history.AddAsync(recording, default);
        context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Revisão em português.")];
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(),
            context.Repository, history: history);
        var job = await queue.RetryAsync(recording with { Language = "Brazilian Portuguese" }, default);
        await queue.WaitForIdleAsync(default);
        Assert.Equal(TranscriptionJobStatus.Succeeded, job.Status);
        Assert.Equal("Brazilian Portuguese", job.Language);
        context.Repository.Items.Add(job);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new[]
        {
            recording with { TranscriptOutputs = [new(job.Id, job.Outputs!)] }
        }));
        var before = await File.ReadAllBytesAsync(path);

        var entries = await new RecordingLibrary(new JsonRecordingHistory(path), context.Repository).LoadAsync(default);

        var owned = Assert.Single(Assert.Single(entries).Recording.TranscriptOutputs);
        Assert.Equal(job.Id, owned.JobId);
        Assert.Null(owned.Language);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("time")]
    [InlineData("number")]
    [InlineData("line ending")]
    public async Task AnalyzeAsync_InvalidSavedPair_DoesNotInferAndReleasesResourcesForExplicitRetry(string corruption)
    {
        using var context = new DesktopTestContext();
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(context.Recording(Guid.NewGuid()), "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Ana sends the draft.")]), default);
        var original = await File.ReadAllTextAsync(outputs.SubtitlePath);
        var corrupt = corruption switch
        {
            "text" => original.Replace("Ana", "Bruno"),
            "time" => original.Replace("00:00:02,000", "00:00:00,000"),
            "number" => original.Replace("1" + Environment.NewLine, "2" + Environment.NewLine),
            _ => original.Replace("\r\n", "\r")
        };
        await File.WriteAllTextAsync(outputs.SubtitlePath, corrupt);
        var backend = new CandidateBackend();
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, backend);
        var attempt = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.AnalyzeAsync(new(attempt, outputs, "English", null), new Progress<string>(), default));
        Assert.Empty(backend.Prompt);
        await File.WriteAllTextAsync(outputs.SubtitlePath, original);
        var result = await adapter.AnalyzeAsync(new(attempt, outputs, "English", null), new Progress<string>(), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Completed", result.Status);
        await adapter.DeleteDiagnosticsAsync([attempt], default);
        Assert.False(Directory.Exists(Path.Combine(context.Directory, attempt.ToString("N"))));
    }

    [Fact]
    public async Task AnalyzeAsync_UnicodeQuestionRanges_PreserveCompleteScalarsInNativeTokenPacking()
    {
        using var context = new DesktopTestContext();
        var original = "a" + string.Concat(Enumerable.Repeat("😀", 800)) + " qualification remains.";
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(context.Recording(Guid.NewGuid()), "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), original)]), default);
        var embeddings = new EmbeddingBackend();
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend(), embeddings);
        var result = await adapter.AnalyzeAsync(new(Guid.NewGuid(), outputs, "English", "What was reported?"),
            new Progress<string>(), default);
        Assert.NotEmpty(result.Claims);
        Assert.All(embeddings.Inputs, input => Assert.True(embeddings.CountTokens(input) <= 512));
        Assert.All(result.Claims.SelectMany(claim => claim.Evidence), evidence =>
            Assert.Contains(evidence.Text, "[00:00:00.000] " + original));
    }

    [Theory]
    [InlineData("First line.\nSecond line.")]
    [InlineData("First line.\r\nSecond line.")]
    public async Task AnalyzeAsync_MultilineSavedCue_PreservesOriginalTextWithoutNormalizingIt(string original)
    {
        using var context = new DesktopTestContext();
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(context.Recording(Guid.NewGuid()), "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(4), original)]), default);
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend());
        var result = await adapter.AnalyzeAsync(new(Guid.NewGuid(), outputs, "English", null), new Progress<string>(), default);
        Assert.Equal("[00:00:00.000] " + original, Assert.Single(Assert.Single(result.Claims).Evidence).Text);
    }

    [Fact]
    public async Task AnalyzeAsync_UnknownAttemptLanguage_RequiresMetadataInsteadOfGuessing()
    {
        using var context = new DesktopTestContext();
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(context.Recording(Guid.NewGuid()), "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Ana sends the draft.")]), default);
        var backend = new CandidateBackend();
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, backend);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.AnalyzeAsync(new(Guid.NewGuid(), outputs, "source language", null), new Progress<string>(), default));
        Assert.Empty(backend.Prompt);
    }

    [Fact]
    public async Task StartRecordingAsync_CloseWhileWaitingForAnalysisRelease_DoesNotStartCaptureAfterCloseIntent()
    {
        using var context = new DesktopTestContext();
        var adapter = new HeldAnalysis();
        var vm = await ReviewedAsync(context, adapter);
        vm.PermissionConfirmed = true;
        vm.SelectedMicrophoneId = "microphone";
        vm.DestinationFolder = context.Directory;
        var analysis = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var capture = vm.StartRecordingAsync(false);
        var close = vm.PrepareViewCloseAsync();
        adapter.Release.SetResult();
        await Task.WhenAll(analysis, capture, close);
        Assert.Null(context.Engine.Request);
        Assert.False(vm.StartCommand.CanExecute(null));
        await vm.RetranscribeSelectedRecordingCommand.ExecuteAsync(null);
        Assert.Empty(context.Queue.Jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRecordingAsync_FullDeletion_RemovesPrivateAnalysisAndSavedArtifactsTogether(bool lockedDiagnostic)
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.NewGuid());
        await File.WriteAllBytesAsync(recording.AudioPath, [1]);
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Ana sends the draft.")]), default);
        var attempt = Guid.NewGuid();
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "history.json"));
        await history.AddAsync(recording with
        {
            TranscriptOutputs = [new(attempt, outputs) { Language = "English" }]
        }, default);
        var adapter = new LocalTranscriptAnalysis("unused", Path.Combine(context.Directory, "analysis"), new CandidateBackend());
        var vm = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger,
            library: new RecordingLibrary(history, context.Repository, cancelRecordingAsync: context.Queue.CancelRecordingAsync,
                deleteJobAsync: context.Queue.DeleteJobAsync,
                releasePlaybackAsync: (_, _) => Task.CompletedTask), analysis: adapter);
        var reviewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.CompletedTranscriptJobId) && vm.CompletedTranscriptJobId == attempt)
                reviewed.TrySetResult();
        };
        var retained = Assert.Single(await history.LoadAsync(default));
        vm.Recordings.Add(retained);
        vm.SelectedRecording = retained;
        await reviewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.True(File.Exists(vm.AnalysisArtifact));
        if (lockedDiagnostic)
        {
            using (File.Open(vm.AnalysisArtifact, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await vm.DeleteRecordingAsync(true);
                Assert.Contains("Deletion could not finish", vm.Message);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.True(File.Exists(outputs.TextPath));
                Assert.True(File.Exists(outputs.SubtitlePath));
            }
        }
        await vm.DeleteRecordingAsync(true);
        Assert.False(Directory.Exists(Path.Combine(context.Directory, "analysis", attempt.ToString("N"))));
        Assert.False(File.Exists(outputs.TextPath));
        Assert.False(File.Exists(outputs.SubtitlePath));
        Assert.False(File.Exists(recording.AudioPath));
    }

    [Fact]
    public async Task DeleteDiagnosticsAsync_ExplicitOwnedAttempt_RemovesItsPrivateSourcesButKeepsOtherAttempts()
    {
        using var context = new DesktopTestContext();
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(context.Recording(Guid.NewGuid()), "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Ana sends the draft.")]), default);
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend());
        var owned = Guid.NewGuid();
        var other = Guid.NewGuid();
        var first = await adapter.AnalyzeAsync(new(owned, outputs, "English", null), new Progress<string>(), default);
        var second = await adapter.AnalyzeAsync(new(other, outputs, "English", null), new Progress<string>(), default);
        await adapter.DeleteDiagnosticsAsync([owned], default);
        Assert.False(Directory.Exists(Path.Combine(context.Directory, owned.ToString("N"))));
        Assert.True(File.Exists(second.LedgerPath));
        Assert.True(File.Exists(outputs.TextPath));
        Assert.True(File.Exists(outputs.SubtitlePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateSummaryCommand_JobHistoryDeleted_RetainsExactAttemptLanguageAcrossReload(bool legacyMetadata)
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.NewGuid());
        await File.WriteAllBytesAsync(recording.AudioPath, [1]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "history.json"));
        await history.AddAsync(recording, default);
        context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Revisão em português.")];
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(),
            context.Repository, history: history);
        var job = await queue.RetryAsync(recording with { Language = "Brazilian Portuguese" }, default);
        await queue.WaitForIdleAsync(default);
        if (legacyMetadata)
        {
            var legacy = recording with { TranscriptOutputs = [new(job.Id, job.Outputs!)] };
            await File.WriteAllTextAsync(Path.Combine(context.Directory, "history.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { legacy }));
        }
        await queue.DeleteJobAsync(job.Id, default);
        var reloaded = new JsonRecordingHistory(Path.Combine(context.Directory, "history.json"));
        var retained = Assert.Single(await reloaded.LoadAsync(default));
        var analysis = new HeldAnalysis();
        var vm = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, reloaded,
            new RecoveryManager(context.Recovery), context, context.Logger,
            library: new RecordingLibrary(reloaded), analysis: analysis);
        var reviewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.CompletedTranscriptJobId) && vm.CompletedTranscriptJobId == job.Id)
                reviewed.TrySetResult();
        };
        vm.Recordings.Add(retained);
        vm.SelectedRecording = retained;
        await reviewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var work = vm.GenerateSummaryCommand.ExecuteAsync(null);
        try
        {
            await analysis.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Brazilian Portuguese", analysis.Request!.Language);
            Assert.Equal(job.Id, analysis.Request.AttemptId);
        }
        finally { analysis.Release.TrySetResult(); await work; }
    }

    [Fact]
    public async Task AnalyzeAsync_LongQuestionSources_EmbedsTheQuestionOnceAndHonorsNativeDocumentTokenLimits()
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.NewGuid());
        var sentence = string.Join(" ", Enumerable.Repeat("Qualificação importante sobre a ação proposta.", 30));
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, "Brazilian Portuguese",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), sentence),
             new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "O prazo ainda não foi aprovado.")]), default);
        var embeddings = new EmbeddingBackend();
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend(), embeddings);
        var result = await adapter.AnalyzeAsync(new(Guid.NewGuid(), outputs, "Brazilian Portuguese", "Quais são todas as ações?"),
            new Progress<string>(), default);
        Assert.Equal(1, embeddings.Inputs.Count(input => input.StartsWith("search_query: ", StringComparison.Ordinal)));
        Assert.All(embeddings.Inputs, input => Assert.True(embeddings.CountTokens(input) <= 512));
        Assert.True(embeddings.Inputs.Count(input => input.StartsWith("search_document: ", StringComparison.Ordinal)) > 2);
        Assert.Contains("not exhaustive", result.Detail);
        Assert.All(result.Claims.SelectMany(claim => claim.Evidence), evidence =>
            Assert.Contains(evidence.Text, $"[00:00:00.000] {sentence}\r\n[00:00:03.000] O prazo ainda não foi aprovado."));
    }

    [Fact]
    public async Task AnalyzeAsync_SavedCues_PreservesTheSelectedOriginalCueAndBothTimestamps()
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.NewGuid());
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, "English",
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Ana sends the draft."),
             new(TimeSpan.FromMilliseconds(3200), TimeSpan.FromMilliseconds(4900), "Bruno will review it tomorrow.")]), default);
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend(reference: 1));
        var result = await adapter.AnalyzeAsync(new(Guid.NewGuid(), outputs, "English", null), new Progress<string>(), default);
        Assert.Equal("Completed", result.Status);
        var evidence = Assert.Single(Assert.Single(result.Claims).Evidence);
        Assert.Equal("[00:00:03.200] Bruno will review it tomorrow.", evidence.Text);
        Assert.Equal(3200, evidence.StartMs);
        Assert.Contains("00:00:04,900", evidence.Reference);
    }

    [Fact]
    public async Task GenerateSummaryCommand_TranscriptionRunning_DoesNotStartCompetingInference()
    {
        using var context = new DesktopTestContext();
        var adapter = new HeldAnalysis();
        var vm = await ReviewedAsync(context, adapter);
        var recording = vm.SelectedRecording!;
        await File.WriteAllBytesAsync(recording.AudioPath, [1]);
        context.Transcriber.Block = true;
        try
        {
            await context.Queue.EnqueueAsync(recording, default);
            await context.Transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.CanAnalyzeTranscript);
            Assert.False(vm.GenerateSummaryCommand.CanExecute(null));
            await vm.GenerateSummaryCommand.ExecuteAsync(null);
            Assert.False(adapter.Entered.Task.IsCompleted);
        }
        finally
        {
            context.Transcriber.Release.TrySetResult();
            await context.Queue.WaitForIdleAsync(default);
        }
    }

    [Fact]
    public async Task AskTranscriptCommand_SelectedAttempt_ReturnsReadableCandidateAndOriginalEvidence()
    {
        using var context = new DesktopTestContext();
        var adapter = new HeldAnalysis();
        var vm = await ReviewedAsync(context, adapter);
        vm.AnalysisQuestion = "Who owns the draft?";
        var progressSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.AnalysisStatus) && vm.AnalysisStatus == "Controlled analysis phase")
                progressSeen.TrySetResult();
        };
        Assert.True(vm.AskTranscriptCommand.CanExecute(null));
        var work = vm.AskTranscriptCommand.ExecuteAsync(null);
        await Task.WhenAll(adapter.Entered.Task, progressSeen.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Who owns the draft?", adapter.Request!.Question);
        Assert.Equal(vm.CompletedTranscriptJobId, adapter.Request.AttemptId);
        adapter.Release.SetResult();
        await work;
        Assert.Equal("Old candidate", Assert.Single(vm.AnalysisClaims).Text);
        Assert.Contains("Original text", vm.AnalysisEvidence);
        Assert.Contains("00:00:01.000", vm.AnalysisEvidence);
        Assert.Contains(adapter.Request.AttemptId.ToString("N"), vm.AnalysisOwnership);
        Assert.Equal("owned-ledger", vm.AnalysisArtifact);
        Assert.Contains("UNVERIFIED", vm.AnalysisStatus);
    }

    [Fact]
    public async Task AnalyzeAsync_EmbeddingCacheMissing_ExplainsExplicitConfigurationWithoutDownloading()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "configuration.txt");
        await File.WriteAllTextAsync(path, "[00:00:01.000] Ana will send the draft.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "1\n00:00:01,000 --> 00:00:02,000\nAna will send the draft.\n");
        var adapter = new LocalTranscriptAnalysis("", context.Directory, new CandidateBackend());
        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            adapter.AnalyzeAsync(new(Guid.NewGuid(), new(path, Path.ChangeExtension(path, ".srt")),
                "English", "Who will send the draft?"), new Progress<string>(), default));
        Assert.Contains("SOUNDRECORDER_ANALYSIS_MODELS", error.Message);
        Assert.Contains("No models are downloaded", error.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_Question_UsesRetrievedOriginalExcerptsWithoutInventingAnAnswer()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "question.txt");
        const string original = "[00:00:01.000] Ana will send the draft on Friday.";
        await File.WriteAllTextAsync(path, original);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "1\n00:00:01,000 --> 00:00:02,000\nAna will send the draft on Friday.\n");
        var backend = new CandidateBackend();
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, backend, new EmbeddingBackend());
        var result = await adapter.AnalyzeAsync(new(Guid.NewGuid(), new(path, Path.ChangeExtension(path, ".srt")),
            "Brazilian Portuguese", "Who will send the draft?"), new Progress<string>(), default);
        Assert.Equal("Completed", result.Status);
        Assert.Contains("Who will send the draft?", backend.Prompt);
        Assert.Contains("pt-BR", backend.Prompt);
        Assert.Equal(original, Assert.Single(Assert.Single(result.Claims).Evidence).Text);
    }

    [Fact]
    public async Task AnalyzeAsync_TokenLimit_DoesNotRepairOrPresentUncommittedOutput()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "limited.txt");
        await File.WriteAllTextAsync(path, "[00:00:01.000] Ana will send the draft.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "1\n00:00:01,000 --> 00:00:02,000\nAna will send the draft.\n");
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory,
            new CandidateBackend(GenerationFinish.TokenLimit));
        var result = await adapter.AnalyzeAsync(new(Guid.NewGuid(), new(path, Path.ChangeExtension(path, ".srt")),
            "English", null), new Progress<string>(), default);
        Assert.Equal("TokenLimited", result.Status);
        Assert.Empty(result.Claims);
        Assert.Contains("not an answer or summary", result.Detail);
    }

    [Fact]
    public async Task ViewTranscriptCommand_AnotherSuccessfulAttempt_CancelsWithoutImplicitRetargeting()
    {
        using var context = new DesktopTestContext();
        var analysis = new HeldAnalysis();
        var vm = await ReviewedAsync(context, analysis);
        var originalAttempt = vm.CompletedTranscriptJobId;
        var work = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await analysis.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var path = Path.Combine(context.Directory, "another-attempt.txt");
        await File.WriteAllTextAsync(path, "Different transcript attempt.");
        var next = context.Job(vm.SelectedRecording!, new(path, Path.ChangeExtension(path, ".srt")));
        vm.Jobs.Add(next);
        vm.SelectedJob = next;
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        Assert.Equal(next.Id, vm.CompletedTranscriptJobId);
        Assert.Equal(originalAttempt, analysis.Request!.AttemptId);
        Assert.True(analysis.Token.IsCancellationRequested);
        analysis.Release.SetResult();
        await work;
        Assert.Empty(vm.AnalysisClaims);
    }

    [Fact]
    public async Task StartRecordingAsync_AnalysisInNativeCall_CancelsAndWaitsWithoutStartingCaptureEarly()
    {
        using var context = new DesktopTestContext();
        var analysis = new HeldAnalysis();
        var vm = await ReviewedAsync(context, analysis);
        vm.PermissionConfirmed = true;
        vm.SelectedMicrophoneId = "microphone";
        vm.DestinationFolder = context.Directory;
        var work = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await analysis.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var capture = vm.StartRecordingAsync(false);
        Assert.True(analysis.Token.IsCancellationRequested);
        Assert.Null(context.Engine.Request);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanAnalyzeTranscript);
        analysis.Release.SetResult();
        await Task.WhenAll(work, capture);
        Assert.NotNull(context.Engine.Request);
        Assert.Empty(vm.AnalysisClaims);
        await vm.StopAsync(false);
    }

    [Fact]
    public async Task RetranscribeSelectedRecordingCommand_AnalysisActive_DoesNotQueueCompetingInference()
    {
        using var context = new DesktopTestContext();
        var analysis = new HeldAnalysis();
        var vm = await ReviewedAsync(context, analysis);
        var work = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await analysis.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.RetranscribeSelectedRecordingCommand.ExecuteAsync(null);
        Assert.Empty(context.Queue.Jobs);
        Assert.Contains("Cancel experimental analysis", vm.Message);
        vm.CancelAnalysisCommand.Execute(null);
        Assert.True(vm.IsAnalyzing);
        analysis.Release.SetResult();
        await work;
        Assert.Contains("Cancelled", vm.AnalysisStatus);
        Assert.Empty(vm.AnalysisClaims);
    }

    [Fact]
    public async Task GenerateSummaryCommand_AdapterFails_ShowsActionableFailureAndReleasesExclusion()
    {
        using var context = new DesktopTestContext();
        var vm = await ReviewedAsync(context, new FailingAnalysis());
        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        Assert.Contains("Analysis failed", vm.AnalysisStatus);
        Assert.Contains("Check the local cache", vm.AnalysisStatus);
        Assert.Empty(vm.AnalysisClaims);
        Assert.False(vm.IsAnalyzing);
        Assert.True(vm.CanAnalyzeTranscript);
    }

    [Fact]
    public async Task GenerateSummaryCommand_SelectionChanges_DiscardsOldOutputAndReportsRelease()
    {
        using var context = new DesktopTestContext();
        var analysis = new HeldAnalysis();
        var vm = await ReviewedAsync(context, analysis);
        var work = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await analysis.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedRecording = context.Recording(Guid.NewGuid());
        Assert.True(analysis.Token.IsCancellationRequested);
        Assert.True(vm.IsAnalyzing);
        analysis.Release.SetResult();
        await work;
        Assert.Empty(vm.AnalysisClaims);
        Assert.Empty(vm.AnalysisArtifact);
        Assert.False(vm.IsAnalyzing);
        Assert.Contains("released", vm.AnalysisStatus);
    }

    [Fact]
    public async Task ViewTranscriptCommand_SuccessfulAttempt_EnablesExplicitLocalAnalysis()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "saved.txt");
        await File.WriteAllTextAsync(path, "The draft is due Friday.");
        var recording = context.Recording(Guid.NewGuid());
        var job = context.Job(recording, new(path, Path.ChangeExtension(path, ".srt")));
        var vm = new MainViewModel(context.Recorder, context.Engine, context.Queue,
            context.Models, context.History, new RecoveryManager(context.Recovery), context, context.Logger,
            analysis: new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend()));
        vm.Recordings.Add(recording);
        vm.Jobs.Add(job);
        vm.SelectedJob = job;
        Assert.False(vm.CanAnalyzeTranscript);
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        Assert.True(vm.CanAnalyzeTranscript);
    }

    [Fact]
    public async Task AnalyzeAsync_SavedTranscript_ReturnsReadableCandidateAndExactOriginalEvidence()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "saved.txt");
        const string source = "[00:00:01.000] Ana will send the draft on Friday.";
        await File.WriteAllTextAsync(path, source);
        var subtitles = Path.ChangeExtension(path, ".srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:01,000 --> 00:00:05,000\nAna will send the draft on Friday.\n");
        var adapter = new LocalTranscriptAnalysis("unused", context.Directory, new CandidateBackend());
        var attempt = Guid.NewGuid();

        var result = await adapter.AnalyzeAsync(new(attempt, new(path, subtitles), "English", null),
            new Progress<string>(), default);

        Assert.Equal("Completed", result.Status);
        var claim = Assert.Single(result.Claims);
        Assert.Contains("Ana will send the draft", claim.Text);
        var evidence = Assert.Single(claim.Evidence);
        Assert.Equal(source, evidence.Text);
        Assert.Equal(1000, evidence.StartMs);
        using var ledger = ClaimLedger.Open(result.LedgerPath);
        Assert.Equal(attempt.ToString("N"), ledger.ReadRun().Identity.SuccessfulTranscriptionAttemptId);
    }

    private sealed class CandidateBackend(GenerationFinish finish = GenerationFinish.Completed, int reference = 0) : IGenerationBackend
    {
        public string Prompt { get; private set; } = "";
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt) => prompt.Length / 4;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            return Task.FromResult(new GenerationResult("""
                {"claims":[{"kind":"Action","statement":"Ana will send the draft","owner":{"state":"Stated","value":"Ana"},"deadline":{"state":"Stated","value":"Friday"},"qualifiers":[],"references":[0]}],"limitation":null}
                """.Replace("\"references\":[0]", $"\"references\":[{reference}]"), finish, 60));
        }
    }

    private static async Task<MainViewModel> ReviewedAsync(DesktopTestContext context, ILocalTranscriptAnalysis adapter)
    {
        var path = Path.Combine(context.Directory, "reviewed.txt");
        await File.WriteAllTextAsync(path, "[00:00:01.000] Ana will send the draft on Friday.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "saved subtitle fixture");
        var recording = context.Recording(Guid.NewGuid());
        var job = context.Job(recording, new(path, Path.ChangeExtension(path, ".srt")));
        var vm = new MainViewModel(context.Recorder, context.Engine, context.Queue,
            context.Models, context.History, new RecoveryManager(context.Recovery), context, context.Logger,
            analysis: adapter);
        vm.Recordings.Add(recording);
        vm.Jobs.Add(job);
        vm.SelectedJob = job;
        await vm.ViewTranscriptCommand.ExecuteAsync(null);
        return vm;
    }

    private sealed class HeldAnalysis : ILocalTranscriptAnalysis
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public TranscriptAnalysisRequest? Request { get; private set; }
        public async Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
            IProgress<string> progress, CancellationToken token)
        {
            Token = token;
            Request = request;
            progress.Report("Controlled analysis phase");
            Entered.SetResult();
            await Release.Task;
            return new("Completed", null, "owned-ledger", [new("Old candidate", [new("original", 1000, "Original text")])]);
        }

    }

    private sealed class FailingAnalysis : ILocalTranscriptAnalysis
    {
        public Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
            IProgress<string> progress, CancellationToken token) =>
            throw new IOException("The configured model cache is unavailable.");
    }

    private sealed class EmbeddingBackend : IEmbeddingBackend
    {
        public List<string> Inputs { get; } = [];
        public int CountTokens(string input) => new System.Text.UTF8Encoding(false, true).GetByteCount(input) * 2;
        public Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }
}
