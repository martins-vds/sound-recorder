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
    private Task? _analysisEvidenceTask;
    private bool _analysisEvidenceVerified;
    private long _analysisVersion;
    private int _pendingTranscriptionRequests;

    [ObservableProperty]
    private bool isAnalyzing;

    [ObservableProperty]
    private bool isTranscriptAnalysisExpanded;

    [ObservableProperty]
    private string analysisQuestion = "";

    [ObservableProperty]
    private bool analysisUseLocalLookup;

    [ObservableProperty]
    private bool analysisIsFollowUp;

    [ObservableProperty]
    private string analysisAntecedent = "";

    [ObservableProperty]
    private string analysisStatus = "Select a successful transcript, then explicitly generate a summary or ask a question.";

    [ObservableProperty]
    private string analysisArtifact = "";

    [ObservableProperty]
    private TranscriptAnalysisClaim? selectedAnalysisClaim;

    public ObservableCollection<TranscriptAnalysisClaim> AnalysisClaims { get; } = [];
    public ObservableCollection<AnalysisJob> AnalysisJobs { get; } = [];
    public string? AnalysisStorageError { get; set; }

    [ObservableProperty]
    private AnalysisJob? selectedAnalysisJob;

    private bool IsAnalysisIdle => !IsAnalyzing && !_closingView && !IsDeletingRecording;
    private bool CanRequestAnalysis => AnalysisStorageError is null && IsAnalysisIdle &&
        !IsCaptureStartingOrActive && !HasPendingTranscription;
    private bool SelectedAnalysisIsReleased => SelectedAnalysisJob is
        { Status: AnalysisJobStatus.Cancelled or AnalysisJobStatus.Interrupted or AnalysisJobStatus.Failed };
    private bool SelectedAnalysisMatchesTarget() => AnalysisTarget() is { } target &&
        SelectedAnalysisJob?.Request.Identity.AttemptId == target.AttemptId;
    public bool CanContinueAnalysis => _analysis is PersistentTranscriptAnalysis && CanRequestAnalysis &&
        SelectedAnalysisIsReleased && SelectedAnalysisMatchesTarget();
    public bool CanResumeAnalysis => CanContinueAnalysis && SelectedAnalysisJob?.Revision is not null;

    [RelayCommand(CanExecute = nameof(CanResumeAnalysis))]
    private Task ResumeAnalysisAsync() => ContinueAnalysisAsync(true);

    [RelayCommand(CanExecute = nameof(CanContinueAnalysis))]
    private Task RetryAnalysisAsync() => ContinueAnalysisAsync(false);

    private Task ContinueAnalysisAsync(bool resume)
    {
        if (!CanContinueAnalysis || SelectedAnalysisJob is not { } previous || AnalysisTarget() is not { } target)
            return Task.CompletedTask;
        var cancellation = _analysisCancellation = new();
        var version = ++_analysisVersion;
        IsAnalyzing = true;
        AnalysisStatus = resume ? "Explicit compatible resume requested." : "Explicit new analysis attempt requested with original pins.";
        NotifyAnalysisAvailability();
        return _analysisTask = RunAnalysisAsync(target, version, cancellation, previous, resume);
    }

    private bool _analysisInitialized;
    private readonly SemaphoreSlim _analysisRestoreGate = new(1, 1);
    private bool _analysisModelsReady;
    private bool _analysisCaptureIntent;
    private CancellationTokenSource? _analysisSetupCancellation;
    private Task? _analysisSetupTask;

    public async Task InitializeAnalysisAsync()
    {
        if (_analysis is not PersistentTranscriptAnalysis persistent || _analysisInitialized) return;
        try
        {
            await persistent.RecoverAsync();
            _analysisInitialized = true;
            await RefreshAnalysisReadinessAsync();
        }
        catch (Exception error)
        {
            AnalysisStatus = "Analysis state could not be restored. " + error.Message + " No work was restarted.";
        }
    }

    [RelayCommand]
    private Task RestoreAnalysisAsync()
    {
        return _analysis is PersistentTranscriptAnalysis persistent && AnalysisTarget() is { } target
            ? ReadRestoredAnalysisAsync(persistent, target, _analysisVersion)
            : Task.CompletedTask;
    }

    private async Task ReadRestoredAnalysisAsync(PersistentTranscriptAnalysis persistent,
        TranscriptAnalysisRequest target, long version)
    {
        if (!CanRestoreAnalysis(version)) return;
        await _analysisRestoreGate.WaitAsync();
        try
        {
            if (!CanRestoreAnalysis(version)) return;
            var result = await persistent.RestoreAsync(target, CancellationToken.None);
            await PresentRestoredAnalysisAsync(persistent, target, result, version);
        }
        catch (Exception error)
        {
            if (IsCurrentAnalysisRead(version))
                ClearUnavailableAnalysis(error, version, "No replacement transcript was selected.");
        }
        finally { _analysisRestoreGate.Release(); }
    }

    private bool IsCurrentAnalysisRead(long version) => version == _analysisVersion && !_closingView;
    private bool CanRestoreAnalysis(long version) => IsCurrentAnalysisRead(version) && !IsAnalyzing && !IsDeletingRecording;

    private async Task PresentRestoredAnalysisAsync(PersistentTranscriptAnalysis persistent,
        TranscriptAnalysisRequest target, TranscriptAnalysisResult result, long version)
    {
        if (!IsCurrentAnalysisRead(version)) return;
        AnalysisClaims.Clear();
        PresentAnalysis(result);
        if (_analysisEvidenceTask is { } evidence) await evidence;
        if (!IsCurrentAnalysisRead(version)) return;
        AnalysisHistory = persistent.History;
        RefreshAnalysisJobs(persistent, target);
    }

    private void ClearUnavailableAnalysis(Exception error, long version, string detail)
    {
        AnalysisClaims.Clear();
        SelectedAnalysisClaim = null;
        AnalysisHistory = "";
        SetAnalysisStatus(version, "Exact source/history unavailable. " + error.Message + " " + detail);
    }

    [ObservableProperty]
    private string analysisHistory = "";

    [ObservableProperty]
    private string analysisReadiness = "Explicit model setup has not been checked.";

    [RelayCommand]
    private async Task RefreshAnalysisReadinessAsync()
    {
        if (_analysis is not PersistentTranscriptAnalysis persistent) return;
        try
        {
            var readiness = await persistent.Setup.GetReadinessAsync();
            _analysisModelsReady = readiness.State == AnalysisModelReadinessState.Ready;
            AnalysisReadiness = $"{readiness.State} · {readiness.Error ?? readiness.Directory}";
        }
        catch (Exception error)
        {
            _analysisModelsReady = false;
            AnalysisReadiness = "Readiness failed. " + error.Message;
        }
        NotifyAnalysisAvailability();
    }

    [ObservableProperty]
    private bool isSettingUpAnalysis;

    [ObservableProperty]
    private string analysisModelDirectory = Environment.GetEnvironmentVariable("SOUNDRECORDER_ANALYSIS_MODELS") ?? "";

    public string AnalysisSetupDetails => _analysis is PersistentTranscriptAnalysis persistent
        ? $"{persistent.Setup.Profile.Id}\n{persistent.Setup.Profile.Runtime}\n" +
          string.Join("\n", persistent.Setup.Profile.Files.Select(file => $"{file.Role}: {file.Name} · {file.Bytes:N0} bytes · {file.License}")) +
          $"\nTotal pinned download: {persistent.Setup.Profile.Files.Sum(file => file.Bytes):N0} bytes.\n" +
          $"Install location: {persistent.ModelStorageLocation}\nConservative preflight: 4 GiB available memory / 256 MiB disk reserve. Not measured supported limits. No automatic download or inference."
        : "Production model setup is unavailable.";
    public bool CanSetupAnalysis => _analysis is PersistentTranscriptAnalysis && !IsSettingUpAnalysis && !IsAnalyzing && !_closingView;

    [RelayCommand(CanExecute = nameof(CanSetupAnalysis))]
    private Task SelectAnalysisModelDirectoryAsync() => SetupAnalysisAsync((setup, progress, token) =>
        setup.SelectDirectoryAsync(AnalysisModelDirectory, token));

    [RelayCommand(CanExecute = nameof(CanSetupAnalysis))]
    private Task ImportAnalysisModelsAsync(string package) => SetupAnalysisAsync(async (setup, progress, token) =>
        { await setup.ImportAsync(package, progress, token); });

    [RelayCommand(CanExecute = nameof(CanSetupAnalysis))]
    private Task DownloadAnalysisModelsAsync() => SetupAnalysisAsync(async (setup, progress, token) =>
        { await setup.DownloadAsync(progress, token); });

    private Task SetupAnalysisAsync(Func<IAnalysisModelSetup, IProgress<AnalysisModelSetupProgress>, CancellationToken, Task> action)
    {
        if (!CanSetupAnalysis) return Task.CompletedTask;
        return _analysisSetupTask = RunSetupAnalysisAsync(action);
    }

    private async Task RunSetupAnalysisAsync(Func<IAnalysisModelSetup, IProgress<AnalysisModelSetupProgress>, CancellationToken, Task> action)
    {
        IsSettingUpAnalysis = true;
        using var cancellation = _analysisSetupCancellation = new();
        var progress = new Progress<AnalysisModelSetupProgress>(item =>
        {
            if (IsSettingUpAnalysis && !cancellation.IsCancellationRequested)
                AnalysisReadiness = $"{item.Phase} · {item.BytesTransferred:N0}/{item.TotalBytes:N0} bytes";
        });
        try
        {
            await action(((PersistentTranscriptAnalysis)_analysis!).Setup, progress, cancellation.Token);
            await RefreshAnalysisReadinessAsync();
        }
        catch (OperationCanceledException) { AnalysisReadiness = "Setup cancelled; no incomplete profile was selected."; }
        catch (Exception error) { AnalysisReadiness = "Setup failed. " + error.Message + " Explicitly retry; the previous selection is retained."; }
        finally
        {
            _analysisSetupCancellation = null;
            IsSettingUpAnalysis = false;
            NotifyAnalysisAvailability();
        }
    }

    [RelayCommand]
    private void CancelAnalysisSetup() => _analysisSetupCancellation?.Cancel();
    public string AnalysisWarning => "EXPERIMENTAL · UNVERIFIED model output. Phi-mini may invent or misinterpret facts, owners and deadlines even with valid citations. Inspect every original excerpt. Not release-approved. Full recording deletion also removes its owned private analysis diagnostics.";
    public string AnalysisOwnership => CompletedTranscriptJobId is { } id
        ? $"Selected successful transcript attempt: {id:N}" : "No successful transcript selected.";
    public string AnalysisEvidence => SelectedAnalysisClaim is null ? "Select a candidate to inspect exact original excerpts." :
        _analysis is PersistentTranscriptAnalysis && !_analysisEvidenceVerified ? "Checking the exact original TXT/SRT source; no generated text is evidence." :
        string.Join("\n\n", SelectedAnalysisClaim.Evidence.Select(evidence =>
            $"{evidence.Reference}" +
            (evidence.StartMs is { } ms ? $" · original start marker {TimeSpan.FromMilliseconds(ms):hh\\:mm\\:ss\\.fff}" : "") +
            "\n" + evidence.Text));
    public bool CanAnalyzeTranscript => IsAnalysisAvailable && CanCopyTranscript && AnalysisTarget() is not null;
    private bool IsSelectedAnalysisProfileReady => _analysis is not PersistentTranscriptAnalysis ||
        _analysisInitialized && _analysisModelsReady;
    private bool IsAnalysisAvailable => _analysis is not null && CanRequestAnalysis &&
        !IsSettingUpAnalysis && IsSelectedAnalysisProfileReady;
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

    private async Task RunAnalysisAsync(TranscriptAnalysisRequest target, long version, CancellationTokenSource cancellation,
        AnalysisJob? previous = null, bool resume = false)
    {
        var progress = new Progress<string>(phase => ReportAnalysisProgress(version, cancellation, phase));
        try
        {
            var result = await ExecuteAnalysisRequestAsync(target, progress, cancellation.Token, previous, resume);
            if (!CanPublishAnalysis(version, cancellation)) return;
            PresentAnalysis(result);
            if (_analysisEvidenceTask is { } evidence) await evidence;
            if (!CanPublishAnalysis(version, cancellation)) return;
            if (_analysis is PersistentTranscriptAnalysis persistent)
            {
                AnalysisHistory = persistent.History;
                RefreshAnalysisJobs(persistent, target);
            }
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

    private Task<TranscriptAnalysisResult> ExecuteAnalysisRequestAsync(TranscriptAnalysisRequest target,
        IProgress<string> progress, CancellationToken token, AnalysisJob? previous, bool resume)
    {
        if (_analysis is not PersistentTranscriptAnalysis durable)
            return _analysis!.AnalyzeAsync(target, progress, token);
        if (previous is not null)
            return durable.ContinueAsync(previous.Id, target, resume, progress, token);
        return durable.RunAsync(target, RequestedAnalysisMode(target.Question), RequestedDiscussionContext(), progress, token);
    }

    private AnalysisJobMode RequestedAnalysisMode(string? question) => question is null ? AnalysisJobMode.Summary :
        AnalysisUseLocalLookup ? AnalysisJobMode.LocalQuestion : AnalysisJobMode.GlobalQuestion;
    private AnalysisJobDiscussionContext? RequestedDiscussionContext() => AnalysisIsFollowUp
        ? new AnalysisJobDiscussionContext(true, AnalysisAntecedent.Trim()) : null;

    private void RefreshAnalysisJobs(PersistentTranscriptAnalysis persistent, TranscriptAnalysisRequest target)
    {
        var jobs = persistent.Jobs.Where(job => job.Request.Identity.AttemptId == target.AttemptId).ToArray();
        if (AnalysisJobs.SequenceEqual(jobs)) return;
        var selectedId = SelectedAnalysisJob?.Id;
        ReplaceItems(AnalysisJobs, jobs);
        SelectedAnalysisJob = AnalysisJobs.FirstOrDefault(job => job.Id == selectedId);
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
        FinishAnalysisStatus(version, cancellation);
        cancellation.Dispose();
        _analysisCancellation = null;
        IsAnalyzing = false;
        NotifyAnalysisAvailability();
        RefreshCurrentAnalysisJobs();
        RestoreChangedSelection(version);
    }

    private void FinishAnalysisStatus(long version, CancellationTokenSource cancellation)
    {
        if (version != _analysisVersion)
            AnalysisStatus = "Previous analysis released. Generate or ask explicitly for the selected transcript.";
        else if (cancellation.IsCancellationRequested)
            AnalysisStatus = "Cancelled. Native inference has released; no output claimed.";
    }

    private void RefreshCurrentAnalysisJobs()
    {
        if (_analysis is PersistentTranscriptAnalysis persistent && AnalysisTarget() is { } target)
            RefreshAnalysisJobs(persistent, target);
    }

    private void RestoreChangedSelection(long version)
    {
        if (version != _analysisVersion && !_closingView && AnalysisTarget() is not null)
            _ = ObserveViewReadAsync(RestoreAnalysisAsync());
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
        if (_analysis is PersistentTranscriptAnalysis persistent)
        {
            _analysisCaptureIntent = true;
            await persistent.SetRecordingIntentAsync(true);
        }
        CancelAnalysis();
        if (_analysisTask is { } running) await running;
        if (_analysis is PersistentTranscriptAnalysis durable)
        {
            await durable.CancelAndReleaseAsync();
            if (durable.ReleaseError is { } error)
            {
                AnalysisStorageError = "Analysis storage unavailable. " + error;
                AnalysisReadiness = AnalysisStorageError;
                AnalysisStatus = AnalysisStorageError + " Native inference released; displayed job metadata may be stale. Restore storage and restart before another analysis request.";
                _analysisModelsReady = false;
            }
        }
    }

    private async Task DeleteOwnedAnalysisAsync(SavedRecording recording)
    {
        if (AnalysisStorageError is not null)
            throw new InvalidOperationException(AnalysisStorageError + " Restore the private storage before full recording deletion.");
        if (_analysis is not PersistentTranscriptAnalysis) await ReleaseAnalysisForCaptureAsync();
        if (_analysis is not ILocalAnalysisDiagnostics diagnostics) return;
        await diagnostics.DeleteDiagnosticsAsync(await OwnedAnalysisAttemptsAsync(recording), CancellationToken.None);
    }

    private async Task<Guid[]> OwnedAnalysisAttemptsAsync(SavedRecording recording)
    {
        var saved = await Task.Run(() => _history.LoadAsync(CancellationToken.None));
        var owned = saved.FirstOrDefault(item => item.Id == recording.Id) ?? recording;
        return owned.TranscriptOutputs.Select(item => item.JobId)
            .Concat(_queue.Jobs.Where(job => job.Recording.Id == recording.Id).Select(job => job.Id))
            .Distinct().ToArray();
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
        AnalysisIsFollowUp = false;
        AnalysisAntecedent = "";
        AnalysisHistory = "";
        AnalysisJobs.Clear();
        ResetAnalysisStatus();
        OnPropertyChanged(nameof(AnalysisOwnership));
        NotifyAnalysisAvailability();
        if (_analysisInitialized && !IsAnalyzing && AnalysisTarget() is not null)
            _ = ObserveViewReadAsync(RestoreAnalysisAsync());
    }

    private void ResetAnalysisStatus()
    {
        AnalysisStatus = IsAnalyzing ? "Selection changed. Discarding old output; waiting for native safe release." :
            "Select a successful transcript, then explicitly generate a summary or ask a question.";
        if (CompletedTranscriptJobId is not null && AnalysisTarget() is null)
            AnalysisStatus = "The selected transcript's exact attempt language is unavailable. Select an attempt with retained language metadata or explicitly transcribe again. No language was guessed.";
    }

    private void NotifyAnalysisAvailability()
    {
        var intent = IsCaptureStartingOrActive || _closingView || IsDeletingRecording;
        if (_analysis is PersistentTranscriptAnalysis persistent && _analysisCaptureIntent != intent)
        {
            _analysisCaptureIntent = intent;
            _ = persistent.SetRecordingIntentAsync(intent);
        }
        OnPropertyChanged(nameof(CanAnalyzeTranscript));
        OnPropertyChanged(nameof(CanSetupAnalysis));
        OnPropertyChanged(nameof(CanContinueAnalysis));
        OnPropertyChanged(nameof(CanResumeAnalysis));
        GenerateSummaryCommand.NotifyCanExecuteChanged();
        AskTranscriptCommand.NotifyCanExecuteChanged();
        SelectAnalysisModelDirectoryCommand.NotifyCanExecuteChanged();
        ImportAnalysisModelsCommand.NotifyCanExecuteChanged();
        DownloadAnalysisModelsCommand.NotifyCanExecuteChanged();
        RetryAnalysisCommand.NotifyCanExecuteChanged();
        ResumeAnalysisCommand.NotifyCanExecuteChanged();
    }

    partial void OnCompletedTranscriptJobIdChanged(Guid? value) => ResetTranscriptAnalysis();
    partial void OnAnalysisQuestionChanged(string value) => AskTranscriptCommand.NotifyCanExecuteChanged();
    partial void OnSelectedAnalysisClaimChanged(TranscriptAnalysisClaim? value)
    {
        _analysisEvidenceVerified = false;
        OnPropertyChanged(nameof(AnalysisEvidence));
        if (_analysis is PersistentTranscriptAnalysis && value is not null)
            _analysisEvidenceTask = ObserveViewReadAsync(InspectAnalysisEvidenceAsync());
    }

    private Task InspectAnalysisEvidenceAsync()
    {
        if (_analysis is not PersistentTranscriptAnalysis persistent ||
            SelectedAnalysisClaim is not { } claim || AnalysisTarget() is not { } target) return Task.CompletedTask;
        return VerifyAnalysisEvidenceAsync(persistent, claim, target, _analysisVersion);
    }

    private bool IsCurrentAnalysisEvidence(long version, TranscriptAnalysisClaim claim) =>
        IsCurrentAnalysisRead(version) && SelectedAnalysisClaim == claim;

    private async Task VerifyAnalysisEvidenceAsync(PersistentTranscriptAnalysis persistent,
        TranscriptAnalysisClaim claim, TranscriptAnalysisRequest target, long version)
    {
        try
        {
            await persistent.VerifySourceAsync(target, CancellationToken.None);
            if (!IsCurrentAnalysisEvidence(version, claim)) return;
            _analysisEvidenceVerified = true;
            OnPropertyChanged(nameof(AnalysisEvidence));
        }
        catch (Exception error)
        {
            if (IsCurrentAnalysisEvidence(version, claim))
                ClearUnavailableAnalysis(error, version, "No citation was retargeted.");
        }
    }
    partial void OnIsBusyChanged(bool value) => NotifyAnalysisAvailability();
    partial void OnIsDeletingRecordingChanged(bool value) => NotifyAnalysisAvailability();
    partial void OnIsSettingUpAnalysisChanged(bool value) => NotifyAnalysisAvailability();
    partial void OnSelectedAnalysisJobChanged(AnalysisJob? value) => NotifyAnalysisAvailability();
}
