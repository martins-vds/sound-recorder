namespace SoundRecorder.Core;

public sealed record RecoveryCandidate(
    Guid RecordingId,
    string CapturedAudioPath,
    string Language,
    TimeSpan CapturedDuration,
    DateTimeOffset LastSavedAt);
