using SoundRecorder.Infrastructure.Analysis;
using SoundRecorder.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace SoundRecorder.Desktop;

public sealed partial class MainViewModel
{
    private readonly ILocalTranscriptAnalysis? _analysis;
    private CancellationTokenSource? _analysisCancellation;
    private Task? _analysisTask;
    private long _analysisVersion;
    private int _pendingTranscriptionRequests;

    [ObservableProperty]
    private bool isAnalyzing;

    [ObservableProperty]
    private bool isTranscriptAnalysisExpanded;

    [ObservableProperty]
    private string analysisQuestion = "";

    [ObservableProperty]
    private string analysisStatus = "Select a successful transcript, then explicitly generate a summary or ask a question.";

    [ObservableProperty]
    private string analysisArtifact = "";

    [ObservableProperty]
    private TranscriptAnalysisClaim? selectedAnalysisClaim;

    public ObservableCollection<TranscriptAnalysisClaim> AnalysisClaims { get; } = [];
    public string AnalysisWarning => "EXPERIMENTAL · UNVERIFIED model output. Phi-mini may invent or misinterpret facts, owners and deadlines even with valid citations. Inspect every original excerpt. Not release-approved. Full recording deletion also removes its owned private analysis diagnostics.";
    public string AnalysisOwnership => CompletedTranscriptJobId is { } id
        ? $"Selected successful transcript attempt: {id:N}" : "No successful transcript selected.";
    public string AnalysisEvidence => SelectedAnalysisClaim is null ? "Select a candidate to inspect exact original excerpts." :
        string.Join("\n\n", SelectedAnalysisClaim.Evidence.Select(evidence =>
            $"{evidence.Reference}" +
            (evidence.StartMs is { } ms ? $" · original start marker {TimeSpan.FromMilliseconds(ms):hh\\:mm\\:ss\\.fff}" : "") +
            "\n" + evidence.Text));
    public bool CanAnalyzeTranscript => IsAnalysisAvailable && CanCopyTranscript && AnalysisTarget() is not null &&
        !IsCaptureStartingOrActive && !HasPendingTranscription;
    private bool IsAnalysisAvailable => _analysis is not null && !IsAnalyzing && !_closingView && !IsDeletingRecording;
    private bool IsCaptureStartingOrActive => IsBusy || !IsIdle;
    private bool HasPendingTranscription => IsSelectingModel || _pendingTranscriptionRequests != 0 ||
        _queue.Jobs.Any(IsBlockingJob);
    private static bool IsBlockingJob(TranscriptionJob job) =>
        job.Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running || job.IsTerminationPending;
    private bool CanAskTranscript() => CanAnalyzeTranscript && !string.IsNullOrWhiteSpace(AnalysisQuestion);

    [RelayCommand(CanExecute = nameof(CanAnalyzeTranscript))]
    private Task GenerateSummaryAsync() => BeginAnalysis(null);

    [RelayCommand(CanExecute = nameof(CanAskTranscript))]
    private Task AskTranscriptAsync() => BeginAnalysis(AnalysisQuestion.Trim());

    private Task BeginAnalysis(string? question)
    {
        if (!CanAnalyzeTranscript || AnalysisTarget() is not { } target) return Task.CompletedTask;
        var cancellation = _analysisCancellation = new();
        var version = ++_analysisVersion;
        IsAnalyzing = true;
        AnalysisClaims.Clear();
        SelectedAnalysisClaim = null;
        AnalysisArtifact = "";
        AnalysisStatus = "Preparing local analysis; capture will cancel it and wait for safe release.";
        NotifyAnalysisAvailability();
        return _analysisTask = RunAnalysisAsync(target with { Question = question }, version, cancellation);
    }

    private async Task RunAnalysisAsync(TranscriptAnalysisRequest target, long version, CancellationTokenSource cancellation)
    {
        var progress = new Progress<string>(phase => ReportAnalysisProgress(version, cancellation, phase));
        try
        {
            var result = await _analysis!.AnalyzeAsync(target, progress, cancellation.Token);
            if (!CanPublishAnalysis(version, cancellation)) return;
            PresentAnalysis(result);
        }
        catch (OperationCanceledException)
        {
            SetAnalysisStatus(version, "Cancelled. Native inference has released; no output claimed.");
        }
        catch (Exception exception)
        {
            DesktopLog.TranscriptAnalysisFailed(_logger, exception.GetType().Name);
            SetAnalysisStatus(version, "Analysis failed. " + exception.Message + " Check the local cache/files and explicitly retry.");
        }
        finally
        {
            FinishAnalysis(version, cancellation);
        }
    }

    private bool CanPublishAnalysis(long version, CancellationTokenSource cancellation) =>
        version == _analysisVersion && !cancellation.IsCancellationRequested;

    private void ReportAnalysisProgress(long version, CancellationTokenSource cancellation, string phase)
    {
        if (IsAnalyzing && CanPublishAnalysis(version, cancellation)) AnalysisStatus = phase;
    }

    private void SetAnalysisStatus(long version, string text)
    {
        if (version == _analysisVersion) AnalysisStatus = text;
    }

    private void PresentAnalysis(TranscriptAnalysisResult result)
    {
        AnalysisStatus = $"{result.Status} · semantics UNVERIFIED. {result.Detail}";
        AnalysisArtifact = result.LedgerPath;
        foreach (var claim in result.Claims) AnalysisClaims.Add(claim);
        SelectedAnalysisClaim = AnalysisClaims.FirstOrDefault();
    }

    private void FinishAnalysis(long version, CancellationTokenSource cancellation)
    {
        if (version != _analysisVersion)
            AnalysisStatus = "Previous analysis released. Generate or ask explicitly for the selected transcript.";
        else if (cancellation.IsCancellationRequested)
            AnalysisStatus = "Cancelled. Native inference has released; no output claimed.";
        cancellation.Dispose();
        _analysisCancellation = null;
        IsAnalyzing = false;
        NotifyAnalysisAvailability();
    }

    [RelayCommand]
    private void CancelAnalysis()
    {
        if (_analysisCancellation is null) return;
        _analysisCancellation.Cancel();
        AnalysisStatus = "Cancellation requested; waiting for a native safe boundary. Capture and new jobs wait for release.";
    }

    private async Task ReleaseAnalysisForCaptureAsync()
    {
        CancelAnalysis();
        if (_analysisTask is { } running) await running;
    }

    private async Task DeleteOwnedAnalysisAsync(SavedRecording recording)
    {
        await ReleaseAnalysisForCaptureAsync();
        if (_analysis is not ILocalAnalysisDiagnostics diagnostics) return;
        var saved = await Task.Run(() => _history.LoadAsync(CancellationToken.None));
        var owned = saved.FirstOrDefault(item => item.Id == recording.Id) ?? recording;
        var attempts = owned.TranscriptOutputs.Select(item => item.JobId)
            .Concat(_queue.Jobs.Where(job => job.Recording.Id == recording.Id).Select(job => job.Id))
            .Distinct().ToArray();
        await diagnostics.DeleteDiagnosticsAsync(attempts, CancellationToken.None);
    }

    private TranscriptAnalysisRequest? AnalysisTarget()
    {
        if (SelectedRecording is not { PendingDeletion: null } recording || CompletedTranscriptJobId is not { } attempt)
            return null;
        var job = FindAnalysisJob(recording.Id, attempt);
        var pair = FindAnalysisOutputs(recording, attempt, job);
        var retainedLanguage = RetainedAttemptLanguage(recording, attempt, job);
        var language = Languages.FirstOrDefault(choice => choice == retainedLanguage);
        return pair is null || language is null ? null :
            new(attempt, pair, language, null);
    }

    private TranscriptionJob? FindAnalysisJob(Guid recordingId, Guid attempt) =>
        Jobs.FirstOrDefault(item => item.Id == attempt && item.Recording.Id == recordingId &&
            item.Status == TranscriptionJobStatus.Succeeded);

    private static TranscriptOutputs? FindAnalysisOutputs(SavedRecording recording, Guid attempt, TranscriptionJob? job) =>
        recording.TranscriptOutputs.FirstOrDefault(item => item.JobId == attempt)?.Outputs ?? job?.Outputs;

    private static string? RetainedAttemptLanguage(SavedRecording recording, Guid attempt, TranscriptionJob? job) =>
        recording.TranscriptOutputs.FirstOrDefault(item => item.JobId == attempt)?.Language ?? job?.Language;

    private void ResetTranscriptAnalysis()
    {
        _analysisVersion++;
        CancelAnalysis();
        AnalysisClaims.Clear();
        SelectedAnalysisClaim = null;
        AnalysisArtifact = "";
        AnalysisQuestion = "";
        AnalysisStatus = IsAnalyzing ? "Selection changed. Discarding old output; waiting for native safe release." :
            "Select a successful transcript, then explicitly generate a summary or ask a question.";
        if (CompletedTranscriptJobId is not null && AnalysisTarget() is null)
            AnalysisStatus = "The selected transcript's exact attempt language is unavailable. Select an attempt with retained language metadata or explicitly transcribe again. No language was guessed.";
        OnPropertyChanged(nameof(AnalysisOwnership));
        NotifyAnalysisAvailability();
    }

    private void NotifyAnalysisAvailability()
    {
        OnPropertyChanged(nameof(CanAnalyzeTranscript));
        GenerateSummaryCommand.NotifyCanExecuteChanged();
        AskTranscriptCommand.NotifyCanExecuteChanged();
    }

    partial void OnCompletedTranscriptJobIdChanged(Guid? value) => ResetTranscriptAnalysis();
    partial void OnAnalysisQuestionChanged(string value) => AskTranscriptCommand.NotifyCanExecuteChanged();
    partial void OnSelectedAnalysisClaimChanged(TranscriptAnalysisClaim? value) => OnPropertyChanged(nameof(AnalysisEvidence));
    partial void OnIsBusyChanged(bool value) => NotifyAnalysisAvailability();
    partial void OnIsDeletingRecordingChanged(bool value) => NotifyAnalysisAvailability();
}
