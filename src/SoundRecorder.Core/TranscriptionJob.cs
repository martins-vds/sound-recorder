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
        string? error)
    {
        Id = id;
        Recording = recording;
        CreatedAt = createdAt;
        Status = status;
        Outputs = outputs;
        Error = error;
    }

    public Guid Id { get; }

    public SavedRecording Recording { get; }

    public TranscriptionJobStatus Status { get; internal set; }

    public DateTimeOffset CreatedAt { get; }

    public TranscriptOutputs? Outputs { get; internal set; }

    public string? Error { get; internal set; }
}
