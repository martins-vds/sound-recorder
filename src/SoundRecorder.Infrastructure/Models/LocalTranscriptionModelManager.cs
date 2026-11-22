using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Infrastructure.Models;

public sealed partial class LocalTranscriptionModelManager : ILocalTranscriptionModelManager
{
    private readonly string _modelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalTranscriptionModelManager> _logger;
    private TranscriptionModelPackage? _currentPackage;
    private string? _currentModelPath;
    private volatile Dictionary<string, InstalledModel> _inventory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);

    public LocalTranscriptionModelManager(
        string modelsDirectory,
        HttpClient httpClient,
        ILogger<LocalTranscriptionModelManager>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<LocalTranscriptionModelManager>.Instance;
        Directory.CreateDirectory(_modelsDirectory);
        LoadInventoryAsync(default).GetAwaiter().GetResult();
        LoadCurrentPackage();
    }

    public TranscriptionModelPackage? CurrentPackage => HasVerifiedSelection() ? _currentPackage : null;

    public string? SelectedModelId { get; private set; }

    public IReadOnlyList<DownloadedTranscriptionModel> DownloadedModels =>
        _inventory.Values
            .Where(IsStillVerified)
            .Select(model => model.Model)
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();

    public bool HasCompatibleModel(string language)
    {
        return _currentPackage?.Languages.Contains(
                   language,
                   StringComparer.OrdinalIgnoreCase) == true &&
               HasVerifiedSelection();
    }

    public string? GetModelPath(string language)
    {
        return HasCompatibleModel(language) ? _currentModelPath : null;
    }

    public string? GetModelPath(string language, string? pinnedModelId)
    {
        if (pinnedModelId is null)
            return GetModelPath(language);
        return _inventory.TryGetValue(pinnedModelId, out var installed) &&
               installed.Model.Languages.Contains(language, StringComparer.OrdinalIgnoreCase) &&
               IsStillVerified(installed)
            ? installed.Model.ModelPath
            : null;
    }

    public async Task SelectAsync(string id, CancellationToken cancellationToken)
    {
        await _inventoryGate.WaitAsync(cancellationToken);
        try
        {
            if (!_inventory.TryGetValue(id, out var installed))
                throw new InvalidOperationException("The model is unavailable. Register or download it before selecting it.");
            var hash = await ComputeHashAsync(installed.Model.ModelPath, cancellationToken);
            if (!hash.Equals(installed.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The model failed verification. Re-import or download it again.");
            var file = new FileInfo(installed.Model.ModelPath);
            installed = installed with { Length = file.Length, LastWriteTimeUtc = file.LastWriteTimeUtc };
            _inventory = new Dictionary<string, InstalledModel>(_inventory, StringComparer.Ordinal) { [id] = installed };
            await ActivateAsync(installed, cancellationToken);
        }
        finally
        {
            _inventoryGate.Release();
        }
    }

    public async Task RefreshInventoryAsync(CancellationToken cancellationToken)
    {
        await _inventoryGate.WaitAsync(cancellationToken);
        try
        {
            var failures = await LoadInventoryAsync(cancellationToken);
            _currentPackage = null;
            _currentModelPath = null;
            var selectionFailure = LoadCurrentPackage();
            if (selectionFailure is not null)
                failures.Add(selectionFailure);
            if (failures.Count != 0)
                throw new InvalidDataException(
                    "Some local models are unavailable. Re-import or download them again.",
                    new AggregateException(failures));
        }
        finally
        {
            _inventoryGate.Release();
        }
    }

    public async Task<TranscriptionModelPackage> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken) =>
        (await InstallPackageAsync(packagePath, activate: true, cancellationToken)).Package;

    public async Task<DownloadedTranscriptionModel> RegisterAsync(
        string packagePath, CancellationToken cancellationToken)
    {
        return (await InstallPackageAsync(packagePath, activate: false, cancellationToken)).Model;
    }

    private async Task<InstalledModel> InstallPackageAsync(
        string packagePath, bool activate, CancellationToken cancellationToken,
        bool onlyIfNoSelection = false)
    {
        InfrastructureLog.ModelImportStarted(_logger);
        using var staging = new ModelStaging(_modelsDirectory);
        try
        {
            TranscriptionModelPackage manifest;
            using (var archive = ZipFile.OpenRead(packagePath))
                manifest = await StageImportedPackageAsync(archive, staging.Path, cancellationToken);
            return await CommitStagedModelAsync(
                staging, manifest, activate, cancellationToken, onlyIfNoSelection);
        }
        catch (Exception exception)
        {
            InfrastructureLog.ModelImportFailed(_logger, exception);
            throw;
        }
    }

    private static async Task<TranscriptionModelPackage> StageImportedPackageAsync(
        ZipArchive archive, string stagingDirectory, CancellationToken cancellationToken)
    {
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The model package does not contain manifest.json.");
        var manifest = await ReadManifestAsync(manifestEntry, cancellationToken);
        ValidateManifest(manifest);
        if (TranscriptionModelCatalog.Find(manifest.Id) is not null)
            throw new InvalidDataException(
                "Built-in artifact IDs are reserved for verified catalog downloads. Import custom packages with a distinct ID.");
        var modelEntry = archive.GetEntry(manifest.ModelFile)
            ?? throw new InvalidDataException($"The model package does not contain '{manifest.ModelFile}'.");
        var stagedModelPath = Path.Combine(stagingDirectory, manifest.ModelFile);
        await using (var source = modelEntry.Open())
        await using (var output = new FileStream(
            stagedModelPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(output, cancellationToken);
        }
        var actualHash = await ComputeHashAsync(stagedModelPath, cancellationToken);
        if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model package failed its SHA-256 integrity check.");
        return manifest;
    }

    private static void DeleteOwnedStaging(string staging)
    {
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
    }

    private async Task<InstalledModel> CommitStagedModelAsync(
        ModelStaging staging, TranscriptionModelPackage package, bool activate,
        CancellationToken cancellationToken, bool onlyIfNoSelection = false)
    {
        await _inventoryGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindReusableArtifactAsync(package, cancellationToken);
            if (existing is not null)
            {
                await ActivateIfRequestedAsync(existing, activate, onlyIfNoSelection, cancellationToken);
                return existing;
            }
            var identity = await PrepareStagedIdentityAsync(staging.Path, package, activate, cancellationToken);
            return await PublishStagedArtifactAsync(
                staging, package, identity, activate, onlyIfNoSelection, cancellationToken);
        }
        finally
        {
            _inventoryGate.Release();
        }
    }

    private async Task<InstalledModel?> FindReusableArtifactAsync(
        TranscriptionModelPackage package, CancellationToken cancellationToken)
    {
        var existing = _inventory.Values.FirstOrDefault(model =>
            model.Package.Id == package.Id &&
            model.Package.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase));
        if (existing is not null &&
            File.Exists(existing.Model.ModelPath) &&
            (await ComputeHashAsync(existing.Model.ModelPath, cancellationToken))
                .Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            return existing;
        return null;
    }

    private async Task<string?> PrepareStagedIdentityAsync(
        string staging, TranscriptionModelPackage package, bool activate, CancellationToken cancellationToken)
    {
        var conflict = _inventory.Values.Any(model =>
            model.Package.Id == package.Id &&
            !model.Package.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase));
        if (!conflict)
            return null;
        if (!activate)
            throw new InvalidDataException(
                "This model ID already identifies a different artifact. Import a package with a distinct ID.");
        var identity = $"{package.Id}@{package.Sha256.ToLowerInvariant()}";
        await WriteLegacyIdentityAsync(Path.Combine(staging, "identity.json"), identity, cancellationToken);
        return identity;
    }

    private Task ActivateIfRequestedAsync(
        InstalledModel installed, bool activate, bool onlyIfNoSelection, CancellationToken cancellationToken)
    {
        if (activate && (!onlyIfNoSelection || SelectedModelId is null))
            return ActivateAsync(installed, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task<InstalledModel> PublishStagedArtifactAsync(
        ModelStaging staging, TranscriptionModelPackage package, string? identity,
        bool activate, bool onlyIfNoSelection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFileName(staging.Path);
        try
        {
            var installed = Installed(directory, package);
            if (identity is not null)
                installed = WithLegacyIdentity(installed, identity);
            await PublishManifestAsync(staging.Path, package, cancellationToken);
            await ActivateIfRequestedAsync(installed, activate, onlyIfNoSelection, cancellationToken);
            _inventory = new Dictionary<string, InstalledModel>(_inventory, StringComparer.Ordinal)
            {
                [installed.Model.Id] = installed
            };
            staging.Published = true;
            return installed;
        }
        catch
        {
            Directory.Delete(staging.Path, recursive: true);
            throw;
        }
    }

    private static async Task PublishManifestAsync(
        string directory, TranscriptionModelPackage package, CancellationToken cancellationToken)
    {
        // The verified manifest marks readiness; publishing must not require renaming an open directory.
        var temporary = Path.Combine(directory, $".manifest-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(package), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(directory, "manifest.json"));
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task ActivateAsync(InstalledModel installed, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(_modelsDirectory, "current.json");
        var temporary = $"{marker}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary, JsonSerializer.Serialize(new CurrentModel(
                    installed.Directory, installed.Package, installed.Model.Id)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, marker, overwrite: true);
            _currentPackage = installed.Package;
            _currentModelPath = installed.Model.ModelPath;
            SelectedModelId = installed.Model.Id;
            InfrastructureLog.ModelActivated(_logger, installed.Package.Id);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
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

        return (await InstallPackageAsync(
            packages[0], activate: true, cancellationToken, onlyIfNoSelection: true)).Package;
    }

    private Exception? LoadCurrentPackage()
    {
        try
        {
            ReadCurrentPackage();
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The selected model is unavailable. Select a verified downloaded model.");
            return exception;
        }
    }

    private void ReadCurrentPackage()
    {
        var current = ReadCurrentMarker();
        if (current is null)
            return;
        ValidateCurrentMarker(current);
        SelectedModelId = current.ModelId ?? current.Package.Id;
        var modelPath = Path.Combine(
            _modelsDirectory,
            current.Directory,
            current.Package.ModelFile);
        var installed = FindSelectedArtifact(current);
        ValidateSelectedIdentity(current.ModelId, installed.Model.Id);
        RestoreAvailableSelection(installed, modelPath);
    }

    private CurrentModel? ReadCurrentMarker()
    {
        var marker = Path.Combine(_modelsDirectory, "current.json");
        if (!File.Exists(marker))
            return null;
        return JsonSerializer.Deserialize<CurrentModel>(File.ReadAllText(marker));
    }

    private static void ValidateCurrentMarker(CurrentModel current)
    {
        if (current.Package is null)
            throw new InvalidDataException("The selected model metadata is invalid.");
        ValidateSafeFileName(current.Directory);
        ValidateManifest(current.Package);
    }

    private InstalledModel FindSelectedArtifact(CurrentModel current)
    {
        var installed = _inventory.Values.FirstOrDefault(model =>
            model.Directory == current.Directory &&
            model.Package.Id == current.Package.Id &&
            model.Package.Sha256.Equals(current.Package.Sha256, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
            throw new InvalidDataException("The selected model is unavailable. Re-import or download it again.");
        return installed;
    }

    private static void ValidateSelectedIdentity(string? selectedId, string artifactId)
    {
        if (selectedId is not null && selectedId != artifactId)
            throw new InvalidDataException("The selected model identity is invalid. Select a verified downloaded model.");
    }

    private void RestoreAvailableSelection(InstalledModel installed, string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new InvalidDataException("The selected model is unavailable. Re-import or download it again.");
        _currentPackage = installed.Package;
        _currentModelPath = installed.Model.ModelPath;
        SelectedModelId = installed.Model.Id;
    }

    private async Task<List<Exception>> LoadInventoryAsync(CancellationToken cancellationToken)
    {
        var inventory = new Dictionary<string, InstalledModel>(StringComparer.Ordinal);
        var failures = new List<Exception>();
        var selection = TryReadSelectionMarker();
        var selectedDirectory = selection?.Directory;
        foreach (var directory in Directory.EnumerateDirectories(_modelsDirectory)
                     .OrderBy(directory => Path.GetFileName(directory) == selectedDirectory ? 0 : 1)
                     .ThenBy(directory => directory, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal))
                continue;
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;
            await LoadInventoryDirectoryAsync(
                directory, manifestPath, inventory, failures, selection, cancellationToken).ConfigureAwait(false);
        }
        _inventory = inventory;
        return failures;
    }

    private async Task LoadInventoryDirectoryAsync(
        string directory, string manifestPath, Dictionary<string, InstalledModel> inventory,
        List<Exception> failures, CurrentModel? selection, CancellationToken cancellationToken)
    {
        try
        {
            var installed = await ReadVerifiedInventoryModelAsync(
                directory, manifestPath, cancellationToken).ConfigureAwait(false);
            installed = await ApplyPersistedIdentityAsync(directory, installed, cancellationToken).ConfigureAwait(false);
            installed = await ReserveSelectionIdentityAsync(directory, installed, selection, cancellationToken)
                .ConfigureAwait(false);
            await MergeInventoryArtifactAsync(directory, installed, inventory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            failures.Add(exception);
            _logger.LogWarning(exception, "A local model is unavailable. Re-import or download a verified model.");
        }
    }

    private static async Task<InstalledModel> ReserveSelectionIdentityAsync(
        string directory, InstalledModel installed, CurrentModel? selection, CancellationToken cancellationToken)
    {
        if (selection?.Package is not { } selectedPackage)
            return installed;
        var selectedId = selection.ModelId ?? selectedPackage.Id;
        if (installed.Model.Id != selectedId ||
            string.Equals(installed.Package.Sha256, selectedPackage.Sha256, StringComparison.OrdinalIgnoreCase))
            return installed;
        var identity = $"{installed.Package.Id}@{installed.Package.Sha256.ToLowerInvariant()}";
        await WriteLegacyIdentityAsync(Path.Combine(directory, "identity.json"), identity, cancellationToken)
            .ConfigureAwait(false);
        return WithLegacyIdentity(installed, identity);
    }

    private async Task<InstalledModel> ReadVerifiedInventoryModelAsync(
        string directory, string manifestPath, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked model directories are unavailable. Re-import a regular model package.");
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var package = JsonSerializer.Deserialize<TranscriptionModelPackage>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The model package manifest is invalid.");
        ValidateManifest(package);
        var installed = Installed(Path.GetFileName(directory), package);
        var hash = await ComputeHashAsync(installed.Model.ModelPath, cancellationToken).ConfigureAwait(false);
        if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model package failed its SHA-256 integrity check.");
        return installed;
    }

    private static async Task<InstalledModel> ApplyPersistedIdentityAsync(
        string directory, InstalledModel installed, CancellationToken cancellationToken)
    {
        var identityPath = Path.Combine(directory, "identity.json");
        if (!File.Exists(identityPath))
            return installed;
        var identity = JsonSerializer.Deserialize<string>(
            await File.ReadAllTextAsync(identityPath, cancellationToken).ConfigureAwait(false));
        if (identity != $"{installed.Package.Id}@{installed.Package.Sha256.ToLowerInvariant()}")
            throw new InvalidDataException("The local model artifact identity is invalid.");
        return WithLegacyIdentity(installed, identity);
    }

    private static async Task MergeInventoryArtifactAsync(
        string directory, InstalledModel installed, Dictionary<string, InstalledModel> inventory,
        CancellationToken cancellationToken)
    {
        if (inventory.TryGetValue(installed.Model.Id, out var previous))
        {
            if (previous.Package.Sha256.Equals(installed.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                return;
            var identity = $"{installed.Package.Id}@{installed.Package.Sha256.ToLowerInvariant()}";
            await WriteLegacyIdentityAsync(
                Path.Combine(directory, "identity.json"), identity, cancellationToken).ConfigureAwait(false);
            installed = WithLegacyIdentity(installed, identity);
        }
        inventory[installed.Model.Id] = installed;
    }

    private CurrentModel? TryReadSelectionMarker()
    {
        try
        {
            var marker = Path.Combine(_modelsDirectory, "current.json");
            return File.Exists(marker)
                ? JsonSerializer.Deserialize<CurrentModel>(File.ReadAllText(marker))
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static InstalledModel WithLegacyIdentity(InstalledModel installed, string identity) =>
        installed with
        {
            Model = installed.Model with
            {
                Id = identity,
                DisplayName = $"{installed.Package.Id} ({installed.Package.Sha256[..8]})"
            }
        };

    private static async Task WriteLegacyIdentityAsync(
        string path, string identity, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary, JsonSerializer.Serialize(identity), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private InstalledModel Installed(string directory, TranscriptionModelPackage package)
    {
        var file = new FileInfo(Path.Combine(_modelsDirectory, directory, package.ModelFile));
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked model files are unavailable. Re-import a regular model file.");
        return new(directory, package, new DownloadedTranscriptionModel(
            package.Id, TranscriptionModelCatalog.Find(package.Id)?.DisplayName ?? package.Id,
            file.FullName, Array.AsReadOnly(package.Languages.ToArray())),
            file.Length, file.LastWriteTimeUtc);
    }

    private bool HasVerifiedSelection() =>
        SelectedModelId is not null &&
        _inventory.TryGetValue(SelectedModelId, out var installed) &&
        installed.Model.ModelPath == _currentModelPath &&
        IsStillVerified(installed);

    private static bool IsStillVerified(InstalledModel installed)
    {
        try
        {
            var file = new FileInfo(installed.Model.ModelPath);
            return MatchesVerifiedFile(file, installed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool MatchesVerifiedFile(FileInfo file, InstalledModel installed) =>
        file.Exists && file.Length == installed.Length &&
        file.LastWriteTimeUtc == installed.LastWriteTimeUtc &&
        (file.Attributes & FileAttributes.ReparsePoint) == 0;

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
        ValidatePackageCompatibility(manifest);
        ValidatePackageLanguages(manifest);
        ValidateModelFileName(manifest.ModelFile);
        ValidatePackageChecksum(manifest.Sha256);
    }

    private static void ValidatePackageCompatibility(TranscriptionModelPackage manifest)
    {
        if (manifest.FormatVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.Id) ||
            !string.Equals(manifest.Platform, "Windows11-x64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Engine, "Whisper.net 1.9", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The model package is not compatible with this application.");
        }

    }

    private static void ValidatePackageLanguages(TranscriptionModelPackage manifest)
    {
        if (manifest.Languages is null || manifest.Languages.Count == 0 ||
            manifest.Languages.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("The model package is not compatible with this application.");
    }

    private static void ValidateModelFileName(string modelFile)
    {
        ValidateSafeFileName(modelFile);
        if (modelFile.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            modelFile.Equals("identity.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model package uses a reserved metadata file name.");
    }

    private static void ValidatePackageChecksum(string? hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "The model package manifest has an invalid SHA-256 value.");
        }
    }

    private static void ValidateSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.IsPathRooted(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
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
        if (stream.Length == 0)
            throw new InvalidDataException("The model file is empty. Re-import or download a complete model.");
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(bytes);
    }

    private sealed record CurrentModel(
        string Directory,
        TranscriptionModelPackage Package,
        string? ModelId = null);

    private sealed record InstalledModel(
        string Directory, TranscriptionModelPackage Package, DownloadedTranscriptionModel Model,
        long Length, DateTime LastWriteTimeUtc);

    private sealed class ModelStaging : IDisposable
    {
        public ModelStaging(string modelsDirectory)
        {
            Path = System.IO.Path.Combine(modelsDirectory, $"model-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public bool Published { get; set; }

        public void Dispose()
        {
            if (!Published)
                DeleteOwnedStaging(Path);
        }
    }
}
