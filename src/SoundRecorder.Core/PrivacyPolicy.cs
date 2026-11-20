namespace SoundRecorder.Core;

public static class PrivacyPolicy
{
    public const bool StoresConversationContentLocally = true;
    public const bool SendsTelemetry = false;
    public const bool ChecksForUpdatesInBackground = false;
    public const bool UsesCloudTranscriptionFallback = false;
    public const bool ProvidesAppManagedEncryption = false;
    public const bool ProvidesPasswordProtection = false;
    public const bool DiagnosesEverySynchronizationTool = false;
    public const bool SupportsWindows11X64 = true;
    public const bool SupportsArm64 = false;
    public const bool SupportsCompressedAudioOutput = false;
    public const bool SupportsSeparateSourceTracks = false;

    public const string CloudSynchronizationWarning =
        "An external service may upload files from this folder. The app cannot detect " +
        "every third-party synchronization tool.";
}
