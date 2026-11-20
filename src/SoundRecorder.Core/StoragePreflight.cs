namespace SoundRecorder.Core;

public static class StoragePreflight
{
    public static long RequiredBytes(AudioSource source)
    {
        var channels = source == AudioSource.MicrophoneOnly ? 1 : 2;
        return RecordingFormat.SampleRate *
               (RecordingFormat.BitsPerSample / 8) *
               channels *
               (long)RecordingFormat.MaximumCapturedDuration.TotalSeconds;
    }

    public static string? Validate(
        long availableBytes,
        long requiredBytes,
        bool explicitlyAllowedShortRecording)
    {
        return availableBytes < requiredBytes && !explicitlyAllowedShortRecording
            ? "The destination does not have enough free space for a four-hour recording."
            : null;
    }
}
