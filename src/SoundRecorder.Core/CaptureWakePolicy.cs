namespace SoundRecorder.Core;

public static class CaptureWakePolicy
{
    public const bool PreventsIdleSleep = true;
    public const bool PreventsExplicitSuspensionOrShutdown = false;
}
