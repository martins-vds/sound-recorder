using System.Net.Http;
using System.Text.Json;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Models;

public sealed partial class LocalTranscriptionModelManager
{
    public async Task<DownloadedTranscriptionModel> DownloadAsync(
        ModelCatalogEntry entry,
        IProgress<ModelDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        entry = GetDownloadCatalogEntry(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var staging = new ModelStaging(_modelsDirectory);
        try
        {
            using (staging)
                return await DownloadIntoStagingAsync(entry, progress, staging, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("The model download failed. Check the connection and retry.", exception);
        }
        catch (IOException exception)
        {
            throw new IOException("The model download could not finish. Check local storage and retry.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The model source metadata is invalid. Retry the download.", exception);
        }
    }

    private static ModelCatalogEntry GetDownloadCatalogEntry(ModelCatalogEntry entry)
    {
        var catalogEntry = TranscriptionModelCatalog.Find(entry.Key);
        if (catalogEntry is null || catalogEntry.DownloadUri != entry.DownloadUri)
            throw new InvalidDataException("Choose a supported built-in catalog entry before downloading.");
        return catalogEntry;
    }

    private async Task<DownloadedTranscriptionModel> DownloadIntoStagingAsync(
        ModelCatalogEntry entry, IProgress<ModelDownloadProgress> progress,
        ModelStaging staging, CancellationToken cancellationToken)
    {
        progress.Report(new(ModelDownloadPhase.ResolvingMetadata, 0, null));
        var metadata = await ResolveMetadataAsync(entry, cancellationToken);
        using var response = await _httpClient.GetAsync(
            metadata.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        ValidateReportedDownloadSize(total, metadata.Size);
        var path = Path.Combine(staging.Path, "model.bin");
        var transferred = await TransferModelAsync(response, path, metadata.Size, total, progress, cancellationToken);
        progress.Report(new(ModelDownloadPhase.Verifying, transferred, total));
        await VerifyDownloadedArtifactAsync(path, metadata, transferred, cancellationToken);
        var package = new TranscriptionModelPackage(
            1, entry.Key, "Whisper.net 1.9", "model.bin", metadata.Sha256,
            "Windows11-x64", entry.Languages);
        var installed = await CommitStagedModelAsync(
            staging, package, activate: false, cancellationToken);
        progress.Report(new(ModelDownloadPhase.Completed, transferred, total));
        return installed.Model;
    }

    private static void ValidateReportedDownloadSize(long? total, long expectedSize)
    {
        if (total.HasValue && total.Value != expectedSize)
            throw new InvalidDataException("The reported model size does not match trusted metadata. Retry the download.");
    }

    private static async Task<long> TransferModelAsync(
        HttpResponseMessage response, string path, long expectedSize, long? total,
        IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken)
    {
        long transferred = 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        var buffer = new byte[81920];
        int count;
        progress.Report(new(ModelDownloadPhase.Downloading, transferred, total));
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (transferred + count > expectedSize)
                throw new InvalidDataException("The model transfer exceeded its expected size. Retry the download.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            transferred += count;
            progress.Report(new(ModelDownloadPhase.Downloading, transferred, total));
        }
        return transferred;
    }

    private static async Task VerifyDownloadedArtifactAsync(
        string path, DownloadMetadata metadata, long transferred,
        CancellationToken cancellationToken)
    {
        ValidateCompletedTransfer(transferred, metadata.Size);
        var hash = await ComputeHashAsync(path, cancellationToken);
        if (!hash.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model failed its SHA-256 integrity check. Retry the download.");
        await using var model = File.OpenRead(path);
        var magic = new byte[4];
        await model.ReadExactlyAsync(magic, cancellationToken);
        if (!magic.AsSpan().SequenceEqual(new byte[] { 0x6c, 0x6d, 0x67, 0x67 }))
            throw new InvalidDataException("The downloaded artifact is not a compatible GGML model. Retry using the catalog.");
    }

    private static void ValidateCompletedTransfer(long transferred, long expectedSize)
    {
        if (transferred != expectedSize)
            throw new InvalidDataException("The model download is incomplete. Retry the download.");
    }

    private async Task<DownloadMetadata> ResolveMetadataAsync(
        ModelCatalogEntry entry, CancellationToken cancellationToken)
    {
        const string api = "https://huggingface.co/api/models/sandrohanea/whisper.net";
        using var revisionResponse = await _httpClient.GetAsync(
            $"{api}/revision/v4", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        revisionResponse.EnsureSuccessStatusCode();
        await using var revisionStream = await revisionResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var revisionDocument = await JsonDocument.ParseAsync(revisionStream, cancellationToken: cancellationToken);
        var revision = ReadSourceRevision(revisionDocument.RootElement);
        var artifactPath = entry.DownloadUri.AbsolutePath.Split("/v4/")[1];
        var directory = artifactPath.Split('/')[0];
        using var treeResponse = await _httpClient.GetAsync(
            $"{api}/tree/{revision}/{directory}?recursive=false&expand=false",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        treeResponse.EnsureSuccessStatusCode();
        await using var treeStream = await treeResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var treeDocument = await JsonDocument.ParseAsync(treeStream, cancellationToken: cancellationToken);
        return ReadArtifactMetadata(treeDocument.RootElement, artifactPath,
            new Uri(entry.DownloadUri.AbsoluteUri.Replace("/v4/", $"/{revision}/")));
    }

    private static string ReadSourceRevision(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Model source metadata is missing. Retry the download.");
        var revision = ReadRequiredString(root, "sha", "Model source metadata is missing. Retry the download.");
        ValidateSourceRevision(revision);
        return revision!;
    }

    private static void ValidateSourceRevision(string? revision)
    {
        if (revision is null || revision.Length != 40 || !revision.All(Uri.IsHexDigit))
            throw new InvalidDataException("Model source revision is invalid. Retry the download.");
    }

    private static DownloadMetadata ReadArtifactMetadata(JsonElement root, string artifactPath, Uri downloadUri)
    {
        ValidateArtifactInventory(root);
        DownloadMetadata? metadata = null;
        foreach (var file in root.EnumerateArray())
        {
            if (!MatchesArtifactPath(file, artifactPath))
                continue;
            if (metadata is not null)
                throw new InvalidDataException("The model source metadata is ambiguous. Retry the download.");
            metadata = ReadIntegrityMetadata(file, downloadUri);
        }
        if (metadata is not null)
            return metadata;
        throw new InvalidDataException("The model is missing from trusted source metadata. Retry the download.");
    }

    private static void ValidateArtifactInventory(JsonElement inventory)
    {
        if (inventory.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The model source inventory is invalid. Retry the download.");
    }

    private static bool MatchesArtifactPath(JsonElement file, string artifactPath) =>
        file.ValueKind == JsonValueKind.Object &&
        file.TryGetProperty("path", out var path) &&
        path.ValueKind == JsonValueKind.String && path.GetString() == artifactPath;

    private static DownloadMetadata ReadIntegrityMetadata(JsonElement file, Uri downloadUri)
    {
        const string missing = "Expected model integrity metadata is missing. Retry the download.";
        if (ReadRequiredString(file, "type", missing) != "file")
            throw new InvalidDataException(missing);
        var expectedSize = ReadRequiredSize(file, missing);
        var lfs = ReadRequiredLfs(file, missing);
        var hash = ReadRequiredString(lfs, "oid", missing);
        var size = ReadRequiredSize(lfs, missing);
        ValidateIntegrityMetadata(hash, size, expectedSize);
        // Git blob oid and xetHash are not the LFS content SHA-256.
        return new(downloadUri, hash!, size);
    }

    private static string? ReadRequiredString(JsonElement value, string name, string error)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(error);
        return property.GetString();
    }

    private static long ReadRequiredSize(JsonElement value, string error)
    {
        if (!value.TryGetProperty("size", out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var size))
            throw new InvalidDataException(error);
        return size;
    }

    private static JsonElement ReadRequiredLfs(JsonElement file, string error)
    {
        if (!file.TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(error);
        return lfs;
    }

    private static void ValidateIntegrityMetadata(string? hash, long size, long expectedSize)
    {
        if (size <= 0 || size != expectedSize ||
            hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Expected model integrity metadata is invalid. Retry the download.");
    }

    private sealed record DownloadMetadata(Uri DownloadUri, string Sha256, long Size);
}
