using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Recovery;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class InfrastructureAdapterTests
{
    [Fact]
    public async Task TranscribeAsync_48KHzRecording_Processes16KHzMonoWave()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audioPath = Path.Combine(directory, "recording.wav");
        using (var writer = new WaveFileWriter(audioPath, new WaveFormat(48_000, 16, 2)))
        {
            writer.Write(new byte[48_000 * 2 * 2], 0, 48_000 * 2 * 2);
        }

        string? temporaryAudioPath = null;
        var logger = new RecordingLogger<WhisperLocalTranscriber>();
        var transcriber = new WhisperLocalTranscriber(
            new TestModelStore(),
            logger,
            (modelPath, language, audio, cancellationToken) =>
            {
                temporaryAudioPath = Assert.IsType<FileStream>(audio).Name;
                using var reader = new WaveFileReader(audio);
                Assert.Equal(16_000, reader.WaveFormat.SampleRate);
                Assert.Equal(16, reader.WaveFormat.BitsPerSample);
                Assert.Equal(1, reader.WaveFormat.Channels);
                Assert.True(reader.TotalTime >= TimeSpan.FromSeconds(.99));
                return Task.FromResult<IReadOnlyList<TranscriptSegment>>([]);
            });
        var recording = new SavedRecording(
            Guid.NewGuid(),
            audioPath,
            "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(1));

        var document = await transcriber.TranscribeAsync(
            recording,
            CancellationToken.None);

        Assert.Empty(document.Segments);
        Assert.NotNull(temporaryAudioPath);
        Assert.False(File.Exists(temporaryAudioPath));
        Assert.Contains(700, logger.EventIds);
        Assert.Contains(701, logger.EventIds);
    }

    [Fact]
    public async Task TranscribeAsync_WhisperEndRoundsPastDuration_PreservesSegmentAtDuration()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audioPath = Path.Combine(directory, "recording.wav");
        using (var writer = new WaveFileWriter(audioPath, new WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[48_000 * 2], 0, 48_000 * 2);
        }

        var duration = TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(1.5);
        var transcriber = new WhisperLocalTranscriber(
            new TestModelStore(),
            null,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new TranscriptSegment(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    "hello"),
            ]));
        var recording = new SavedRecording(
            Guid.NewGuid(),
            audioPath,
            "English",
            RecordingOutcome.Complete,
            duration);

        var document = await transcriber.TranscribeAsync(
            recording,
            CancellationToken.None);

        var segment = Assert.Single(document.Segments);
        Assert.Equal(duration, segment.End);
        Assert.Equal("hello", segment.Text);
    }

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
    public void FinalizationReplacesInvalidFloatSamplesWithSilence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, "partial.wav");
        using (var writer = new WaveFileWriter(
                   partialPath,
                   WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1)))
        {
            var samples = new float[48_000];
            for (var index = 0; index < samples.Length; index++)
            {
                samples[index] = index < 24_000
                    ? .1f * MathF.Sin(index / 48_000f * MathF.PI * 440)
                    : index % 2 == 0
                        ? float.NaN
                        : float.MaxValue;
            }

            writer.WriteSamples(samples, 0, samples.Length);
        }

        var outputPath = Path.Combine(directory, "final.wav");
        WaveRecordingFinalizerAccessor.Finalize(
            partialPath,
            null,
            AudioSource.MicrophoneOnly,
            outputPath);

        using var reader = new WaveFileReader(outputPath);
        var samplesProvider = reader.ToSampleProvider();
        var output = new float[48_000];
        var count = samplesProvider.Read(output);
        Assert.InRange(count, 47_000, 48_000);
        Assert.Contains(output[..24_000], sample => MathF.Abs(sample) > .01f);
        Assert.All(output[30_000..count], sample => Assert.InRange(sample, -.0001f, .0001f));
    }

    [Fact]
    public void Finalization_48KHzPcmInput_PreservesCapturedSamplesExactly()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "capture.wav");
        var samples = new short[48_000];
        var random = new Random(42);
        random.NextBytes(System.Runtime.InteropServices.MemoryMarshal
            .AsBytes(samples.AsSpan()));
        using (var writer = new WaveFileWriter(
                   inputPath,
                   new WaveFormat(48_000, 16, 1)))
        {
            writer.WriteSamples(samples, 0, samples.Length);
        }

        var outputPath = Path.Combine(directory, "final.wav");
        WaveRecordingFinalizerAccessor.Finalize(
            inputPath,
            null,
            AudioSource.MicrophoneOnly,
            outputPath);

        using var output = new WaveFileReader(outputPath);
        var buffer = new byte[samples.Length * 2];
        var count = output.Read(buffer);
        Assert.Equal(buffer.Length, count);
        Assert.Equal(
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(samples.AsSpan())
                .ToArray(),
            buffer);
    }

    [Fact]
    public void Sanitize_WasapiSilentPacket_ReplacesBufferContentsWithSilence()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var data = Enumerable.Repeat(byte.MaxValue, format.BlockAlign * 100)
            .Select(value => value)
            .ToArray();

        var invalid = WaveCaptureSanitizer.Sanitize(
            data,
            format,
            AudioClientBufferFlags.Silent);

        Assert.Equal(0, invalid);
        Assert.All(data, value => Assert.Equal(0, value));
    }

    [Fact]
    public void CalculateLevel_ExtensibleFloatFormat_ReturnsPeak()
    {
        var format = new WaveFormatExtensible(
            48_000,
            32,
            1,
            true,
            32,
            1);
        var samples = new[] { -.25f, .75f };
        var bytes = samples
            .SelectMany(BitConverter.GetBytes)
            .ToArray();

        var level = WindowsRecordingEngineAccessor.CalculateLevel(bytes, format);

        Assert.Equal(.75f, level);
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

    private sealed class TestModelStore : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => true;

        public string? GetModelPath(string language) => "model.bin";
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

internal static class WindowsRecordingEngineAccessor
{
    public static float CalculateLevel(byte[] buffer, WaveFormat format)
    {
        var captureType = typeof(WindowsRecordingEngine)
            .GetNestedType("CaptureSource", BindingFlags.NonPublic)!;
        var method = captureType.GetMethod(
            "CalculateLevel",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (float)method.Invoke(
            null,
            [buffer, buffer.Length, format])!;
    }
}
