using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Models;

/// <summary>Installs explicitly selected local transcription models.</summary>
public interface ILocalTranscriptionModelManager : ITranscriptionModelStore
{
    TranscriptionModelPackage? CurrentPackage { get; }

    Task<TranscriptionModelPackage> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken);

    Task<TranscriptionModelPackage?> ImportBundledAsync(
        string bundledModelsDirectory,
        CancellationToken cancellationToken);

    Task<TranscriptionModelPackage> DownloadAndImportAsync(
        Uri packageUri,
        CancellationToken cancellationToken);
}
