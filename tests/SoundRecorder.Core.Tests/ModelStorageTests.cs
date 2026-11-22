using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundRecorder.Infrastructure.Models;

namespace SoundRecorder.Core.Tests;

public sealed class ModelStorageTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();
    private readonly HttpClient _client = new(new RejectNetworkHandler());
    private static readonly byte[] ModelBytes = [12, 34, 56, 78];

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ImportAsync_RepeatedFreshPublicationReleasesPackageAndArtifactHandles(int concurrency)
    {
        for (var batch = 0; batch < 25; batch++)
        {
            var start = batch * concurrency;
            await Task.WhenAll(Enumerable.Range(start, concurrency).Select(Publish));
        }

        async Task Publish(int attempt)
        {
            var models = _directory.PathFor($"publication-{attempt}");
            var logger = new PublicationLogger();
            var manager = new LocalTranscriptionModelManager(models, _client, logger);
            var package = Package(Manifest());
            var failure = await Record.ExceptionAsync(() => manager.ImportAsync(package, default));
            Assert.True(failure is null,
                $"Publication {attempt}: {failure}; HRESULT {failure?.HResult:X}; {string.Join("; ", logger.Messages)}");
            var model = Assert.IsType<string>(manager.GetModelPath("English"));
            using (File.Open(package, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (File.Open(model, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (File.Open(Path.Combine(Path.GetDirectoryName(model)!, "manifest.json"),
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (File.Open(Path.Combine(models, "current.json"),
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(model));
            Assert.Empty(Directory.GetDirectories(models, ".staging-*"));
        }
    }

    private sealed class PublicationLogger : Microsoft.Extensions.Logging.ILogger<LocalTranscriptionModelManager>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ImportAsync_ValidPackage_ActivatesPersistsAndReloadsWithoutNetwork()
    {
        var logger = new StorageTestLogger<LocalTranscriptionModelManager>();
        var modelDirectory = _directory.PathFor("new", "models");
        var manager = new LocalTranscriptionModelManager(modelDirectory, _client, logger);
        Assert.Null(manager.CurrentPackage);
        Assert.Null(manager.GetModelPath("English"));
        var manifest = Manifest();

        Assert.Equal(JsonSerializer.Serialize(manifest),
            JsonSerializer.Serialize(await manager.ImportAsync(Package(manifest), default)));
        Assert.Equal(JsonSerializer.Serialize(manifest), JsonSerializer.Serialize(manager.CurrentPackage));
        var modelPath = Assert.IsType<string>(manager.GetModelPath("english"));
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(JsonSerializer.Serialize(manifest),
            await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(modelPath)!, "manifest.json")));
        Assert.True(manager.HasCompatibleModel("BRAZILIAN PORTUGUESE"));
        Assert.False(manager.HasCompatibleModel("French"));
        Assert.Null(manager.GetModelPath("French"));
        Assert.Empty(Directory.GetDirectories(modelDirectory, ".staging-*"));
        Assert.Empty(Directory.GetFiles(modelDirectory, "*.tmp"));
        Assert.Equal(2, logger.Entries.Count);

        var reloaded = new LocalTranscriptionModelManager(modelDirectory, _client);
        Assert.Equal(manifest.Id, reloaded.CurrentPackage?.Id);
        Assert.Equal(modelPath, reloaded.GetModelPath("English"));
        File.Delete(modelPath);
        Assert.False(reloaded.HasCompatibleModel("English"));
        Assert.Null(reloaded.GetModelPath("English"));
        Assert.Null(new LocalTranscriptionModelManager(modelDirectory, _client).CurrentPackage);
    }

    [Theory]
    [InlineData("version", "The model package is not compatible with this application.")]
    [InlineData("id", "The model package is not compatible with this application.")]
    [InlineData("platform", "The model package is not compatible with this application.")]
    [InlineData("engine", "The model package is not compatible with this application.")]
    [InlineData("languages", "The model package is not compatible with this application.")]
    [InlineData("hash-length", "The model package manifest has an invalid SHA-256 value.")]
    [InlineData("hash-value", "The model package failed its SHA-256 integrity check.")]
    [InlineData("parent", "The model package contains an unsafe file name.")]
    [InlineData("slash", "The model package contains an unsafe file name.")]
    [InlineData("backslash", "The model package contains an unsafe file name.")]
    [InlineData("rooted", "The model package contains an unsafe file name.")]
    public async Task ImportAsync_InvalidPackage_PreservesPreviousWorkingModel(string invalid, string expectedMessage)
    {
        var modelDirectory = _directory.PathFor("models");
        var logger = new StorageTestLogger<LocalTranscriptionModelManager>();
        var manager = new LocalTranscriptionModelManager(modelDirectory, _client, logger);
        var original = Manifest();
        await manager.ImportAsync(Package(original), default);
        var previousPath = manager.GetModelPath("English");
        var marker = await File.ReadAllTextAsync(Path.Combine(modelDirectory, "current.json"));
        var changed = invalid switch
        {
            "version" => original with { FormatVersion = 2 },
            "id" => original with { Id = " " },
            "platform" => original with { Platform = "Linux" },
            "engine" => original with { Engine = "another-engine" },
            "languages" => original with { Languages = [] },
            "hash-length" => original with { Sha256 = "0123" },
            "hash-value" => original with { Sha256 = new string('0', 64) },
            "parent" => original with { ModelFile = "..model.bin" },
            "slash" => original with { ModelFile = "sub/model.bin" },
            "backslash" => original with { ModelFile = @"sub\model.bin" },
            _ => original with { ModelFile = @"C:\model.bin" },
        };

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.ImportAsync(Package(changed), default));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(previousPath, manager.GetModelPath("English"));
        Assert.Equal(ModelBytes, await File.ReadAllBytesAsync(previousPath!));
        Assert.Equal(marker, await File.ReadAllTextAsync(Path.Combine(modelDirectory, "current.json")));
        Assert.Empty(Directory.GetDirectories(modelDirectory, ".staging-*"));
        Assert.Equal(4, logger.Entries.Count);
    }

    [Theory]
    [InlineData("missing-manifest", "The model package does not contain manifest.json.")]
    [InlineData("null-manifest", "The model package manifest is invalid.")]
    [InlineData("missing-model", "The model package does not contain 'model.bin'.")]
    public async Task ImportAsync_IncompleteArchive_ReportsActionableErrorAndCleansStaging(string kind, string message)
    {
        var package = _directory.PathFor("invalid.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            if (kind != "missing-manifest")
            {
                using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                writer.Write(kind == "null-manifest" ? "null" : JsonSerializer.Serialize(Manifest()));
            }
        }

        var modelDirectory = _directory.PathFor("models");
        var manager = new LocalTranscriptionModelManager(modelDirectory, _client);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => manager.ImportAsync(package, default));
        Assert.Equal(message, exception.Message);
        Assert.Null(manager.CurrentPackage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(modelDirectory));
    }

    [Fact]
    public async Task ImportAsync_Update_ReplacesMarkerAndAcceptsCaseInsensitiveCompatibility()
    {
        var models = _directory.PathFor("models");
        var manager = new LocalTranscriptionModelManager(models, _client);
        await manager.ImportAsync(Package(Manifest()), default);
        var previousModel = manager.GetModelPath("English");
        var updated = Manifest() with
        {
            Id = "updated", Engine = "whisper.NET 1.9", Platform = "windows11-X64",
            Sha256 = Convert.ToHexString(SHA256.HashData(ModelBytes)).ToLowerInvariant(),
        };

        await manager.ImportAsync(Package(updated), default);

        Assert.Equal("updated", manager.CurrentPackage?.Id);
        Assert.NotEqual(previousModel, manager.GetModelPath("English"));
        Assert.True(File.Exists(previousModel));
        Assert.Equal("updated", new LocalTranscriptionModelManager(models, _client).CurrentPackage?.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ImportBundledAsync_WithoutExactlyOnePackage_DoesNotActivate(int count)
    {
        var bundled = _directory.PathFor("bundled");
        Directory.CreateDirectory(bundled);
        for (var index = 0; index < count; index++)
        {
            File.Copy(Package(Manifest()), Path.Combine(bundled, $"{index}.zip"));
        }

        await File.WriteAllTextAsync(Path.Combine(bundled, "ignored.txt"), "not a package");
        var manager = new LocalTranscriptionModelManager(_directory.PathFor("models"), _client);
        if (count == 0)
        {
            Assert.Null(await manager.ImportBundledAsync(bundled, default));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => manager.ImportBundledAsync(bundled, default));
            Assert.Equal("Multiple bundled model packages were found. Keep exactly one.", exception.Message);
        }

        Assert.Null(manager.CurrentPackage);
    }

    [Theory]
    [InlineData("English")]
    [InlineData("Brazilian Portuguese")]
    public async Task ImportBundledAsync_OnlyOneLanguageInstalled_ImportsBilingualPackage(string language)
    {
        var manager = new LocalTranscriptionModelManager(_directory.PathFor("models"), _client);
        await manager.ImportAsync(Package(Manifest() with { Languages = [language] }), default);
        var bundled = _directory.PathFor("bundled");
        Directory.CreateDirectory(bundled);
        File.Copy(Package(Manifest() with { Id = "bilingual" }), Path.Combine(bundled, "model.zip"));
        Assert.Equal("bilingual", (await manager.ImportBundledAsync(bundled, default))?.Id);
    }

    [Fact]
    public async Task ImportBundledAsync_MissingDirectoryOrWorkingModel_DoesNotReplaceCurrentModel()
    {
        var manager = new LocalTranscriptionModelManager(_directory.PathFor("models"), _client);
        Assert.Null(await manager.ImportBundledAsync(_directory.PathFor("missing"), default));
        await manager.ImportAsync(Package(Manifest()), default);
        var path = manager.GetModelPath("English");
        Assert.Null(await manager.ImportBundledAsync(_directory.PathFor("missing"), default));
        Assert.Equal(path, manager.GetModelPath("English"));
    }

    [Fact]
    public async Task Constructor_NullMarker_LeavesModelUnavailable()
    {
        var models = _directory.PathFor("models");
        Directory.CreateDirectory(models);
        await File.WriteAllTextAsync(Path.Combine(models, "current.json"), "null");
        Assert.Null(new LocalTranscriptionModelManager(models, _client).CurrentPackage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAndImportAsync_ControlledResponse_ImportsOrReportsFailureAndRemovesDownload(bool fail)
    {
        var package = Package(Manifest());
        var bytes = await File.ReadAllBytesAsync(package);
        var uri = new Uri("https://models.invalid/explicit-package");
        using var client = new HttpClient(new PackageHandler(uri, bytes, fail));
        var logger = new StorageTestLogger<LocalTranscriptionModelManager>();
        var models = _directory.PathFor("models");
        var manager = new LocalTranscriptionModelManager(models, client, logger);
        if (fail)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => manager.DownloadAndImportAsync(uri, default));
            Assert.Null(manager.CurrentPackage);
            Assert.Equal(2, logger.Entries.Count);
        }
        else
        {
            Assert.Equal("test-model", (await manager.DownloadAndImportAsync(uri, default)).Id);
            Assert.True(manager.HasCompatibleModel("English"));
            Assert.Equal(3, logger.Entries.Count);
        }

        Assert.Empty(Directory.GetFiles(models, "*.model-package.zip"));
    }

    [Fact]
    public async Task ImportAsync_PreCanceled_CleansStagingWithoutActivation()
    {
        var models = _directory.PathFor("models");
        var manager = new LocalTranscriptionModelManager(models, _client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ImportAsync(Package(Manifest()), cancellation.Token));
        Assert.Null(manager.CurrentPackage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(models));
    }

    [Fact]
    public async Task DownloadAndImportAsync_CanceledAfterTransfer_DoesNotBeginImportOrReplaceWorkingModel()
    {
        var bytes = await File.ReadAllBytesAsync(Package(Manifest()));
        var uri = new Uri("https://models.invalid/explicit-package");
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new PackageHandler(uri, bytes, false, cancellation));
        var logger = new StorageTestLogger<LocalTranscriptionModelManager>();
        var models = _directory.PathFor("models");
        var manager = new LocalTranscriptionModelManager(models, client, logger);
        await manager.ImportAsync(Package(Manifest()), default);
        var originalPath = manager.GetModelPath("English");
        var importEvents = logger.Entries.ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DownloadAndImportAsync(uri, cancellation.Token));

        Assert.Equal(originalPath, manager.GetModelPath("English"));
        Assert.Equal(4, logger.Entries.Count);
        Assert.DoesNotContain(logger.Entries.Skip(importEvents.Length),
            entry => entry.Event == importEvents[0].Event);
        Assert.Empty(Directory.GetFiles(models, "*.model-package.zip"));
        Assert.Empty(Directory.GetDirectories(models, ".staging-*"));
    }

    private static TranscriptionModelPackage Manifest() => new(
        1, "test-model", "Whisper.net 1.9", "model.bin", Convert.ToHexString(SHA256.HashData(ModelBytes)),
        "Windows11-x64", ["English", "Brazilian Portuguese"]);

    private string Package(TranscriptionModelPackage manifest)
    {
        var path = _directory.PathFor($"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), Encoding.UTF8))
        {
            writer.Write(JsonSerializer.Serialize(manifest));
        }

        using var stream = archive.CreateEntry("model.bin").Open();
        stream.Write(ModelBytes);
        return path;
    }

    public void Dispose()
    {
        _client.Dispose();
        _directory.Dispose();
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network request is permitted.");
    }

    private sealed class PackageHandler(
        Uri expected,
        byte[] bytes,
        bool fail,
        CancellationTokenSource? cancelAfterCopy = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(expected, request.RequestUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = cancelAfterCopy is null
                    ? new ByteArrayContent(bytes)
                    : new StreamContent(new CancelAfterCopyStream(bytes, cancelAfterCopy)),
            });
        }
    }

    private sealed class CancelAfterCopyStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            cancellation.Cancel();
        }
    }
}
