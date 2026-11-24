using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Analysis;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis.Models;

/// <summary>Local explicit setup for the approved Phi-mini/Nomic CPU profile.</summary>
public sealed partial class AnalysisModelSetup : IAnalysisModelSetup
{
    private readonly string cacheDirectory;
    private readonly HttpClient httpClient;
    private readonly AnalysisModelLimits limits;
    private readonly PinnedModelFile[] files;
    private readonly Func<AnalysisModelResources> resources;
    private readonly ILogger<AnalysisModelSetup> logger;
    private long TotalBytes => files.Sum(file => file.Bytes);

    /// <summary>Constructing setup does not touch storage or the network. Budgets must be explicitly configured.</summary>
    public AnalysisModelSetup(string cacheDirectory, HttpClient httpClient, AnalysisModelLimits limits,
        ILogger<AnalysisModelSetup>? logger = null)
        : this(cacheDirectory, httpClient, limits, ProductionFiles(), null, logger)
    {
    }

    internal AnalysisModelSetup(string cacheDirectory, HttpClient httpClient, AnalysisModelLimits limits,
        PinnedModelFile[] files, Func<AnalysisModelResources>? resources, ILogger<AnalysisModelSetup>? logger = null)
    {
        this.cacheDirectory = AnalysisModelPaths.LocalPath(cacheDirectory);
        this.httpClient = httpClient;
        this.limits = limits;
        this.files = files.ToArray();
        this.resources = resources ?? (() => AnalysisModelResources.Read(this.cacheDirectory));
        this.logger = logger ?? NullLogger<AnalysisModelSetup>.Instance;
        if (limits.DiskReserveBytes < 0 || limits.RequiredAvailableMemoryBytes <= 0 ||
            limits.DiskReserveBytes > long.MaxValue - TotalBytes)
            throw new ArgumentOutOfRangeException(nameof(limits), "Configure a nonnegative disk reserve and a positive memory budget.");
        Profile = new("sr-model-phi4-mini-v1", "LLamaSharp 0.30.0 / CPU 0.30.0; Windows ARM64/x64; English/Brazilian Portuguese",
            Array.AsReadOnly(files.Select(file => new AnalysisModelFile(file.Role, file.Name, file.Bytes, file.Sha256,
                file.Role == "generation" ? "MIT" : "Apache-2.0")).ToArray()));
    }

    private static PinnedModelFile[] ProductionFiles()
        => [.. ModelFiles.PhiMiniFiles(), .. ModelFiles.CandidateFiles().Where(file => file.Role == "embedding")];

    /// <inheritdoc/>
    public AnalysisModelProfile Profile { get; }

    /// <inheritdoc/>
    public async Task<AnalysisModelReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directory = await ReadSelectionAsync(cancellationToken);
            if (directory is null)
                return new(AnalysisModelReadinessState.SetupRequired, null, "Explicit model setup is required.");
            await VerifyAsync(directory, null, TotalBytes, cancellationToken);
            Preflight(installing: false);
            return new(AnalysisModelReadinessState.Ready, directory, null);
        }
        catch (AnalysisModelResourceException exception)
        {
            return new(AnalysisModelReadinessState.ResourceUnavailable, null, exception.Message);
        }
        catch (Exception exception) when (IsReadinessFailure(exception))
        {
            return new(AnalysisModelReadinessState.Invalid, null, "The selected analysis profile is unavailable or invalid. Explicitly re-import or download the approved files.");
        }
    }

    private async Task<string?> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        AnalysisModelPaths.RequireLocalUnlinked(cacheDirectory);
        var marker = Path.Combine(cacheDirectory, "selected-profile.txt");
        if (!File.Exists(marker))
            return null;
        AnalysisModelPaths.RequireLocalUnlinked(marker);
        if (new FileInfo(marker).Length > 4096)
            throw new InvalidDataException("Invalid model selection. Explicitly select a verified local directory.");
        return await File.ReadAllTextAsync(marker, cancellationToken);
    }

    private static bool IsReadinessFailure(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException;

    /// <inheritdoc/>
    public Task<string> ImportAsync(string packagePath, IProgress<AnalysisModelSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => InstallAsync(async staging =>
        {
            AnalysisModelPaths.RequireLocalUnlinked(packagePath);
            using var archive = ZipFile.OpenRead(packagePath);
            ValidateArchive(archive);
            long transferred = 0;
            foreach (var file in files)
            {
                var entry = archive.GetEntry(file.Name)!;
                await using var source = entry.Open();
                transferred = await CopyAsync(source, Path.Combine(staging, file.Name), file.Bytes, transferred,
                    progress, cancellationToken);
            }
        }, progress, cancellationToken);

    private void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count != files.Length)
            throw new InvalidDataException("Import a ZIP containing only the two exact pinned weight filenames.");
        foreach (var file in files)
        {
            var matches = archive.Entries.Where(entry => entry.FullName == file.Name).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("The package contains missing, duplicate or unsupported filenames.");
            ValidateEntry(matches[0], file.Bytes);
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry, long expectedBytes)
    {
        if (entry.Length != expectedBytes)
            throw new InvalidDataException("The package entry size does not match the pinned profile.");
        if (((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000))
            throw new InvalidDataException("Archive links are not supported. Import regular pinned weight files.");
    }

    /// <inheritdoc/>
    public Task<string> DownloadAsync(IProgress<AnalysisModelSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => InstallAsync(async staging =>
        {
            long transferred = 0;
            foreach (var file in files)
            {
                using var response = await httpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length.HasValue && length.Value != file.Bytes)
                    throw new InvalidDataException("The reported download size differs from the pinned profile. Explicitly retry.");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                transferred = await CopyAsync(source, Path.Combine(staging, file.Name), file.Bytes, transferred,
                    progress, cancellationToken);
            }
        }, progress, cancellationToken);

    /// <inheritdoc/>
    public async Task SelectDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisModelPaths.RequireLocalUnlinked(cacheDirectory);
        SetupStarted(logger);
        Preflight(installing: false);
        await VerifyAsync(directory, null, TotalBytes, cancellationToken);
        Directory.CreateDirectory(cacheDirectory);
        await ActivateAsync(directory, cancellationToken);
        SetupCompleted(logger);
    }

    private async Task<string> InstallAsync(Func<string, Task> stage, IProgress<AnalysisModelSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisModelPaths.RequireLocalUnlinked(cacheDirectory);
        Preflight(installing: true);
        SetupStarted(logger);
        var staging = Path.Combine(cacheDirectory, $".staging-{Guid.NewGuid():N}");
        var installed = Path.Combine(cacheDirectory, $"profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            await stage(staging);
            await VerifyAsync(staging, progress, TotalBytes, cancellationToken);
            Preflight(installing: false);
            progress?.Report(new(AnalysisModelSetupPhase.Activating, TotalBytes, TotalBytes));
            Directory.Move(staging, installed);
            await ActivateAsync(installed, cancellationToken);
        }
        catch (Exception exception)
        {
            SetupFailed(logger, exception.GetType().Name);
            DeleteOwned(staging);
            DeleteOwned(installed);
            throw;
        }
        progress?.Report(new(AnalysisModelSetupPhase.Completed, TotalBytes, TotalBytes));
        SetupCompleted(logger);
        return installed;
    }

    private void Preflight(bool installing)
    {
        var available = resources();
        if (!available.SupportedPlatform)
            throw new AnalysisModelResourceException("The analysis profile requires native Windows ARM64 or x64 CPU support.");
        if (available.AvailableMemoryBytes < limits.RequiredAvailableMemoryBytes)
            throw new AnalysisModelResourceException("Insufficient or unknown available RAM. Free memory and explicitly retry; preflight cannot guarantee against OOM.");
        var requiredDisk = limits.DiskReserveBytes + (installing ? TotalBytes : 0);
        if (available.AvailableDiskBytes < requiredDisk)
            throw new AnalysisModelResourceException("Insufficient or unknown local disk space. Free space for staging and the configured reserve, then explicitly retry.");
    }

    private async Task ActivateAsync(string directory, CancellationToken cancellationToken)
    {
        var pending = Path.Combine(cacheDirectory, $".selection-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(AnalysisModelPaths.LocalPath(directory));
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, Path.Combine(cacheDirectory, "selected-profile.txt"), true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private static void DeleteOwned(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    private async Task<long> CopyAsync(Stream source, string destination, long expected, long priorBytes,
        IProgress<AnalysisModelSetupProgress>? progress, CancellationToken cancellationToken)
    {
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long transferred = 0;
        progress?.Report(new(AnalysisModelSetupPhase.Transferring, priorBytes, TotalBytes));
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (transferred + count > expected)
                throw new InvalidDataException("The transfer exceeded the pinned size. Retry the complete profile.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            transferred += count;
            progress?.Report(new(AnalysisModelSetupPhase.Transferring, priorBytes + transferred, TotalBytes));
        }
        if (transferred != expected)
            throw new InvalidDataException("The transfer is incomplete. Retry the complete profile.");
        return priorBytes + transferred;
    }

    private async Task VerifyAsync(string directory, IProgress<AnalysisModelSetupProgress>? progress,
        long transferred, CancellationToken cancellationToken)
    {
        AnalysisModelPaths.RequireLocalUnlinked(directory);
        foreach (var file in files)
            AnalysisModelPaths.RequireLocalUnlinked(Path.Combine(directory, file.Name));
        progress?.Report(new(AnalysisModelSetupPhase.Verifying, transferred, TotalBytes));
        await ModelFiles.VerifyAsync(directory, files, cancellationToken);
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(Path.Combine(directory, file.Name));
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan().SequenceEqual(new byte[] { 0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0 }))
                throw new InvalidDataException("Only the pinned GGUF v3 CPU profile is compatible. Re-import the approved files.");
        }
    }
}
