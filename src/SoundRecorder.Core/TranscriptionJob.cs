namespace SoundRecorder.Core;

public sealed class TranscriptionJob
{
    public TranscriptionJob(Guid id, SavedRecording recording)
        : this(id, recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Queued, null, null)
    {
    }

    public TranscriptionJob(
        Guid id,
        SavedRecording recording,
        DateTimeOffset createdAt,
        TranscriptionJobStatus status,
        TranscriptOutputs? outputs,
        string? error,
        string? modelId = null,
        string? interruptionReason = null)
    {
        Id = id;
        Recording = recording;
        CreatedAt = createdAt;
        Status = status;
        Outputs = outputs;
        Error = error;
        ModelId = modelId;
        InterruptionReason = interruptionReason;
    }

    public Guid Id { get; }

    public SavedRecording Recording { get; }

    public TranscriptionJobStatus Status { get; internal set; }

    public DateTimeOffset CreatedAt { get; }

    public TranscriptOutputs? Outputs { get; internal set; }

    public string? Error { get; internal set; }

    public string? ModelId { get; }
    public string Language => Recording.Language;
    public TranscriptionProgress? Progress { get; internal set; }
    public bool IsTerminationPending { get; internal set; }
    public string? InterruptionReason { get; internal set; }
}
