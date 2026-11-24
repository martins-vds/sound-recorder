using System.Net.Http;
using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Security.Cryptography;
using SoundRecorder.Analysis;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Analysis.Models;

namespace SoundRecorder.Core.Tests;

public sealed class AnalysisModelSetupTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Construct_EmptyLocalPath_ProvidesActionableValidation(string path)
    {
        using var client = new HttpClient(new RejectNetwork());
        var error = Assert.Throws<InvalidDataException>(() => new AnalysisModelSetup(path, client, new(10, 20)));
        Assert.Contains("local path", error.Message);
    }

    [Fact]
    public void Construct_PathExceedsBound_RequiresShorterLocalPath()
    {
        using var client = new HttpClient(new RejectNetwork());
        var path = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 100), 50));
        var error = Assert.Throws<InvalidDataException>(() => new AnalysisModelSetup(path, client, new(10, 20)));
        Assert.Contains("shorter local directory", error.Message);
    }

    [Fact]
    public async Task SelectDirectoryAsync_OnlyReserveSpaceRemains_CanSelectWithoutAllocatingNewWeights()
    {
        using var fixture = new Fixture();
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        fixture.Resources = new(10, 20, true);
        await fixture.Setup.SelectDirectoryAsync(selected);
        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
        fixture.Resources = new(10, 19, true);
        var error = await Assert.ThrowsAsync<AnalysisModelResourceException>(() => fixture.Setup.SelectDirectoryAsync(selected));
        Assert.Contains("RAM", error.Message);
        fixture.Resources = new(10, 20, true);
        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public async Task DownloadAsync_ReportedSizeDoesNotMatchPin_RejectsBeforeTransfer(long length)
    {
        var transport = new ControlledDownload { DeclaredLength = length };
        using var fixture = new Fixture(transport: transport);
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        transport.Files["generation"] = fixture.Generation;
        var observations = new List<AnalysisModelSetupProgress>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.DownloadAsync(new InlineProgress(observations.Add)));
        Assert.Contains("reported download size", error.Message);
        Assert.Empty(observations);
        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
    }

    [Fact]
    public async Task DownloadAsync_UnknownResponseLength_ReportsExactAggregateBytesSeparatelyFromVerification()
    {
        var transport = new ControlledDownload { ReportLength = false };
        using var fixture = new Fixture(transport: transport);
        transport.Files["generation"] = fixture.Generation;
        transport.Files["embedding"] = fixture.Embedding;
        var observations = new List<AnalysisModelSetupProgress>();
        await fixture.Setup.DownloadAsync(new InlineProgress(observations.Add));
        Assert.Equal([0L, 4, 8, 12, 16, 16, 20, 24, 28, 32],
            observations.Where(item => item.Phase == AnalysisModelSetupPhase.Transferring).Select(item => item.BytesTransferred));
        Assert.All(observations, item => Assert.Equal(32, item.TotalBytes));
        Assert.Equal(new AnalysisModelSetupProgress(AnalysisModelSetupPhase.Verifying, 32, 32),
            Assert.Single(observations, item => item.Phase == AnalysisModelSetupPhase.Verifying));
        Assert.Equal(AnalysisModelSetupPhase.Completed, observations[^1].Phase);
    }

    [Fact]
    public async Task GetReadinessAsync_BrokenCacheJunction_ReportsInvalidWithoutFallback()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(fixture.Root, "cache");
        using var create = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await create.WaitForExitAsync();
        Assert.Equal(0, create.ExitCode);
        Directory.Delete(target);
        try
        {
            var readiness = await fixture.Setup.GetReadinessAsync();
            Assert.Equal(AnalysisModelReadinessState.Invalid, readiness.State);
            Assert.Null(readiness.Directory);
        }
        finally
        {
            using var remove = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{link}\"")
            {
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            await remove.WaitForExitAsync();
            Assert.Equal(0, remove.ExitCode);
        }
    }

    [Fact]
    public async Task Setup_ExplicitOperations_LogOutcomesWithoutUserPaths()
    {
        var logger = new StorageTestLogger<AnalysisModelSetup>();
        using var fixture = new Fixture { Logger = logger };
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        await fixture.Setup.SelectDirectoryAsync(selected);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(fixture.Package("secret-title.txt")));

        Assert.Equal([6100, 6101, 6100, 6101, 6100, 6102], logger.Entries.Select(entry => entry.Event.Id));
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(fixture.Root, entry.Message);
            Assert.DoesNotContain("secret-title", entry.Message);
            Assert.NotEmpty(entry.Message);
        });
    }

    [Fact]
    public async Task ImportAsync_ResourcesDropDuringTransfer_PreservesPriorSelection()
    {
        using var fixture = new Fixture();
        var prior = await fixture.Setup.ImportAsync(fixture.Package());
        var error = await Assert.ThrowsAsync<AnalysisModelResourceException>(() => fixture.Setup.ImportAsync(fixture.Package(),
            new InlineProgress(item => { if (item.Phase == AnalysisModelSetupPhase.Transferring) fixture.Resources = new(9, 20, true); })));
        Assert.Contains("disk", error.Message);
        fixture.Resources = new(10, 20, true);
        Assert.Equal(prior, (await fixture.Setup.GetReadinessAsync()).Directory);
    }

    [Fact]
    public async Task ImportAsync_ExplicitReplacement_CommitsNewProfileButRetainsPreviousWeights()
    {
        using var fixture = new Fixture();
        var previous = await fixture.Setup.ImportAsync(fixture.Package());

        var replacement = await fixture.Setup.ImportAsync(fixture.Package());

        Assert.NotEqual(previous, replacement);
        Assert.Equal(replacement, (await fixture.CreateSetup().GetReadinessAsync()).Directory);
        Assert.Equal(fixture.Generation, await File.ReadAllBytesAsync(Path.Combine(previous, "generation.gguf")));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Root, "cache")).Length);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("download")]
    [InlineData("select")]
    [InlineData("readiness")]
    public async Task Setup_AlreadyCancelled_DoesNotTouchStorageOrProbeResources(string action)
    {
        using var fixture = new Fixture();
        fixture.ResourceProbe = () => throw new InvalidOperationException("Resources must not be probed.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var setup = fixture.Setup;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (action)
            {
                case "import": await setup.ImportAsync("missing", cancellationToken: cancellation.Token); break;
                case "download": await setup.DownloadAsync(cancellationToken: cancellation.Token); break;
                case "select": await setup.SelectDirectoryAsync("missing", cancellation.Token); break;
                default: await setup.GetReadinessAsync(cancellation.Token); break;
            }
        });
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public async Task ImportAsync_WrongEntrySize_ReportsPinnedSizeFailure(int bytes)
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            archive.GetEntry("generation.gguf")!.Delete();
            using var output = archive.CreateEntry("generation.gguf").Open();
            output.Write(new byte[bytes]);
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(package));
        Assert.Contains("entry size", error.Message);
        Assert.Equal(AnalysisModelReadinessState.SetupRequired, (await fixture.Setup.GetReadinessAsync()).State);
    }

    [Theory]
    [InlineData("wrong.gguf")]
    [InlineData("generation.gguf")]
    public async Task ImportAsync_TwoEntriesWithMissingOrDuplicateRole_ReportsAmbiguousPackage(string name)
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            archive.GetEntry("embedding.gguf")!.Delete();
            using var output = archive.CreateEntry(name).Open();
            output.Write(fixture.Embedding);
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(package));
        Assert.Contains("missing, duplicate or unsupported", error.Message);
    }

    [Fact]
    public async Task ImportAsync_SelectionFileLocked_ReportsStorageFailureAndPreservesPreviousProfile()
    {
        using var fixture = new Fixture();
        var previous = await fixture.Setup.ImportAsync(fixture.Package());
        using (var locked = File.Open(Path.Combine(fixture.Root, "cache", "selected-profile.txt"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => fixture.Setup.ImportAsync(fixture.Package()));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(previous, (await fixture.Setup.GetReadinessAsync()).Directory);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "cache")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "cache"), ".selection-*"));
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    [InlineData(long.MaxValue, 20)]
    public void Construct_InvalidResourceBudgets_RejectsWithoutIO(long reserve, long memory)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.CreateSetup(new(reserve, memory)));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task DownloadAsync_CancelDuringStreaming_PreservesPreviouslySelectedProfile()
    {
        var transport = new ControlledDownload { ReportLength = false };
        using var fixture = new Fixture(transport: transport);
        var previous = await fixture.Setup.ImportAsync(fixture.Package());
        transport.Files["generation"] = fixture.Generation;
        transport.Files["embedding"] = fixture.Embedding;
        using var cancellation = new CancellationTokenSource();
        var observations = new List<AnalysisModelSetupProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Setup.DownloadAsync(new InlineProgress(item =>
        {
            observations.Add(item);
            if (item.BytesTransferred == 4)
                cancellation.Cancel();
        }), cancellation.Token));

        Assert.Equal(previous, (await fixture.Setup.GetReadinessAsync()).Directory);
        Assert.DoesNotContain(observations, item => item.Phase == AnalysisModelSetupPhase.Completed);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "cache")));
    }

    [Fact]
    public async Task GetReadinessAsync_AvailableRamDrops_ReportsResourceErrorWithoutUnselecting()
    {
        using var fixture = new Fixture { Resources = new(42, 20, true) };
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        fixture.Resources = new(1000, 19, true);

        var readiness = await fixture.Setup.GetReadinessAsync();

        Assert.Equal(AnalysisModelReadinessState.ResourceUnavailable, readiness.State);
        Assert.Contains("RAM", readiness.Error!);
        fixture.Resources = new(1000, 20, true);
        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
    }

    [Fact]
    public async Task ImportAsync_ArchiveLinkEntry_RejectsWithoutActivation()
    {
        using var fixture = new Fixture();
        var package = fixture.Package();
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
            archive.GetEntry("generation.gguf")!.ExternalAttributes = unchecked((int)0xA1FF0000);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(package));
        Assert.Contains("regular pinned", error.Message);

        Assert.Equal(AnalysisModelReadinessState.SetupRequired, (await fixture.Setup.GetReadinessAsync()).State);
    }

    [Fact]
    public async Task DownloadAsync_ProductionProfile_UsesApprovedFilesWithoutCallerMetadata()
    {
        var transport = new ControlledDownload { Status = HttpStatusCode.ServiceUnavailable };
        using var client = new HttpClient(transport);
        using var fixture = new Fixture();
        var setup = new AnalysisModelSetup(Path.Combine(fixture.Root, "production"), client, new(0, 1));

        await Assert.ThrowsAsync<HttpRequestException>(() => setup.DownloadAsync());

        Assert.Equal(["https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF/resolve/78eb92a46fc37e6b524df991ed9aca9bc6aa7b80/Phi-4-mini-instruct-Q4_K_M.gguf"],
            transport.Requests);
        Assert.Equal(2, setup.Profile.Files.Count);
        Assert.Equal("sr-model-phi4-mini-v1", setup.Profile.Id);
        Assert.Contains("LLamaSharp 0.30.0", setup.Profile.Runtime);
        Assert.Equal(new AnalysisModelFile("generation", "Phi-4-mini-instruct-Q4_K_M.gguf", 2491874272,
            "88c00229914083cd112853aab84ed51b87bdf6b9ce42f532d8c85c7c63b1730a", "MIT"),
            Assert.Single(setup.Profile.Files, file => file.Role == "generation"));
        Assert.Equal(new AnalysisModelFile("embedding", "nomic-embed-text-v2-moe.Q8_0.gguf", 512225120,
            "06e7a7e594a26985523c18383aba4aad39fe6e14f08ffc6ab5b554e1ccdc3cff", "Apache-2.0"),
            Assert.Single(setup.Profile.Files, file => file.Role == "embedding"));
        Assert.DoesNotContain(setup.Profile.Files, file => file.Name.Contains("qwen", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AnalysisModelReadinessState.SetupRequired, (await setup.GetReadinessAsync()).State);
    }

    [Fact]
    public async Task SelectDirectoryAsync_ExternalVerifiedCache_LeavesWeightsUntouchedAndPreservesSelectionOnFailure()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "generation.gguf"), fixture.Generation);
        await File.WriteAllBytesAsync(Path.Combine(directory, "embedding.gguf"), fixture.Embedding);

        await fixture.Setup.SelectDirectoryAsync(directory);
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Setup.SelectDirectoryAsync(Path.Combine(fixture.Root, "missing")));

        Assert.Equal(directory, (await fixture.CreateSetup().GetReadinessAsync()).Directory);
        Assert.Equal(fixture.Generation, await File.ReadAllBytesAsync(Path.Combine(directory, "generation.gguf")));
        Assert.Equal(fixture.Embedding, await File.ReadAllBytesAsync(Path.Combine(directory, "embedding.gguf")));
    }

    [Theory]
    [InlineData(AnalysisModelSetupPhase.Transferring)]
    [InlineData(AnalysisModelSetupPhase.Verifying)]
    [InlineData(AnalysisModelSetupPhase.Activating)]
    public async Task ImportAsync_CancelBeforeCommit_PreservesPreviousFilesAndOnlyCleansOwnedStaging(AnalysisModelSetupPhase phase)
    {
        using var fixture = new Fixture();
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        var independent = Path.Combine(fixture.Root, "cache", ".staging-independent");
        Directory.CreateDirectory(independent);
        await File.WriteAllTextAsync(Path.Combine(independent, "keep"), "untouched");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Setup.ImportAsync(fixture.Package(),
            new InlineProgress(item => { if (item.Phase == phase) cancellation.Cancel(); }), cancellation.Token));

        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(independent, "keep")));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Root, "cache")).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "cache"), ".selection-*"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("http")]
    public async Task DownloadAsync_FailedTransfer_PreservesPreviousProfile(string failure)
    {
        var transport = new ControlledDownload();
        using var fixture = new Fixture(transport: transport);
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        transport.Files["generation"] = fixture.Generation;
        transport.Files["embedding"] = failure switch
        {
            "hash" => new byte[16],
            "short" => new byte[15],
            "long" => new byte[17],
            _ => fixture.Embedding
        };
        if (failure == "http")
            transport.Status = HttpStatusCode.ServiceUnavailable;
        transport.ReportLength = false;

        var error = await Record.ExceptionAsync(() => fixture.Setup.DownloadAsync());

        Assert.NotNull(error);
        Assert.Contains(failure switch { "http" => "503", "hash" => "SHA-256", _ => "retry" },
            error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "cache")));
    }

    [Theory]
    [InlineData("\\\\server\\models")]
    [InlineData("\\\\?\\C:\\models")]
    [InlineData("\\\\.\\C:\\models")]
    public void Construct_NetworkOrDevicePath_RejectsBeforeAnyAccess(string path)
    {
        using var client = new HttpClient(new RejectNetwork());
        Assert.Throws<InvalidDataException>(() => new AnalysisModelSetup(path, client, new(10, 20)));
    }

    [Fact]
    public async Task ImportAsync_SymbolicLinkedCache_RejectsWithoutWritingToTarget()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var target = Path.Combine(fixture.Root, "untouched");
        Directory.CreateDirectory(target);
        using var junction = Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{Path.Combine(fixture.Root, "cache")}\" \"{target}\"")
        {
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await junction.WaitForExitAsync();
        Assert.Equal(0, junction.ExitCode);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(fixture.Package()));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            using var remove = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c rmdir \"{Path.Combine(fixture.Root, "cache")}\"")
            {
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            await remove.WaitForExitAsync();
            Assert.Equal(0, remove.ExitCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetReadinessAsync_SelectedFilesDisappearOrChange_ReportsInvalidWithoutFallback(bool remove)
    {
        using var fixture = new Fixture();
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        var file = Path.Combine(selected, "embedding.gguf");
        if (remove)
            File.Delete(file);
        else
            await File.WriteAllBytesAsync(file, new byte[16]);

        var readiness = await fixture.CreateSetup().GetReadinessAsync();

        Assert.Equal(AnalysisModelReadinessState.Invalid, readiness.State);
        Assert.Null(readiness.Directory);
        Assert.Contains("import", readiness.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadAsync_ExplicitRequest_TransfersBothPinnedFilesThenVerifiesAndActivates()
    {
        var transport = new ControlledDownload();
        using var fixture = new Fixture(transport: transport);
        transport.Files["generation"] = fixture.Generation;
        transport.Files["embedding"] = fixture.Embedding;
        var observations = new List<AnalysisModelSetupProgress>();

        var installed = await fixture.Setup.DownloadAsync(new InlineProgress(observations.Add));

        Assert.Equal(["https://example.invalid/generation", "https://example.invalid/embedding"], transport.Requests);
        Assert.Equal(installed, (await fixture.Setup.GetReadinessAsync()).Directory);
        Assert.Contains(observations, item => item.Phase == AnalysisModelSetupPhase.Transferring && item.BytesTransferred == 16);
        Assert.Equal(new AnalysisModelSetupProgress(AnalysisModelSetupPhase.Completed, 32, 32), observations[^1]);
    }

    [Theory]
    [InlineData(41, 1000, true)]
    [InlineData(1000, 19, true)]
    [InlineData(1000, 1000, false)]
    [InlineData(-1, 1000, true)]
    [InlineData(1000, -1, true)]
    public async Task ImportAsync_InsufficientOrUnknownResources_RejectsBeforeStaging(long disk, long memory, bool supported)
    {
        using var fixture = new Fixture { Resources = new(disk, memory, supported) };
        var package = fixture.Package();

        var error = await Assert.ThrowsAsync<AnalysisModelResourceException>(() => fixture.Setup.ImportAsync(package));
        Assert.Contains(!supported ? "Windows" : memory < 20 ? "RAM" : "disk", error.Message);

        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "cache")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("generation.gguf")]
    [InlineData("manifest.json")]
    [InlineData("folder/")]
    public async Task ImportAsync_UnexpectedOrAmbiguousEntry_PreservesPreviousProfile(string extra)
    {
        using var fixture = new Fixture();
        var selected = await fixture.Setup.ImportAsync(fixture.Package());
        var package = fixture.Package(extra);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(package));
        Assert.Contains("Import", error.Message);

        Assert.Equal(selected, (await fixture.Setup.GetReadinessAsync()).Directory);
        Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Root, "cache")));
        Assert.Equal(fixture.Generation, await File.ReadAllBytesAsync(Path.Combine(selected, "generation.gguf")));
    }

    [Fact]
    public async Task ImportAsync_HashMatchesButFormatIsIncompatible_DoesNotActivate()
    {
        using var fixture = new Fixture(incompatible: true);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Setup.ImportAsync(fixture.Package()));
        Assert.Contains("Re-import", error.Message);

        Assert.Equal(AnalysisModelReadinessState.SetupRequired, (await fixture.Setup.GetReadinessAsync()).State);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "cache")));
    }

    [Fact]
    public async Task ImportAsync_CompletePinnedPackage_ActivatesVerifiedProfileAndSurvivesRestart()
    {
        using var fixture = new Fixture();
        var observations = new List<AnalysisModelSetupProgress>();
        var installed = await fixture.Setup.ImportAsync(fixture.Package(), new InlineProgress(observations.Add));

        Assert.Equal(AnalysisModelReadinessState.Ready, (await fixture.Setup.GetReadinessAsync()).State);
        Assert.Equal(installed, (await fixture.CreateSetup().GetReadinessAsync()).Directory);
        Assert.Equal(fixture.Generation, await File.ReadAllBytesAsync(Path.Combine(installed, "generation.gguf")));
        Assert.Equal(fixture.Embedding, await File.ReadAllBytesAsync(Path.Combine(installed, "embedding.gguf")));
        Assert.Contains(observations, item => item.Phase == AnalysisModelSetupPhase.Verifying);
        Assert.Equal(new AnalysisModelSetupProgress(AnalysisModelSetupPhase.Completed, 32, 32), observations[^1]);
    }

    [Fact]
    public async Task GetReadinessAsync_UnconfiguredProfile_ReportsSetupRequiredWithoutNetwork()
    {
        using var client = new HttpClient(new RejectNetwork());
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "model-setup-tests", Guid.NewGuid().ToString("N"));
        IAnalysisModelSetup setup = new AnalysisModelSetup(root, client, new AnalysisModelLimits(1024, 1024));

        var readiness = await setup.GetReadinessAsync();

        Assert.Equal(AnalysisModelReadinessState.SetupRequired, readiness.State);
        Assert.Contains("Explicit model setup", readiness.Error!);
        Assert.Null(readiness.Directory);
        Assert.False(Directory.Exists(root));
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No network access is permitted.");
    }

    private sealed class InlineProgress(Action<AnalysisModelSetupProgress> report) : IProgress<AnalysisModelSetupProgress>
    {
        public void Report(AnalysisModelSetupProgress value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PinnedModelFile[] trustedFiles;
        public Fixture(bool incompatible = false, HttpMessageHandler? transport = null)
        {
            Client = new(transport ?? new RejectNetwork());
            if (incompatible)
                Generation[0] = 0;
            trustedFiles = [File("generation", Generation), File("embedding", Embedding)];
        }
        public byte[] Generation { get; } = [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
        public byte[] Embedding { get; } = [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0, 8, 7, 6, 5, 4, 3, 2, 1];
        public string Root { get; } = Path.Combine(Environment.CurrentDirectory, "artifacts", "model-setup-tests", Guid.NewGuid().ToString("N"));
        public HttpClient Client { get; }
        public AnalysisModelResources Resources { get; set; } = new(1000, 1000, true);
        public Func<AnalysisModelResources>? ResourceProbe { get; set; }
        public ILogger<AnalysisModelSetup>? Logger { get; set; }
        public AnalysisModelSetup Setup => CreateSetup();
        public AnalysisModelSetup CreateSetup(AnalysisModelLimits? limits = null) => new(Path.Combine(Root, "cache"), Client,
            limits ?? new AnalysisModelLimits(10, 20),
            trustedFiles,
            () => ResourceProbe?.Invoke() ?? Resources, Logger);
        private static PinnedModelFile File(string role, byte[] bytes)
            => new(role, $"{role}.gguf", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                $"https://example.invalid/{role}");
        public string Package(string? extra = null)
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, $"package-{Guid.NewGuid():N}.zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var output = archive.CreateEntry("generation.gguf").Open())
                output.Write(Generation);
            using (var output = archive.CreateEntry("embedding.gguf").Open())
                output.Write(Embedding);
            if (extra is not null)
                archive.CreateEntry(extra);
            return path;
        }
        public void Dispose()
        {
            Client.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
    }
    }

    private sealed class ControlledDownload : HttpMessageHandler
    {
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<string> Requests { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public bool ReportLength { get; set; } = true;
    public long? DeclaredLength { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.AbsoluteUri);
        var response = new HttpResponseMessage(Status);
        if (Status == HttpStatusCode.OK)
        {
            response.Content = ReportLength
                ? new ByteArrayContent(Files[request.RequestUri.AbsolutePath.Trim('/')])
                : new StreamContent(new ControlledStream(Files[request.RequestUri.AbsolutePath.Trim('/')]));
            if (!ReportLength)
                response.Content.Headers.ContentLength = null;
            if (DeclaredLength.HasValue)
                response.Content.Headers.ContentLength = DeclaredLength;
        }
        return Task.FromResult(response);
    }
    }

    private sealed class ControlledStream(byte[] bytes) : MemoryStream(bytes)
    {
    public override bool CanSeek => false;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(4, buffer.Length)], cancellationToken);
    }
}
