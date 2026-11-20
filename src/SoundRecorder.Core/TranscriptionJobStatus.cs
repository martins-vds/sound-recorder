namespace SoundRecorder.Core;

public enum TranscriptionJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
    Interrupted,
}
