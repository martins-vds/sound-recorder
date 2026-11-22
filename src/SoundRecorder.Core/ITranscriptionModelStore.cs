namespace SoundRecorder.Core;

public interface ITranscriptionModelStore
{
    bool HasCompatibleModel(string language);

    string? GetModelPath(string language);

    string? SelectedModelId => null;

    // Legacy stores cannot resolve pinned identities; their current model is not a substitute.
    string? GetModelPath(string language, string? pinnedModelId) =>
        pinnedModelId is null ? GetModelPath(language) : null;
}
