using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;

namespace SoundRecorder.Ui.TestHost;

internal sealed class FixtureModelDownloads(string root, bool unknownLength) : HttpMessageHandler
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";
    private static readonly byte[] Bytes = CreateBytes();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Bytes));
    public bool Controlled { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { Scheme: "https", Host: "huggingface.co" } uri)
        {
            throw new InvalidOperationException("Only controlled model fixture requests are available.");
        }
        File.WriteAllText(Path.Combine(root, "network-requested.txt"), "controlled fixture request");
        HttpContent content;
        if (uri.AbsolutePath.EndsWith("/revision/v4", StringComparison.Ordinal))
        {
            content = new StringContent(JsonSerializer.Serialize(new { sha = Revision }), Encoding.UTF8, "application/json");
        }
        else if (uri.AbsolutePath.StartsWith($"/api/models/sandrohanea/whisper.net/tree/{Revision}/", StringComparison.Ordinal))
        {
            var directory = uri.AbsolutePath.Split('/').Last();
            content = new StringContent(JsonSerializer.Serialize(new[] { "base", "small" }.Select(family => new
            {
                type = "file",
                path = $"{directory}/ggml-{family}.bin",
                size = Bytes.Length,
                lfs = new { oid = Hash, size = Bytes.Length },
            })), Encoding.UTF8, "application/json");
        }
        else if (uri.AbsolutePath.StartsWith($"/sandrohanea/whisper.net/resolve/{Revision}/", StringComparison.Ordinal))
        {
            var hold = Controlled && uri.AbsolutePath.EndsWith("/ggml-small.bin", StringComparison.Ordinal);
            content = new StreamContent(new ControlledModelStream(root, Bytes, hold));
            if (!unknownLength || !hold)
            {
                content.Headers.ContentLength = Bytes.Length;
            }
        }
        else
        {
            throw new InvalidOperationException("The model fixture request did not use the pinned revision.");
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private static byte[] CreateBytes()
    {
        var bytes = new byte[4096];
        new byte[] { 0x6C, 0x6D, 0x67, 0x67 }.CopyTo(bytes, 0);
        return bytes;
    }

    private sealed class ControlledModelStream(string root, byte[] bytes, bool hold) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            while (hold && _position >= 1024 && !File.Exists(Path.Combine(root, "model-transfer-release.txt")))
            {
                await Task.Delay(25, token);
            }
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            if (hold && _position < 1024)
            {
                count = Math.Min(count, 1024 - _position);
            }
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class FixtureModelManager(ILocalTranscriptionModelManager manager, string root, bool holdVerification)
    : ILocalTranscriptionModelManager
{
    public string? SelectedModelId => manager.SelectedModelId;
    public TranscriptionModelPackage? CurrentPackage => manager.CurrentPackage;
    public IReadOnlyList<DownloadedTranscriptionModel> DownloadedModels => manager.DownloadedModels;
    public bool HasCompatibleModel(string language) => manager.HasCompatibleModel(language);
    public string? GetModelPath(string language) => manager.GetModelPath(language);
    public string? GetModelPath(string language, string? pinnedModelId) => manager.GetModelPath(language, pinnedModelId);
    public async Task SelectAsync(string id, CancellationToken token)
    {
        while (File.Exists(Path.Combine(root, "hold-model-selection.txt")) &&
               !File.Exists(Path.Combine(root, "release-model-selection.txt")))
        {
            await Task.Delay(25, token);
        }
        await manager.SelectAsync(id, token);
    }
    public Task RefreshInventoryAsync(CancellationToken token) => manager.RefreshInventoryAsync(token);
    public Task<DownloadedTranscriptionModel> RegisterAsync(string path, CancellationToken token) => manager.RegisterAsync(path, token);
    public Task<TranscriptionModelPackage> ImportAsync(string path, CancellationToken token) => manager.ImportAsync(path, token);
    public Task<TranscriptionModelPackage?> ImportBundledAsync(string path, CancellationToken token) => manager.ImportBundledAsync(path, token);
    public Task<TranscriptionModelPackage> DownloadAndImportAsync(Uri uri, CancellationToken token) => manager.DownloadAndImportAsync(uri, token);
    public Task<DownloadedTranscriptionModel> DownloadAsync(ModelCatalogEntry entry, IProgress<ModelDownloadProgress> progress,
        CancellationToken token) => manager.DownloadAsync(entry, new VerificationProgress(progress, root, holdVerification, token), token);

    private sealed class VerificationProgress(IProgress<ModelDownloadProgress> progress, string root, bool hold, CancellationToken token)
        : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress update)
        {
            progress.Report(update);
            if (hold && update.Phase == ModelDownloadPhase.Verifying)
            {
                while (!File.Exists(Path.Combine(root, "model-verification-release.txt")))
                {
                    token.ThrowIfCancellationRequested();
                    Thread.Sleep(25);
                }
            }
        }
    }
}
