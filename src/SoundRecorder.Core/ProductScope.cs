namespace SoundRecorder.Core;

public static class ProductScope
{
    public const bool RequiresLiveTranscription = false;
    public const bool ImportsExistingAudio = false;
    public const bool TranslatesAudio = false;
    public const bool DetectsLanguageAutomatically = false;
    public const bool GuaranteesMixedLanguageAccuracy = false;
    public const bool IdentifiesSpeakers = false;
    public const bool EditsTranscripts = false;
    public const bool PlaysAudioInApp = true;
    public const bool SearchesRecordings = true;
    public const bool DeletesRecordingsInApp = true;
    public const bool StartsRecordingOnAppLaunch = false;
    public const bool ProvidesGlobalRecordingHotkeys = false;
}
