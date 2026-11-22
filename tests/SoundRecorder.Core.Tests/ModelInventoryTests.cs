using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;

namespace SoundRecorder.Core.Tests;

public sealed class ModelInventoryTests : IDisposable
{
    private readonly ModelTestWorkspace _workspace = new();

    [Fact]
    public async Task LegacyImports_PreserveBothVerifiedArtifactsAcrossRestart()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        await _workspace.Manager.ImportAsync(_workspace.Package("small"), default);
        _workspace.Reload();

        Assert.Equal(["base", "small"],
            _workspace.Manager.DownloadedModels.Select(model => model.Id).Order());
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task RegisterAsync_AddsChoiceWithoutReplacingSelectedLegacyPackage()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var selectedPath = _workspace.Manager.GetModelPath("English");
        var markerPath = Path.Combine(_workspace.ModelsDirectory, "current.json");
        var marker = await File.ReadAllTextAsync(markerPath);
        DownloadedTranscriptionModel? registered = null;

        var failure = await Record.ExceptionAsync(async () =>
            registered = await _workspace.Manager.RegisterAsync(_workspace.Package("small"), default));

        Assert.Null(failure);
        Assert.Equal("small", registered!.Id);
        Assert.Equal(selectedPath, _workspace.Manager.GetModelPath("English"));
        Assert.Equal(marker, await File.ReadAllTextAsync(markerPath));
        Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);
    }

    [Fact]
    public async Task SelectAsync_PersistsExplicitIdentityAndPinnedLookupNeverFallsBack()
    {
        var first = await _workspace.Manager.RegisterAsync(_workspace.Package("base"), default);
        var second = await _workspace.Manager.RegisterAsync(
            _workspace.Package("tiny.en", ["English"]), default);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
        Assert.Null(_workspace.Manager.SelectedModelId);

        var failure = await Record.ExceptionAsync(() =>
            _workspace.Manager.SelectAsync(second.Id, default));
        Assert.Null(failure);
        _workspace.Reload();

        Assert.Equal(second.Id, _workspace.Manager.SelectedModelId);
        Assert.Equal(second.ModelPath, _workspace.Manager.GetModelPath("English", null));
        Assert.Equal(first.ModelPath, _workspace.Manager.GetModelPath("Brazilian Portuguese", first.Id));
        Assert.Null(_workspace.Manager.GetModelPath("Brazilian Portuguese", second.Id));
        Assert.Null(_workspace.Manager.GetModelPath("English", "missing"));
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workspace.Manager.SelectAsync("missing", default));
        Assert.Contains("Register or download", unavailable.Message);
        Assert.Equal(second.Id, _workspace.Manager.SelectedModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshInventory_RejectsMissingOrCorruptedSelectedArtifactWithoutFallback(bool corrupt)
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var selectedPath = _workspace.Manager.GetModelPath("English")!;
        await _workspace.Manager.RegisterAsync(_workspace.Package("small"), default);
        if (corrupt)
            await File.WriteAllBytesAsync(selectedPath, [99, 34, 56, 78]);
        else
            File.Delete(selectedPath);

        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RefreshInventoryAsync(default));

        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("Re-import", failure.Message);
        if (corrupt)
            Assert.Contains(Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions,
                error => error.Message == "The model package failed its SHA-256 integrity check.");
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
        Assert.Null(_workspace.Manager.GetModelPath("English", "base"));
        Assert.Equal("small", Assert.Single(_workspace.Manager.DownloadedModels).Id);
        _workspace.Reload();
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Null(_workspace.Manager.CurrentPackage);
    }

    [Fact]
    public async Task BundledDiscovery_RegistersBilingualModelWithoutReplacingEnglishOnlySelection()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("tiny.en", ["English"]), default);
        var bundled = Path.Combine(_workspace.ModelsDirectory, "bundled");
        Directory.CreateDirectory(bundled);
        File.Copy(_workspace.Package("base"), Path.Combine(bundled, "base.zip"));

        await _workspace.Manager.ImportBundledAsync(bundled, default);

        Assert.Equal("tiny.en", _workspace.Manager.SelectedModelId);
        Assert.False(_workspace.Manager.HasCompatibleModel("Brazilian Portuguese"));
        Assert.Equal(["base", "tiny.en"], _workspace.Manager.DownloadedModels.Select(model => model.Id));
    }

    [Fact]
    public async Task RegisterAsync_StableArtifactIdentityIsNotUsedAsADirectoryName()
    {
        var id = "custom:base:Q5_1";
        var failure = await Record.ExceptionAsync(() =>
            _workspace.Manager.RegisterAsync(_workspace.Package(id), default));
        Assert.Null(failure);
        Assert.Equal(id, Assert.Single(_workspace.Manager.DownloadedModels).Id);
    }

    [Theory]
    [InlineData("Platform")]
    [InlineData("Languages")]
    [InlineData("Sha256")]
    public async Task ReloadInventory_MalformedUntrustedManifestIsReportedWithoutLosingWorkingSelection(string property)
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        await _workspace.Manager.RegisterAsync(_workspace.Package("small"), default);
        var path = _workspace.Manager.GetModelPath("English", "small")!;
        var manifestPath = Path.Combine(Path.GetDirectoryName(path)!, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest[property] = null;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var failure = Record.Exception(_workspace.Reload);
        Assert.Null(failure);
        Assert.Equal("base", Assert.Single(_workspace.Manager.DownloadedModels).Id);
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        await Assert.ThrowsAsync<InvalidDataException>(() => _workspace.Manager.RefreshInventoryAsync(default));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ReloadInventory_InvalidSelectionMarkerLeavesChoicesIntactAndReportsRecovery()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        await File.WriteAllTextAsync(Path.Combine(_workspace.ModelsDirectory, "current.json"), "{broken");
        var failure = Record.Exception(_workspace.Reload);
        Assert.Null(failure);
        Assert.Single(_workspace.Manager.DownloadedModels);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _workspace.Manager.RefreshInventoryAsync(default));
    }

    [Fact]
    public async Task RegisterAsync_ConflictingIdentityCannotRetargetPinnedOrSelectedArtifacts()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var original = _workspace.Manager.GetModelPath("English", "base");
        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RegisterAsync(
            _workspace.Package("base", bytes: [99, 34, 56, 78]), default));

        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("distinct ID", failure.Message);
        Assert.Equal(original, _workspace.Manager.GetModelPath("English", "base"));
        Assert.Equal(original, _workspace.Manager.GetModelPath("English"));
        _workspace.Reload();
        Assert.Equal(original, _workspace.Manager.GetModelPath("English", "base"));
    }

    [Fact]
    public async Task RegisterAsync_RepeatedIdenticalPackagePreservesSelectionAcrossRestart()
    {
        var package = _workspace.Package("base");
        await _workspace.Manager.ImportAsync(package, default);
        var original = _workspace.Manager.GetModelPath("English");
        var registered = await _workspace.Manager.RegisterAsync(package, default);
        _workspace.Reload();

        Assert.Equal(original, _workspace.Manager.GetModelPath("English"));
        Assert.Equal(original, registered.ModelPath);
        Assert.Single(_workspace.Manager.DownloadedModels);
    }

    [Fact]
    public async Task LegacyInventory_SamePackageIdForOlderArtifactKeepsBothPathsAndStableChoices()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var original = _workspace.Manager.GetModelPath("English")!;
        var olderDirectory = Path.Combine(_workspace.ModelsDirectory, "legacy-older");
        ZipFile.ExtractToDirectory(
            _workspace.Package("base", bytes: [99, 34, 56, 78]), olderDirectory);
        _workspace.Reload();

        Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);
        Assert.Equal(original, _workspace.Manager.GetModelPath("English"));
        var older = Assert.Single(_workspace.Manager.DownloadedModels, model => model.ModelPath != original);
        Assert.Equal($"base ({Convert.ToHexString(SHA256.HashData(new byte[] { 99, 34, 56, 78 }))[..8]})",
            older.DisplayName);
        await _workspace.Manager.SelectAsync(older.Id, default);
        _workspace.Reload();
        Assert.Equal(older.Id, _workspace.Manager.SelectedModelId);
        Assert.Equal(older.ModelPath, _workspace.Manager.GetModelPath("English", older.Id));
        Assert.Equal(original, _workspace.Manager.GetModelPath("English", "base"));
    }

    [Fact]
    public async Task RegisterAsync_CustomPackageCannotMasqueradeAsBuiltInCatalogArtifact()
    {
        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RegisterAsync(
            _workspace.Package("whisper.net:base:NoQuantization"), default));
        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("reserved", failure.Message);
        Assert.Empty(_workspace.Manager.DownloadedModels);
        Assert.Null(_workspace.Manager.CurrentPackage);
        Assert.Null(_workspace.Manager.SelectedModelId);
    }

    [Fact]
    public async Task RegisterAsync_EmptyArtifactIsNotReadyEvenWhenItsHashMatches()
    {
        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RegisterAsync(
            _workspace.Package("empty", bytes: []), default));
        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("complete model", failure.Message);
        Assert.Empty(_workspace.Manager.DownloadedModels);
    }

    [Fact]
    public async Task ChangedArtifact_IsNotOfferedOrResolvedUntilReverified()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var path = _workspace.Manager.GetModelPath("English")!;
        await File.WriteAllBytesAsync(path, [99, 34, 56, 78]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(_workspace.Manager.DownloadedModels);
        Assert.Null(_workspace.Manager.CurrentPackage);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
        Assert.Null(_workspace.Manager.GetModelPath("English", "base"));
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => _workspace.Manager.SelectAsync("base", default));
        Assert.Contains("Re-import or download", invalid.Message);
    }

    [Fact]
    public async Task RegisterAsync_ModelCannotOverwriteInventoryMetadata()
    {
        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RegisterAsync(
            _workspace.Package("custom", modelFile: "identity.json"), default));
        Assert.IsType<InvalidDataException>(failure);
        Assert.Empty(_workspace.Manager.DownloadedModels);
    }

    [Fact]
    public async Task RefreshInventory_SelectedPackageWithMissingManifestIsReportedUnavailable()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var path = _workspace.Manager.GetModelPath("English")!;
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "manifest.json"));
        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RefreshInventoryAsync(default));
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
    }

    [Fact]
    public async Task LegacyImport_SamePackageIdUpdateActivatesDistinctArtifactWithoutRetargetingOldIdentity()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var oldPath = _workspace.Manager.GetModelPath("English", "base");
        var failure = await Record.ExceptionAsync(() => _workspace.Manager.ImportAsync(
            _workspace.Package("base", bytes: [99, 34, 56, 78]), default));

        Assert.Null(failure);
        Assert.Equal("base", _workspace.Manager.CurrentPackage!.Id);
        Assert.NotEqual(oldPath, _workspace.Manager.GetModelPath("English"));
        Assert.Equal(oldPath, _workspace.Manager.GetModelPath("English", "base"));
        var selected = _workspace.Manager.SelectedModelId;
        _workspace.Reload();
        Assert.Equal(selected, _workspace.Manager.SelectedModelId);
        Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);
        Assert.Equal(oldPath, _workspace.Manager.GetModelPath("English", "base"));
    }

    [Fact]
    public async Task RefreshInventory_SelectedMarkerWithMissingModelFileReportsFailure()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var markerPath = Path.Combine(_workspace.ModelsDirectory, "current.json");
        var marker = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!;
        marker["Package"]!["ModelFile"] = "missing.bin";
        await File.WriteAllTextAsync(markerPath, marker.ToJsonString());

        var failure = await Record.ExceptionAsync(() => _workspace.Manager.RefreshInventoryAsync(default));

        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Null(_workspace.Manager.CurrentPackage);
        Assert.Single(_workspace.Manager.DownloadedModels);
        Assert.Contains(Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions,
            error => error.Message == "The selected model is unavailable. Re-import or download it again.");
    }

    [Fact]
    public async Task SelectAsync_ReverifiesTouchedFileAndUpdatesReadyInventory()
    {
        var model = await _workspace.Manager.RegisterAsync(_workspace.Package("base"), default);
        File.SetLastWriteTimeUtc(model.ModelPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Empty(_workspace.Manager.DownloadedModels);
        await _workspace.Manager.SelectAsync(model.Id, default);
        Assert.Equal(model, Assert.Single(_workspace.Manager.DownloadedModels));
        Assert.Equal(model.ModelPath, _workspace.Manager.GetModelPath("English", model.Id));
        Assert.True(_workspace.Manager.HasCompatibleModel("English"));
    }

    [Fact]
    public async Task LegacyImport_ExistingArtifactCanBeExplicitlyReactivated()
    {
        var package = _workspace.Package("base");
        await _workspace.Manager.ImportAsync(package, default);
        var basePath = _workspace.Manager.GetModelPath("English");
        await _workspace.Manager.ImportAsync(_workspace.Package("small"), default);
        await _workspace.Manager.ImportAsync(package, default);
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Equal(basePath, _workspace.Manager.GetModelPath("English"));
        Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);
    }

    [Fact]
    public async Task ImportAsync_FailedMarkerPublicationRollsBackOnlyNewArtifactAndTemporaryMarker()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var original = _workspace.Manager.GetModelPath("English");
        var marker = Path.Combine(_workspace.ModelsDirectory, "current.json");
        var backup = Path.Combine(_workspace.ModelsDirectory, "saved-marker.json");
        File.Move(marker, backup);
        Directory.CreateDirectory(marker);
        var exception = await Record.ExceptionAsync(() =>
            _workspace.Manager.ImportAsync(_workspace.Package("small"), default));
        Assert.IsType<UnauthorizedAccessException>(exception);
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Equal(original, _workspace.Manager.GetModelPath("English"));
        Assert.Single(Directory.GetDirectories(_workspace.ModelsDirectory, "model-*"));
        Assert.Empty(Directory.GetFiles(_workspace.ModelsDirectory, "*.tmp"));
        Assert.Empty(Directory.GetDirectories(_workspace.ModelsDirectory, ".staging-*"));
        Directory.Delete(marker);
        File.Move(backup, marker);
        _workspace.Reload();
        Assert.Equal("base", Assert.Single(_workspace.Manager.DownloadedModels).Id);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("package-id")]
    [InlineData("digest")]
    [InlineData("model-id")]
    [InlineData("platform")]
    [InlineData("null-package")]
    [InlineData("unsafe-directory")]
    public async Task RefreshInventory_InconsistentSelectionMarkerDoesNotSubstituteAnotherArtifact(string change)
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var other = await _workspace.Manager.RegisterAsync(_workspace.Package("small"), default);
        var markerPath = Path.Combine(_workspace.ModelsDirectory, "current.json");
        var marker = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!;
        switch (change)
        {
            case "directory": marker["Directory"] = Path.GetFileName(Path.GetDirectoryName(other.ModelPath)); break;
            case "package-id": marker["Package"]!["Id"] = "small"; break;
            case "digest": marker["Package"]!["Sha256"] = new string('0', 64); break;
            case "model-id": marker["ModelId"] = "unknown"; break;
            case "platform": marker["Package"]!["Platform"] = "Linux"; break;
            case "null-package": marker["Package"] = null; break;
            case "unsafe-directory": marker["Directory"] = @"..\other"; break;
        }
        await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _workspace.Manager.RefreshInventoryAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(error.InnerException);
        var failures = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
        var selectionMessage = change switch
        {
            "null-package" => "The selected model metadata is invalid.",
            "unsafe-directory" => "The model package contains an unsafe file name.",
            "model-id" => "The selected model identity is invalid. Select a verified downloaded model.",
            "platform" => "The model package is not compatible with this application.",
            _ => "The selected model is unavailable. Re-import or download it again."
        };
        Assert.Contains(failures, failure => failure.Message == selectionMessage);
        Assert.Equal(change == "model-id" ? "unknown" : "base", _workspace.Manager.SelectedModelId);
        Assert.Null(_workspace.Manager.CurrentPackage);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
        Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _workspace.Manager.RefreshInventoryAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RefreshInventory_IgnoresOwnedInFlightAndUnregisteredDirectories()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var staging = Path.Combine(_workspace.ModelsDirectory, ".staging-inflight");
        ZipFile.ExtractToDirectory(_workspace.Package("small"), staging);
        Directory.CreateDirectory(Path.Combine(_workspace.ModelsDirectory, "unregistered"));
        await _workspace.Manager.RefreshInventoryAsync(default);
        Assert.Equal("base", Assert.Single(_workspace.Manager.DownloadedModels).Id);
        Assert.True(File.Exists(Path.Combine(staging, "model.bin")));
    }

    [Theory]
    [InlineData("null", "The model package manifest is invalid.")]
    [InlineData("identity", "The local model artifact identity is invalid.")]
    public async Task RefreshInventory_ReportsUnusableStoredMetadataWithoutDeletingItsFiles(string kind, string message)
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var other = await _workspace.Manager.RegisterAsync(_workspace.Package("small"), default);
        var directory = Path.GetDirectoryName(other.ModelPath)!;
        await File.WriteAllTextAsync(Path.Combine(directory, kind == "null" ? "manifest.json" : "identity.json"),
            kind == "null" ? "null" : "\"a-different-identity\"");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _workspace.Manager.RefreshInventoryAsync(default));
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains(aggregate.InnerExceptions, failure => failure.Message == message);
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.NotNull(_workspace.Manager.CurrentPackage);
        Assert.True(File.Exists(other.ModelPath));
        Assert.Equal("base", Assert.Single(_workspace.Manager.DownloadedModels).Id);
    }

    [Theory]
    [InlineData(":invalid.bin", "The model package contains an unsafe file name.")]
    [InlineData(" ", "The model package contains an unsafe file name.")]
    [InlineData(@"C:\rooted.bin", "The model package contains an unsafe file name.")]
    [InlineData("manifest.json", "The model package uses a reserved metadata file name.")]
    public async Task RegisterAsync_RejectsUnsafeAndReservedPayloadNamesBeforeExtraction(string file, string message)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _workspace.Manager.RegisterAsync(_workspace.Package("custom", modelFile: file), default));
        Assert.Equal(message, error.Message);
        Assert.Empty(_workspace.Manager.DownloadedModels);
    }

    [Fact]
    public async Task RegisterAsync_RejectsPartiallyInvalidLanguagesAndDigest()
    {
        var languageError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _workspace.Manager.RegisterAsync(_workspace.Package("mixed", ["English", " "]), default));
        Assert.Equal("The model package is not compatible with this application.", languageError.Message);
        var hashError = await Assert.ThrowsAsync<InvalidDataException>(() => _workspace.Manager.RegisterAsync(
            _workspace.Package("invalid-hash", transform: package => package with { Sha256 = "0" + new string('G', 63) }),
            default));
        Assert.Equal("The model package manifest has an invalid SHA-256 value.", hashError.Message);
        Assert.Empty(_workspace.Manager.DownloadedModels);
    }

    [Fact]
    public async Task LegacyManagerDefaultsCannotAdvertiseUnsupportedInventoryOperations()
    {
        ILocalTranscriptionModelManager legacy = new LegacyManager();
        Assert.Empty(legacy.DownloadedModels);
        Assert.Null(legacy.SelectedModelId);
        Assert.Equal("legacy.bin", legacy.GetModelPath("English", null));
        Assert.Null(legacy.GetModelPath("English", "a-pinned-id"));
        Assert.False(string.IsNullOrWhiteSpace(
            (await Assert.ThrowsAsync<NotSupportedException>(() => legacy.SelectAsync("id", default))).Message));
        Assert.False(string.IsNullOrWhiteSpace(
            (await Assert.ThrowsAsync<NotSupportedException>(() => legacy.RegisterAsync("package.zip", default))).Message));
        Assert.False(string.IsNullOrWhiteSpace(
            (await Assert.ThrowsAsync<NotSupportedException>(() => legacy.RefreshInventoryAsync(default))).Message));
        Assert.False(string.IsNullOrWhiteSpace(
            (await Assert.ThrowsAsync<NotSupportedException>(() => legacy.DownloadAsync(
                TranscriptionModelCatalog.Entries[0], new InlineModelProgress(_ => { }), default))).Message));
    }

    [Fact]
    public async Task RefreshInventory_LogsDistinctRecoveryActionsForManifestAndSelectionFailures()
    {
        var logger = new StorageTestLogger<LocalTranscriptionModelManager>();
        using var workspace = new ModelTestWorkspace(logger: logger);
        await workspace.Manager.ImportAsync(workspace.Package("base"), default);
        var other = await workspace.Manager.RegisterAsync(workspace.Package("small"), default);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(other.ModelPath)!, "manifest.json"), "null");
        await File.WriteAllTextAsync(Path.Combine(workspace.ModelsDirectory, "current.json"), "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.Manager.RefreshInventoryAsync(default));
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "A local model is unavailable. Re-import or download a verified model.");
        Assert.Contains(logger.Entries, entry =>
            entry.Message == "The selected model is unavailable. Select a verified downloaded model.");
        Assert.Null(workspace.Manager.CurrentPackage);
        Assert.Equal("base", Assert.Single(workspace.Manager.DownloadedModels).Id);
    }

    [Fact]
    public async Task MissingLegacySelection_NeverRetargetsPinnedIdToAnOlderDifferentArtifact()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var selectedPath = _workspace.Manager.GetModelPath("English")!;
        ZipFile.ExtractToDirectory(_workspace.Package("base", bytes: [99, 34, 56, 78]),
            Path.Combine(_workspace.ModelsDirectory, "older-version"));
        File.Delete(selectedPath);
        _workspace.Reload();
        Assert.Equal("base", _workspace.Manager.SelectedModelId);
        Assert.Null(_workspace.Manager.GetModelPath("English", "base"));
        var older = Assert.Single(_workspace.Manager.DownloadedModels);
        Assert.NotEqual("base", older.Id);
        _workspace.Reload();
        Assert.Equal(older.Id, Assert.Single(_workspace.Manager.DownloadedModels).Id);
        Assert.Null(_workspace.Manager.GetModelPath("English", "base"));
    }

    [Fact]
    public async Task ChangedLengthWithOriginalTimestampCannotMasqueradeAsVerified()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var path = _workspace.Manager.GetModelPath("English")!;
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, [12, 34, 56, 78, 99]);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Empty(_workspace.Manager.DownloadedModels);
        Assert.Null(_workspace.Manager.CurrentPackage);
        Assert.Null(_workspace.Manager.GetModelPath("English", "base"));
    }

    [Fact]
    public async Task DuplicateVerifiedLegacyCopiesRetainSelectedPathAndSingleChoice()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var selected = _workspace.Manager.GetModelPath("English");
        ZipFile.ExtractToDirectory(_workspace.Package("base"), Path.Combine(_workspace.ModelsDirectory, "duplicate"));
        _workspace.Reload();
        Assert.Equal(selected, Assert.Single(_workspace.Manager.DownloadedModels).ModelPath);
        Assert.Equal(selected, _workspace.Manager.GetModelPath("English"));
    }

    [Fact]
    public async Task ConstructorInventoryLoadingDoesNotCaptureStartupSynchronizationContext()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base", bytes: new byte[8 * 1024 * 1024]), default);
        var olderBytes = new byte[8 * 1024 * 1024];
        olderBytes[^1] = 1;
        ZipFile.ExtractToDirectory(_workspace.Package("base", bytes: olderBytes),
            Path.Combine(_workspace.ModelsDirectory, "older"));
        var context = new StartupContext();
        var reloaded = await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                _ = new LocalTranscriptionModelManager(_workspace.ModelsDirectory, _workspace.Client);
                return new LocalTranscriptionModelManager(_workspace.ModelsDirectory, _workspace.Client);
            }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, context.Posts);
        Assert.Equal("base", reloaded.SelectedModelId);
        Assert.Equal(2, reloaded.DownloadedModels.Count);
    }

    [Fact]
    public async Task ConstructorReadsLargeValidPersistedIdentityWithoutCapturingStartupContext()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        await _workspace.Manager.ImportAsync(_workspace.Package("base", bytes: [99, 34, 56, 78]), default);
        var selected = _workspace.Manager.SelectedModelId;
        var path = _workspace.Manager.GetModelPath("English")!;
        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "identity.json"),
            new string(' ', 8 * 1024 * 1024));
        var context = new StartupContext();
        var loaded = await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { return new LocalTranscriptionModelManager(_workspace.ModelsDirectory, _workspace.Client); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, context.Posts);
        Assert.Equal(selected, loaded.SelectedModelId);
        Assert.Equal(2, loaded.DownloadedModels.Count);
    }

    [Fact]
    public async Task UnselectedLegacyCollisionsPreserveDistinctStableArtifactIdentities()
    {
        var first = Path.Combine(_workspace.ModelsDirectory, "a-original");
        var second = Path.Combine(_workspace.ModelsDirectory, "z-other");
        ZipFile.ExtractToDirectory(_workspace.Package("base"), first);
        ZipFile.ExtractToDirectory(_workspace.Package("base", bytes: [99, 34, 56, 78]), second);
        _workspace.Reload();
        Assert.Null(_workspace.Manager.SelectedModelId);
        Assert.Equal(Path.Combine(first, "model.bin"), _workspace.Manager.GetModelPath("English", "base"));
        var alternate = Assert.Single(_workspace.Manager.DownloadedModels, model => model.ModelPath == Path.Combine(second, "model.bin"));
        Assert.Equal($"base@{Convert.ToHexString(SHA256.HashData(new byte[] { 99, 34, 56, 78 })).ToLowerInvariant()}",
            alternate.Id);
        _workspace.Reload();
        Assert.Contains(_workspace.Manager.DownloadedModels, model => model.Id == alternate.Id);
        Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);
    }

    [Fact]
    public async Task FailedLegacyIdentityPublicationCleansOnlyItsOwnedTemporaryFile()
    {
        var first = Path.Combine(_workspace.ModelsDirectory, "a-original");
        var second = Path.Combine(_workspace.ModelsDirectory, "z-other");
        ZipFile.ExtractToDirectory(_workspace.Package("base"), first);
        ZipFile.ExtractToDirectory(_workspace.Package("base", bytes: [99, 34, 56, 78]), second);
        Directory.CreateDirectory(Path.Combine(second, "identity.json"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            _workspace.Manager.RefreshInventoryAsync(default));
        Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(Path.Combine(first, "model.bin"), Assert.Single(_workspace.Manager.DownloadedModels).ModelPath);
        Assert.Empty(Directory.GetFiles(second, "*.tmp"));
        Assert.True(Directory.Exists(Path.Combine(second, "identity.json")));
        Assert.True(File.Exists(Path.Combine(second, "model.bin")));
    }

    [Fact]
    public async Task RefreshInventory_DirectoryJunctionIsNotASecondReadyArtifact()
    {
        await _workspace.Manager.ImportAsync(_workspace.Package("base"), default);
        var target = Path.GetDirectoryName(_workspace.Manager.GetModelPath("English")!)!;
        var junction = Path.Combine(_workspace.ModelsDirectory, "linked-copy");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Value '{target.Replace("'", "''")}' | Out-Null" })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                _workspace.Manager.RefreshInventoryAsync(default));
            var failures = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
            Assert.Contains(failures, failure =>
                failure.Message == "Linked model directories are unavailable. Re-import a regular model package.");
            Assert.Equal("base", Assert.Single(_workspace.Manager.DownloadedModels).Id);
            Assert.Equal("base", _workspace.Manager.SelectedModelId);
            Assert.True(File.Exists(Path.Combine(target, "model.bin")));
        }
        finally { Directory.Delete(junction); }
    }

    private sealed class StartupContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => _posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class LegacyManager : ILocalTranscriptionModelManager
    {
        public TranscriptionModelPackage? CurrentPackage => null;
        public bool HasCompatibleModel(string language) => true;
        public string? GetModelPath(string language) => "legacy.bin";
        public Task<TranscriptionModelPackage> ImportAsync(string path, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<TranscriptionModelPackage?> ImportBundledAsync(string path, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<TranscriptionModelPackage> DownloadAndImportAsync(Uri uri, CancellationToken token) =>
            throw new NotSupportedException();
    }
}

internal sealed class ModelTestWorkspace : IDisposable
{
    private readonly StorageTestDirectory _directory = new();
    public HttpClient Client { get; }
    public string ModelsDirectory => _directory.PathFor("models");
    public ILocalTranscriptionModelManager Manager { get; private set; }
    public static byte[] Bytes { get; } = [12, 34, 56, 78];

    public ModelTestWorkspace(HttpMessageHandler? handler = null,
        Microsoft.Extensions.Logging.ILogger<LocalTranscriptionModelManager>? logger = null)
    {
        Client = new HttpClient(handler ?? new RejectModelNetworkHandler());
        Manager = new LocalTranscriptionModelManager(ModelsDirectory, Client, logger);
    }

    public void Reload() => Manager = new LocalTranscriptionModelManager(ModelsDirectory, Client);

    public string Package(string id, string[]? languages = null, bool invalidHash = false,
        byte[]? bytes = null, string modelFile = "model.bin",
        Func<TranscriptionModelPackage, TranscriptionModelPackage>? transform = null)
    {
        bytes ??= Bytes;
        var path = _directory.PathFor($"{Guid.NewGuid():N}.zip");
        var manifest = new TranscriptionModelPackage(
            1, id, "Whisper.net 1.9", modelFile,
            invalidHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)),
            "Windows11-x64", languages ?? ["English", "Brazilian Portuguese"]);
        if (transform is not null)
            manifest = transform(manifest);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(manifest));
        using var stream = archive.CreateEntry(modelFile).Open();
        stream.Write(bytes);
        return path;
    }

    public void Dispose()
    {
        Client.Dispose();
        _directory.Dispose();
    }

    private sealed class RejectModelNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network request is permitted.");
    }
}
