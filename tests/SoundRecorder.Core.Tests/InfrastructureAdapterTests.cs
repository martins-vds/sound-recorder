using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Recovery;

namespace SoundRecorder.Core.Tests;

public sealed class InfrastructureAdapterTests
{
    [Fact]
    public async Task BundledModelPackageIsImportedOnFirstStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var bundledDirectory = Path.Combine(directory, "publish", "models");
        var activeDirectory = Path.Combine(directory, "local", "models");
        Directory.CreateDirectory(bundledDirectory);
        File.Move(CreatePackage(directory, "Whisper.net 1.9"), Path.Combine(bundledDirectory, "tiny.zip"));
        var manager = new LocalTranscriptionModelManager(
            activeDirectory,
            new HttpClient());

        var imported = await manager.ImportBundledAsync(
            bundledDirectory,
            CancellationToken.None);

        Assert.NotNull(imported);
        Assert.True(manager.HasCompatibleModel("English"));
        Assert.True(manager.HasCompatibleModel("Brazilian Portuguese"));
    }

    [Fact]
    public async Task ExistingLocalModelPreventsBundledModelReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var bundledDirectory = Path.Combine(directory, "publish", "models");
        var activeDirectory = Path.Combine(directory, "local", "models");
        Directory.CreateDirectory(bundledDirectory);
        var existingPackage = CreatePackage(directory, "Whisper.net 1.9", "existing-model");
        var bundledSource = Path.Combine(directory, "bundled");
        Directory.CreateDirectory(bundledSource);
        var bundledPackage = CreatePackage(bundledSource, "Whisper.net 1.9", "bundled-model");
        File.Move(bundledPackage, Path.Combine(bundledDirectory, "tiny.zip"));
        var manager = new LocalTranscriptionModelManager(activeDirectory, new HttpClient());
        await manager.ImportAsync(existingPackage, CancellationToken.None);

        var imported = await manager.ImportBundledAsync(
            bundledDirectory,
            CancellationToken.None);

        Assert.Null(imported);
        Assert.Equal("existing-model", manager.CurrentPackage?.Id);
    }

    [Fact]
    public void FinalizationReadsPartialCaptureOnlyAfterWriterIsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, "partial.wav");
        var writer = new WaveFileWriter(
            partialPath,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1));
        writer.WriteSamples(new float[48_000], 0, 48_000);
        writer.Flush();

        Assert.Throws<IOException>(
            () => WaveRecordingFinalizerAccessor.Finalize(
                partialPath,
                null,
                AudioSource.MicrophoneOnly,
                Path.Combine(directory, "before-close.wav")));

        writer.Dispose();
        var outputPath = Path.Combine(directory, "after-close.wav");
        WaveRecordingFinalizerAccessor.Finalize(
            partialPath,
            null,
            AudioSource.MicrophoneOnly,
            outputPath);

        using var reader = new WaveFileReader(outputPath);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
        Assert.Equal(48_000, reader.WaveFormat.SampleRate);
    }

    [Fact]
    public async Task RecoveryRepairsIeeeFloatWaveDataChunkBeforeFinalizing()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, "partial.wav");
        using (var writer = new WaveFileWriter(
                   partialPath,
                   WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1)))
        {
            var samples = Enumerable.Range(0, 48_000)
                .Select(index => (float)Math.Sin(index / 48_000d * Math.PI * 440))
                .ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
        }

        var bytes = await File.ReadAllBytesAsync(partialPath);
        Assert.True(FindSequence(bytes, Encoding.ASCII.GetBytes("fact")) >= 0);
        var checkpointPath = Path.Combine(directory, "recording.capture.json");
        await File.WriteAllTextAsync(
            checkpointPath,
            JsonSerializer.Serialize(new
            {
                RecordingId = Guid.NewGuid(),
                MicrophonePath = partialPath,
                SystemAudioPath = (string?)null,
                DestinationFolder = directory,
                Title = "recovery",
                Language = "English",
                Source = AudioSource.MicrophoneOnly,
                CapturedTicks = TimeSpan.FromSeconds(1).Ticks,
                LastSavedAt = DateTimeOffset.UtcNow,
            }));
        var storage = new LocalRecoveryStorage(directory);
        var candidate = Assert.Single(await storage.FindAsync(CancellationToken.None));

        var recovered = await storage.RecoverAsync(candidate, CancellationToken.None);

        Assert.Equal(RecordingOutcome.Recovered, recovered.Outcome);
        using var reader = new WaveFileReader(recovered.AudioPath);
        Assert.Equal(48_000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.True(reader.TotalTime >= TimeSpan.FromSeconds(.99));
    }

    [Fact]
    public async Task ModelActivationRejectsAnUnsupportedEngineVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var packagePath = CreatePackage(directory, "Whisper.net 2.0");
        var manager = new LocalTranscriptionModelManager(
            Path.Combine(directory, "models"),
            new HttpClient());

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync(packagePath, CancellationToken.None));

        Assert.Contains("not compatible", error.Message);
        Assert.False(manager.HasCompatibleModel("English"));
    }

    private static string CreatePackage(
        string directory,
        string engine,
        string id = "test-model")
    {
        var packagePath = Path.Combine(directory, "model.zip");
        var modelBytes = Encoding.UTF8.GetBytes("model");
        var package = new TranscriptionModelPackage(
            1,
            id,
            engine,
            "model.bin",
            Convert.ToHexString(SHA256.HashData(modelBytes)),
            "Windows11-x64",
            ["English", "Brazilian Portuguese"]);
        using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var model = archive.CreateEntry("model.bin").Open())
        {
            model.Write(modelBytes);
        }

        using var manifest = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifest, package);
        return packagePath;
    }

    private static int FindSequence(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }

        return -1;
    }
}

internal static class WaveRecordingFinalizerAccessor
{
    public static void Finalize(
        string? microphonePath,
        string? systemAudioPath,
        AudioSource source,
        string outputPath)
    {
        var method = typeof(LocalTranscriptionModelManager).Assembly
            .GetType("SoundRecorder.Infrastructure.Audio.WaveRecordingFinalizer")!
            .GetMethod(
                "Finalize",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<string?, string?, AudioSource, string>>();
        method(microphonePath, systemAudioPath, source, outputPath);
    }
}
