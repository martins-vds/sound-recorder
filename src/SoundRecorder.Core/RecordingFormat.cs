namespace SoundRecorder.Core;

public static class RecordingFormat
{
    public const int SampleRate = 48_000;
    public const int BitsPerSample = 16;
    public static readonly TimeSpan MaximumCapturedDuration = TimeSpan.FromHours(4);
    public static readonly TimeSpan WarningCapturedDuration = TimeSpan.FromHours(4) - TimeSpan.FromMinutes(5);
}
