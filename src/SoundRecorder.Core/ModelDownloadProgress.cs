namespace SoundRecorder.Core;

public enum ModelDownloadPhase
{
    ResolvingMetadata,
    Downloading,
    Verifying,
    Completed
}

public sealed record ModelDownloadProgress(
    ModelDownloadPhase Phase,
    long BytesTransferred,
    long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0
        ? BytesTransferred * 100d / TotalBytes.Value
        : null;
}
