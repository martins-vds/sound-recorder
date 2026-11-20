namespace SoundRecorder.Core;

public interface ITranscriptionModelStore
{
    bool HasCompatibleModel(string language);

    string? GetModelPath(string language);
}
