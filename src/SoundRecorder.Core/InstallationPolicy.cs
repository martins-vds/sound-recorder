namespace SoundRecorder.Core;

public static class InstallationPolicy
{
    public const bool RequiresAdministrator = false;
    public const bool RequiresDedicatedGpu = false;
    public const bool RequiresAccount = false;
    public const string InitialUiLanguage = "English";
    public const string TargetFramework = "net10.0-windows";
    public const string RuntimeIdentifier = "win-x64";
}
