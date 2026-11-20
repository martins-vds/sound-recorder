namespace SoundRecorder.Core;

public sealed record RecoveredRecordingResult(
    SavedRecording? Recording,
    string? Error);
