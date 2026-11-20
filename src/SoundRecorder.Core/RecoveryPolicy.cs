namespace SoundRecorder.Core;

public static class RecoveryPolicy
{
    public const int MaximumStorageFlushIntervalSeconds = 4;
    public const int MaximumRecoverableAudioLossSeconds = 5;
}
