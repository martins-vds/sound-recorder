namespace SoundRecorder.Core;

public sealed class RecordingStartException : InvalidOperationException
{
    public RecordingStartException(string message)
        : base(message)
    {
    }

    public RecordingStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
