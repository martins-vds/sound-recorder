using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Models;

/// <summary>Manages verified local artifacts separately from explicit model selection.</summary>
public interface ILocalTranscriptionModelManager : ITranscriptionModelStore
{
    TranscriptionModelPackage? CurrentPackage { get; }

    IReadOnlyList<DownloadedTranscriptionModel> DownloadedModels => [];

    Task SelectAsync(string id, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Model selection is not supported by this store."));

    /// <summary>Verifies and registers an offline ZIP package without changing selection.</summary>
    Task<DownloadedTranscriptionModel> RegisterAsync(
        string packagePath, CancellationToken cancellationToken) =>
        Task.FromException<DownloadedTranscriptionModel>(
            new NotSupportedException("Model registration is not supported by this store."));

    Task RefreshInventoryAsync(CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Model inventory refresh is not supported by this store."));

    /// <summary>Downloads and verifies a raw catalog artifact without changing selection.</summary>
    Task<DownloadedTranscriptionModel> DownloadAsync(
        ModelCatalogEntry entry,
        IProgress<ModelDownloadProgress> progress,
        CancellationToken cancellationToken) =>
        Task.FromException<DownloadedTranscriptionModel>(
            new NotSupportedException("Catalog downloads are not supported by this store."));

    /// <summary>Legacy compatibility entrypoint: registers and activates an offline ZIP package.</summary>
    Task<TranscriptionModelPackage> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken);

    Task<TranscriptionModelPackage?> ImportBundledAsync(
        string bundledModelsDirectory,
        CancellationToken cancellationToken);

    /// <summary>Legacy compatibility entrypoint: downloads a ZIP package and activates it.</summary>
    Task<TranscriptionModelPackage> DownloadAndImportAsync(
        Uri packageUri,
        CancellationToken cancellationToken);
}
