using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Audio;

internal sealed record CaptureCheckpoint(
    Guid RecordingId,
    string MicrophonePath,
    string? SystemAudioPath,
    string DestinationFolder,
    string? Title,
    string Language,
    AudioSource Source,
    long CapturedTicks,
    DateTimeOffset LastSavedAt);
