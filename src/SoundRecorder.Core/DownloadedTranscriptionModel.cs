namespace SoundRecorder.Core;

public sealed record DownloadedTranscriptionModel(
    string Id,
    string DisplayName,
    string ModelPath,
    IReadOnlyList<string> Languages)
{
    public override string ToString() => DisplayName;
}
