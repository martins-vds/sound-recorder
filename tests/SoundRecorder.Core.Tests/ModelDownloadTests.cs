using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;

namespace SoundRecorder.Core.Tests;

public sealed class ModelDownloadTests
{
    [Fact]
    public async Task DownloadAsync_ReportsBytesAndVerifiesRawArtifactBeforeNonactivatingRegistration()
    {
        var handler = new ControlledModelDownload();
        using var workspace = new ModelTestWorkspace(handler);
        await workspace.Manager.ImportAsync(workspace.Package("tiny"), default);
        var observations = new List<ModelDownloadProgress>();
        var progress = new InlineModelProgress(value =>
        {
            observations.Add(value);
            if (value.Phase != ModelDownloadPhase.Completed)
            {
                Assert.DoesNotContain(workspace.Manager.DownloadedModels, model => model.Id == handler.Entry.Key);
                Assert.DoesNotContain(new LocalTranscriptionModelManager(workspace.ModelsDirectory, workspace.Client)
                    .DownloadedModels, model => model.Id == handler.Entry.Key);
            }
        });

        var failure = await Record.ExceptionAsync(() =>
            workspace.Manager.DownloadAsync(handler.Entry, progress, default));

        Assert.Null(failure);
        Assert.Equal(new ModelDownloadProgress(ModelDownloadPhase.ResolvingMetadata, 0, null), observations[0]);
        Assert.Equal(new ModelDownloadProgress(ModelDownloadPhase.Downloading, 0, 4096), observations[1]);
        Assert.Contains(observations, value => value.BytesTransferred == 1024 && value.Percent == 25);
        Assert.Contains(observations, value => value.Phase == ModelDownloadPhase.Verifying);
        Assert.Equal(ModelDownloadPhase.Completed, observations[^1].Phase);
        Assert.Equal(4096, observations[^1].BytesTransferred);
        Assert.Equal(4096, observations[^1].TotalBytes);
        Assert.Equal(["English", "Brazilian Portuguese"],
            Assert.Single(workspace.Manager.DownloadedModels, model => model.Id == handler.Entry.Key).Languages);
        var model = Assert.Single(workspace.Manager.DownloadedModels, model => model.Id == handler.Entry.Key);
        Assert.Equal(handler.Bytes, await File.ReadAllBytesAsync(model.ModelPath));
        Assert.Equal(handler.Entry.DisplayName, model.DisplayName);
        Assert.Equal("tiny", workspace.Manager.SelectedModelId);
        workspace.Reload();
        Assert.Equal(2, workspace.Manager.DownloadedModels.Count);
        Assert.Equal("tiny", workspace.Manager.SelectedModelId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("interrupted transfer")]
    [InlineData("SHA-256 mismatch")]
    [InlineData("incompatible model")]
    public async Task DownloadAsync_FailurePreservesSelectionAndCleansOnlyOwnedStaging(string failureKind)
    {
        var handler = new ControlledModelDownload();
        handler.Interrupted = failureKind == "interrupted transfer";
        handler.HashMismatch = failureKind == "SHA-256 mismatch";
        if (failureKind == "incompatible model")
            handler.Bytes[0] = 0;
        using var workspace = new ModelTestWorkspace(handler);
        await workspace.Manager.ImportAsync(workspace.Package("base"), default);
        var independent = Path.Combine(workspace.ModelsDirectory, ".staging-unrelated");
        Directory.CreateDirectory(independent);
        var otherFile = Path.Combine(independent, "do-not-delete.bin");
        await File.WriteAllBytesAsync(otherFile, [1, 2]);
        var marker = await File.ReadAllTextAsync(Path.Combine(workspace.ModelsDirectory, "current.json"));

        var failure = await Record.ExceptionAsync(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), default));

        Assert.NotNull(failure);
        Assert.Contains("retry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("base", workspace.Manager.SelectedModelId);
        Assert.Equal("base", Assert.Single(workspace.Manager.DownloadedModels).Id);
        Assert.Equal(marker, await File.ReadAllTextAsync(Path.Combine(workspace.ModelsDirectory, "current.json")));
        Assert.Equal([1, 2], await File.ReadAllBytesAsync(otherFile));
        Assert.Equal([independent], Directory.GetDirectories(workspace.ModelsDirectory, ".staging-*"));
    }

    [Fact]
    public async Task DownloadAsync_UnknownLengthKeepsPercentIndeterminateThroughVerifiedCompletion()
    {
        var handler = new ControlledModelDownload { KnownLength = false };
        using var workspace = new ModelTestWorkspace(handler);
        var observations = new List<ModelDownloadProgress>();
        await workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(observations.Add), default);

        Assert.Contains(observations, value => value.BytesTransferred == 1024);
        Assert.All(observations, value =>
        {
            Assert.Null(value.TotalBytes);
            Assert.Null(value.Percent);
        });
        Assert.Equal(ModelDownloadPhase.Completed, observations[^1].Phase);
        Assert.Null(workspace.Manager.SelectedModelId);
    }

    [Fact]
    public async Task DownloadAsync_CancelDuringTransferPreservesSelectionAndIndependentFiles()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledModelDownload
        {
            BeforeSecondChunk = () => { started.TrySetResult(); return blocked.Task; }
        };
        using var workspace = new ModelTestWorkspace(handler);
        await workspace.Manager.ImportAsync(workspace.Package("base"), default);
        using var cancellation = new CancellationTokenSource();
        var download = workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(workspace.Manager.DownloadedModels);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("base", workspace.Manager.SelectedModelId);
        Assert.Single(workspace.Manager.DownloadedModels);
        Assert.Empty(Directory.GetDirectories(workspace.ModelsDirectory, ".staging-*"));
    }

    [Theory]
    [InlineData("malformed JSON")]
    [InlineData("wrong tree shape")]
    [InlineData("conflicting size")]
    [InlineData("directory instead of artifact")]
    public async Task DownloadAsync_RejectsInvalidTrustedMetadataBeforeRequestingArtifact(string invalid)
    {
        var handler = new ControlledModelDownload();
        handler.MetadataBody = invalid switch
        {
            "malformed JSON" => "[",
            "wrong tree shape" => "{}",
            _ => JsonSerializer.Serialize(new[]
            {
                new
                {
                    type = invalid == "directory instead of artifact" ? "directory" : "file",
                    path = "classic/ggml-base.bin",
                    size = invalid == "conflicting size" ? 1 : 4096,
                    lfs = new { oid = Convert.ToHexString(SHA256.HashData(handler.Bytes)), size = 4096 }
                }
            })
        };
        using var workspace = new ModelTestWorkspace(handler);
        await workspace.Manager.ImportAsync(workspace.Package("tiny"), default);

        var failure = await Record.ExceptionAsync(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), default));

        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("retry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("tiny", workspace.Manager.SelectedModelId);
        Assert.Single(workspace.Manager.DownloadedModels);
        Assert.Empty(Directory.GetDirectories(workspace.ModelsDirectory, ".staging-*"));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("tree")]
    [InlineData("artifact")]
    public async Task DownloadAsync_HttpFailureCannotBeTreatedAsVerifiedContent(string stage)
    {
        var handler = new ControlledModelDownload { FailedStage = stage };
        using var workspace = new ModelTestWorkspace(handler);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), default));
        Assert.Equal("The model download failed. Check the connection and retry.", failure.Message);
        Assert.IsType<HttpRequestException>(failure.InnerException);
        Assert.Empty(workspace.Manager.DownloadedModels);
        Assert.Null(workspace.Manager.SelectedModelId);
        Assert.Equal(stage == "revision" ? 1 : stage == "tree" ? 2 : 3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("declared", "The reported model size does not match trusted metadata. Retry the download.")]
    [InlineData("oversized", "The model transfer exceeded its expected size. Retry the download.")]
    [InlineData("short", "The model download is incomplete. Retry the download.")]
    public async Task DownloadAsync_RejectsIncorrectTransferSizesBeforeRegistration(string kind, string message)
    {
        var handler = new ControlledModelDownload
        {
            KnownLength = kind == "declared",
            ReportedLength = kind == "declared" ? 4095 : null,
            ExpectedSize = kind == "oversized" ? 4095 : kind == "short" ? 4097 : 4096
        };
        using var workspace = new ModelTestWorkspace(handler);
        var progress = new List<ModelDownloadProgress>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(progress.Add), default));
        Assert.Equal(message, error.Message);
        Assert.DoesNotContain(progress, value => value.Phase == ModelDownloadPhase.Completed);
        Assert.Empty(workspace.Manager.DownloadedModels);
        Assert.Empty(Directory.GetDirectories(workspace.ModelsDirectory, ".staging-*"));
    }

    [Theory]
    [InlineData("[]", "Model source metadata is missing. Retry the download.")]
    [InlineData("{}", "Model source metadata is missing. Retry the download.")]
    [InlineData("{\"sha\":null}", "Model source metadata is missing. Retry the download.")]
    [InlineData("{\"sha\":42}", "Model source metadata is missing. Retry the download.")]
    [InlineData("{\"sha\":\"0\"}", "Model source revision is invalid. Retry the download.")]
    [InlineData("{\"sha\":\"0123456789abcdef0123456789abcdef0123456G\"}", "Model source revision is invalid. Retry the download.")]
    public async Task DownloadAsync_InvalidRevisionNeverRequestsMetadataOrBinary(string body, string message)
    {
        var handler = new ControlledModelDownload { RevisionBody = body };
        using var workspace = new ModelTestWorkspace(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), default));
        Assert.Equal(message, error.Message);
        Assert.Single(handler.Requests);
        Assert.Empty(workspace.Manager.DownloadedModels);
    }

    [Theory]
    [InlineData("missing-path", "The model is missing from trusted source metadata. Retry the download.")]
    [InlineData("wrong-path-type", "The model is missing from trusted source metadata. Retry the download.")]
    [InlineData("different-path", "The model is missing from trusted source metadata. Retry the download.")]
    [InlineData("duplicate", "The model source metadata is ambiguous. Retry the download.")]
    [InlineData("missing-type", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("wrong-type", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("missing-size", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("wrong-size-type", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("missing-lfs", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("wrong-lfs-type", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("missing-oid", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("wrong-oid-type", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("missing-lfs-size", "Expected model integrity metadata is missing. Retry the download.")]
    [InlineData("zero-size", "Expected model integrity metadata is invalid. Retry the download.")]
    [InlineData("bad-digest", "Expected model integrity metadata is invalid. Retry the download.")]
    public async Task DownloadAsync_InvalidArtifactMetadataIsNotAReadyChoice(string kind, string message)
    {
        var handler = new ControlledModelDownload();
        var file = System.Text.Json.Nodes.JsonNode.Parse(handler.ValidMetadata)!.AsArray()[0]!;
        switch (kind)
        {
            case "missing-path": file.AsObject().Remove("path"); break;
            case "wrong-path-type": file["path"] = 7; break;
            case "different-path": file["path"] = "classic/other.bin"; break;
            case "missing-type": file.AsObject().Remove("type"); break;
            case "wrong-type": file["type"] = 7; break;
            case "missing-size": file.AsObject().Remove("size"); break;
            case "wrong-size-type": file["size"] = "4096"; break;
            case "missing-lfs": file.AsObject().Remove("lfs"); break;
            case "wrong-lfs-type": file["lfs"] = "not metadata"; break;
            case "missing-oid": file["lfs"]!.AsObject().Remove("oid"); break;
            case "wrong-oid-type": file["lfs"]!["oid"] = 7; break;
            case "missing-lfs-size": file["lfs"]!.AsObject().Remove("size"); break;
            case "zero-size": file["size"] = 0; file["lfs"]!["size"] = 0; break;
            case "bad-digest": file["lfs"]!["oid"] = "0" + new string('G', 63); break;
        }
        handler.MetadataBody = kind == "duplicate"
            ? $"[{file.ToJsonString()},{file.ToJsonString()}]" : $"[null,{file.ToJsonString()}]";
        using var workspace = new ModelTestWorkspace(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), default));
        Assert.Equal(message, error.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(workspace.Manager.DownloadedModels);
    }

    [Fact]
    public async Task DownloadAsync_CatalogForgeryAndPreCancellationNeverMakeARequest()
    {
        var handler = new ControlledModelDownload();
        using var workspace = new ModelTestWorkspace(handler);
        foreach (var entry in new[]
        {
            handler.Entry with { Key = "unknown" },
            handler.Entry with { DownloadUri = new Uri("https://untrusted.invalid/model.bin") }
        })
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                workspace.Manager.DownloadAsync(entry, new InlineModelProgress(_ => { }), default));
            Assert.Equal("Choose a supported built-in catalog entry before downloading.", error.Message);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(_ => { }), cancellation.Token));
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.ModelsDirectory));
    }

    [Fact]
    public async Task DownloadAsync_PublishesVerifiedManifestWithoutRenamingAnOpenArtifactDirectory()
    {
        var handler = new ControlledModelDownload();
        using var workspace = new ModelTestWorkspace(handler);
        Microsoft.Win32.SafeHandles.SafeFileHandle? directoryReader = null;
        try
        {
            var downloaded = await workspace.Manager.DownloadAsync(handler.Entry, new InlineModelProgress(value =>
            {
                if (value.Phase != ModelDownloadPhase.Verifying)
                    return;
                var directory = Assert.Single(Directory.GetDirectories(workspace.ModelsDirectory));
                const uint listDirectory = 1;
                const uint shareReadWriteWithoutDelete = 3;
                const uint openExisting = 3;
                const uint backupSemantics = 0x02000000;
                directoryReader = OpenDirectoryForReading(directory, listDirectory, shareReadWriteWithoutDelete,
                    IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
                Assert.False(directoryReader.IsInvalid);
                Assert.Empty(new LocalTranscriptionModelManager(workspace.ModelsDirectory, workspace.Client).DownloadedModels);
            }), default);

            Assert.Equal(downloaded, Assert.Single(workspace.Manager.DownloadedModels));
            Assert.Null(workspace.Manager.SelectedModelId);
            var path = Path.GetDirectoryName(downloaded.ModelPath)!;
            Assert.True(File.Exists(Path.Combine(path, "manifest.json")));
            var deniedRename = Assert.Throws<IOException>(() => Directory.Move(path, $"{path}-renamed"));
            var nativeError = deniedRename.HResult & 0xffff;
            Assert.True(nativeError is 5 or 32, $"Unexpected directory rename error: {deniedRename}");
        }
        finally { directoryReader?.Dispose(); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryForReading(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}

internal sealed class InlineModelProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
{
    public void Report(ModelDownloadProgress value) => report(value);
}

internal sealed class ControlledModelDownload : HttpMessageHandler
{
    public const string Revision = "0123456789abcdef0123456789abcdef01234567";
    public ModelCatalogEntry Entry { get; set; } = TranscriptionModelCatalog.Find("whisper.net:base:NoQuantization")!;
    public byte[] Bytes { get; } = CreateBytes();
    public List<Uri> Requests { get; } = [];
    public bool KnownLength { get; set; } = true;
    public bool HashMismatch { get; set; }
    public bool Interrupted { get; set; }
    public bool RejectRequests { get; set; }
    public string? MetadataBody { get; set; }
    public string? RevisionBody { get; set; }
    public string? FailedStage { get; set; }
    public long? ReportedLength { get; set; }
    public long? ExpectedSize { get; set; }
    public Func<Task>? BeforeSecondChunk { get; set; }
    public string ValidMetadata => JsonSerializer.Serialize(new[]
    {
        new
        {
            type = "file", path = Entry.DownloadUri.AbsolutePath.Split("/v4/")[1],
            size = ExpectedSize ?? Bytes.Length,
            oid = "not-the-content-hash", xetHash = "not-the-content-hash",
            lfs = new
            {
                oid = HashMismatch ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Bytes)),
                size = ExpectedSize ?? Bytes.Length
            }
        }
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Content);
        var uri = request.RequestUri!;
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("huggingface.co", uri.Host);
        Requests.Add(uri);
        if (RejectRequests)
            throw new InvalidOperationException("The network is disconnected.");
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (uri.AbsolutePath == "/api/models/sandrohanea/whisper.net/revision/v4")
        {
            response.StatusCode = FailedStage == "revision" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            response.Content = new StringContent(RevisionBody ?? JsonSerializer.Serialize(new { sha = Revision }));
        }
        else if (uri.AbsolutePath.StartsWith($"/api/models/sandrohanea/whisper.net/tree/{Revision}/"))
        {
            Assert.Equal("?recursive=false&expand=false", uri.Query);
            response.StatusCode = FailedStage == "tree" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            response.Content = new StringContent(MetadataBody ?? ValidMetadata);
        }
        else
        {
            Assert.Equal(Entry.DownloadUri.AbsoluteUri.Replace("/v4/", $"/{Revision}/"), uri.AbsoluteUri);
            response.StatusCode = FailedStage == "artifact" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            response.Content = new StreamContent(new ChunkedModelStream(Bytes, this));
            if (KnownLength)
                response.Content.Headers.ContentLength = ReportedLength ?? Bytes.Length;
        }
        return Task.FromResult(response);
    }

    private static byte[] CreateBytes()
    {
        var bytes = new byte[4096];
        bytes[0] = 0x6c; bytes[1] = 0x6d; bytes[2] = 0x67; bytes[3] = 0x67;
        return bytes;
    }

    private sealed class ChunkedModelStream(byte[] bytes, ControlledModelDownload handler) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == 1024)
            {
                if (handler.Interrupted)
                    throw new IOException("Controlled transfer interruption.");
                if (handler.BeforeSecondChunk is not null)
                    await handler.BeforeSecondChunk().WaitAsync(cancellationToken);
            }
            var count = Math.Min(1024, Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
