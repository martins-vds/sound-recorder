namespace SoundRecorder.Core;

public sealed record RecordingRequest(
    AudioSource Source,
    string? MicrophoneId,
    string? PlaybackDeviceId,
    string? DestinationFolder,
    string? Title,
    string Language = "English",
    bool AllowShortRecordingWhenStorageIsLow = false);
