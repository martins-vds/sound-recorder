using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundRecorder.Core;

namespace SoundRecorder.Desktop;

public sealed class TranscriptionJobViewModel : ObservableObject
{
    private bool _audioAvailable = true;
    public TranscriptionJobViewModel(TranscriptionJob job) => Job = job;

    public TranscriptionJob Job { get; private set; }
    public Guid Id => Job.Id;
    public string Title => Job.Recording.Title ?? Path.GetFileNameWithoutExtension(Job.Recording.AudioPath);
    public string Language => Job.Language;
    public string ModelName { get; private set; } = string.Empty;
    public string Status => Job.IsTerminationPending
        ? PendingStatus(Job)
        : TerminalStatus(Job);

    private static string PendingStatus(TranscriptionJob job) =>
        job.InterruptionReason == "User requested Stop." ? "Stopping" :
        job.Status is TranscriptionJobStatus.Succeeded or TranscriptionJobStatus.Failed ? "Finishing" : "Cancelling";

    private static string TerminalStatus(TranscriptionJob job) => job.Status switch
        {
            TranscriptionJobStatus.Succeeded => "Completed",
            TranscriptionJobStatus.Canceled => "Cancelled",
            TranscriptionJobStatus.Interrupted when job.InterruptionReason == "User requested Stop." => "Stopped",
            _ => job.Status.ToString(),
        };
    public string? Error => Job.Error;
    public bool CanCancel => Job.HasTerminalCheckpointFailure || !Job.IsTerminationPending &&
        Job.Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running;
    public bool CanStop => !Job.IsTerminationPending && Job.Status == TranscriptionJobStatus.Running;
    public bool CanDelete => !Job.IsTerminationPending &&
        Job.Status is not (TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running);
    public bool CanRestart => CanDelete && _audioAvailable;
    public bool CanViewTranscript => Job.Status == TranscriptionJobStatus.Succeeded && Job.Outputs is not null;
    public bool HasActiveProgress => Job.Status == TranscriptionJobStatus.Running || Job.IsTerminationPending;
    public bool IsProgressIndeterminate => Job.Progress is not
        { Phase: TranscriptionPhase.Recognizing, Percent: not null };
    public double ProgressPercent => Job.Progress?.Percent ?? 0;
    public string ProgressAccessibilityState => IsProgressIndeterminate ? "Indeterminate" : "Recognition estimate";
    public string ProgressText => Job.Progress is { Phase: TranscriptionPhase.Recognizing, Percent: { } percent }
        ? $"Recognizing / {percent}% recognition estimate"
        : $"{Job.Progress?.Phase ?? TranscriptionPhase.Preparing} / progress unavailable";

    public void Refresh(TranscriptionJob job, string modelName, bool audioAvailable)
    {
        Job = job;
        ModelName = modelName;
        _audioAvailable = audioAvailable;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanViewTranscript));
        OnPropertyChanged(nameof(HasActiveProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressAccessibilityState));
        OnPropertyChanged(nameof(ProgressText));
    }

    public override string ToString() => Title;
}
