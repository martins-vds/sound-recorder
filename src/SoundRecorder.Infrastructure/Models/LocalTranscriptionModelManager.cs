using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Infrastructure.Models;

public sealed class LocalTranscriptionModelManager : ILocalTranscriptionModelManager
{
    private readonly string _modelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalTranscriptionModelManager> _logger;
    private TranscriptionModelPackage? _currentPackage;
    private string? _currentModelPath;

    public LocalTranscriptionModelManager(
        string modelsDirectory,
        HttpClient httpClient,
        ILogger<LocalTranscriptionModelManager>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<LocalTranscriptionModelManager>.Instance;
        Directory.CreateDirectory(_modelsDirectory);
        LoadCurrentPackage();
    }

    public TranscriptionModelPackage? CurrentPackage => _currentPackage;

    public bool HasCompatibleModel(string language)
    {
        return _currentPackage?.Languages.Contains(
                   language,
                   StringComparer.OrdinalIgnoreCase) == true &&
               File.Exists(_currentModelPath);
    }

    public string? GetModelPath(string language)
    {
        return HasCompatibleModel(language) ? _currentModelPath : null;
    }

    public async Task<TranscriptionModelPackage> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        InfrastructureLog.ModelImportStarted(_logger);
        var stagingDirectory = Path.Combine(
            _modelsDirectory,
            $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException(
                    "The model package does not contain manifest.json.");
            var manifest = await ReadManifestAsync(manifestEntry, cancellationToken);
            ValidateManifest(manifest);

            var modelEntry = archive.GetEntry(manifest.ModelFile)
                ?? throw new InvalidDataException(
                    $"The model package does not contain '{manifest.ModelFile}'.");
            var stagedModelPath = Path.Combine(stagingDirectory, manifest.ModelFile);
            modelEntry.ExtractToFile(stagedModelPath);
            var actualHash = await ComputeHashAsync(stagedModelPath, cancellationToken);
            if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The model package failed its SHA-256 integrity check.");
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest),
                cancellationToken);

            var targetName = $"{manifest.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var targetDirectory = Path.Combine(_modelsDirectory, targetName);
            Directory.Move(stagingDirectory, targetDirectory);
            var previousPackagePath = Path.Combine(_modelsDirectory, "current.json");
            var temporaryPackagePath = $"{previousPackagePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                temporaryPackagePath,
                JsonSerializer.Serialize(
                    new CurrentModel(targetName, manifest)),
                cancellationToken);
            File.Move(temporaryPackagePath, previousPackagePath, overwrite: true);
            _currentPackage = manifest;
            _currentModelPath = Path.Combine(targetDirectory, manifest.ModelFile);
            InfrastructureLog.ModelActivated(_logger, manifest.Id);
            return manifest;
        }
        catch (Exception exception)
        {
            InfrastructureLog.ModelImportFailed(_logger, exception);
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public async Task<TranscriptionModelPackage> DownloadAndImportAsync(
        Uri packageUri,
        CancellationToken cancellationToken)
    {
        InfrastructureLog.ModelDownloadStarted(_logger);
        var temporaryPackage = Path.Combine(
            _modelsDirectory,
            $"{Guid.NewGuid():N}.model-package.zip");
        try
        {
            await using var download = await _httpClient.GetStreamAsync(
                packageUri,
                cancellationToken);
            await using var output = File.Create(temporaryPackage);
            await download.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Close();
            return await ImportAsync(temporaryPackage, cancellationToken);
        }
        catch (Exception exception)
        {
            InfrastructureLog.ModelDownloadFailed(_logger, exception);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPackage))
            {
                File.Delete(temporaryPackage);
            }
        }
    }

    public async Task<TranscriptionModelPackage?> ImportBundledAsync(
        string bundledModelsDirectory,
        CancellationToken cancellationToken)
    {
        if (HasCompatibleModel("English") &&
            HasCompatibleModel("Brazilian Portuguese"))
        {
            return null;
        }

        if (!Directory.Exists(bundledModelsDirectory))
        {
            return null;
        }

        var packages = Directory.EnumerateFiles(
                bundledModelsDirectory,
                "*.zip",
                SearchOption.TopDirectoryOnly)
            .Take(2)
            .ToArray();
        if (packages.Length == 0)
        {
            return null;
        }

        if (packages.Length > 1)
        {
            throw new InvalidDataException(
                "Multiple bundled model packages were found. Keep exactly one.");
        }

        return await ImportAsync(packages[0], cancellationToken);
    }

    private void LoadCurrentPackage()
    {
        var marker = Path.Combine(_modelsDirectory, "current.json");
        if (!File.Exists(marker))
        {
            return;
        }

        var current = JsonSerializer.Deserialize<CurrentModel>(
            File.ReadAllText(marker));
        if (current is null)
        {
            return;
        }

        var modelPath = Path.Combine(
            _modelsDirectory,
            current.Directory,
            current.Package.ModelFile);
        if (File.Exists(modelPath))
        {
            _currentPackage = current.Package;
            _currentModelPath = modelPath;
        }
    }

    private static async Task<TranscriptionModelPackage> ReadManifestAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<TranscriptionModelPackage>(
                   stream,
                   JsonSerializerOptions.Web,
                   cancellationToken)
               ?? throw new InvalidDataException(
                   "The model package manifest is invalid.");
    }

    private static void ValidateManifest(TranscriptionModelPackage manifest)
    {
        if (manifest.FormatVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.Id) ||
            !manifest.Platform.Equals("Windows11-x64", StringComparison.OrdinalIgnoreCase) ||
            !manifest.Engine.Equals("Whisper.net 1.9", StringComparison.OrdinalIgnoreCase) ||
            manifest.Languages.Count == 0)
        {
            throw new InvalidDataException(
                "The model package is not compatible with this application.");
        }

        ValidateSafeFileName(manifest.ModelFile);
        if (manifest.Sha256.Length != 64)
        {
            throw new InvalidDataException(
                "The model package manifest has an invalid SHA-256 value.");
        }
    }

    private static void ValidateSafeFileName(string fileName)
    {
        if (Path.IsPathRooted(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/') ||
            fileName.Contains('\\'))
        {
            throw new InvalidDataException(
                "The model package contains an unsafe file name.");
        }
    }

    private static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes);
    }

    private sealed record CurrentModel(
        string Directory,
        TranscriptionModelPackage Package);
}
