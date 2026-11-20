namespace SoundRecorder.Core;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
