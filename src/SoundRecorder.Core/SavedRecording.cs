namespace SoundRecorder.Core;

public sealed record SavedRecording(
    Guid Id,
    string AudioPath,
    string Language,
    RecordingOutcome Outcome,
    TimeSpan CapturedDuration);
