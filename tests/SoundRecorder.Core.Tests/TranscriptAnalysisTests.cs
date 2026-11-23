using Models.Harness;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Analysis;
using SoundRecorder.Desktop;

namespace SoundRecorder.Core.Tests;

public sealed class TranscriptAnalysisTests
{
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
        await File.WriteAllTextAsync(path, "Ana will send the draft.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "saved subtitle fixture");
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
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "saved subtitle fixture");
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
        await File.WriteAllTextAsync(path, "Ana will send the draft.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".srt"), "saved subtitle fixture");
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

    private sealed class CandidateBackend(GenerationFinish finish = GenerationFinish.Completed) : IGenerationBackend
    {
        public string Prompt { get; private set; } = "";
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt) => prompt.Length / 4;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            return Task.FromResult(new GenerationResult("""
                {"claims":[{"kind":"Action","statement":"Ana will send the draft","owner":{"state":"Stated","value":"Ana"},"deadline":{"state":"Stated","value":"Friday"},"qualifiers":[],"references":[0]}],"limitation":null}
                """, finish, 60));
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
        public int CountTokens(string input) => input.Length / 4;
        public Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
        {
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }
}
