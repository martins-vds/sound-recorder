namespace SoundRecorder.Core;

public sealed record TranscriptDocument(
    SavedRecording Recording,
    string Language,
    IReadOnlyList<TranscriptSegment> Segments)
{
    public TimeSpan Duration => Recording.CapturedDuration;
}
