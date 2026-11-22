using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Recovery;

namespace SoundRecorder.Core.Tests;

public sealed class RecoveryStorageTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();
    private readonly Guid _recordingId = Guid.NewGuid();
    private static readonly DateTimeOffset SavedAt = DateTimeOffset.Parse("2026-03-04T05:06:07+02:00");

    [Fact]
    public async Task FindAsync_Checkpoints_ReturnsOnlyValidCandidatesWithPersistedMetadata()
    {
        var logger = new StorageTestLogger<LocalRecoveryStorage>();
        var missing = new LocalRecoveryStorage(_directory.PathFor("missing"));
        Assert.Empty(await missing.FindAsync(default));
        var candidate = await Checkpoint(null, null);
        await File.WriteAllTextAsync(_directory.PathFor("bad.capture.json"), "{bad");
        await File.WriteAllTextAsync(_directory.PathFor("null.capture.json"), "null");
        await File.WriteAllTextAsync(_directory.PathFor("ignored.json"), "{}");
        var constructor = typeof(LocalRecoveryStorage).GetConstructor([typeof(string), typeof(ILogger<LocalRecoveryStorage>)]);
        Assert.NotNull(constructor);
        var storage = Assert.IsType<LocalRecoveryStorage>(constructor.Invoke([_directory.Root, logger]));
        var found = Assert.Single(await storage.FindAsync(default));
        Assert.Equal(_recordingId, found.RecordingId);
        Assert.Equal(candidate.CapturedAudioPath, found.CapturedAudioPath);
        Assert.Equal("Brazilian Portuguese", found.Language);
        Assert.Equal(TimeSpan.FromTicks(123456), found.CapturedDuration);
        Assert.Equal(SavedAt, found.LastSavedAt);
        Assert.Single(logger.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Meeting:/ notes")]
    public async Task RecoverAsync_ValidMicrophone_RepairsAudioAndDeletesOnlyCaptureSources(string? title)
    {
        var microphone = Wave("microphone.wav", 480, 1, oddChunk: true);
        var originalAudio = await File.ReadAllBytesAsync(microphone);
        var candidate = await Checkpoint(microphone, null, title);
        var logger = new StorageTestLogger<LocalRecoveryStorage>();
        var recording = await new LocalRecoveryStorage(_directory.Root, logger).RecoverAsync(candidate, default);
        Assert.Equal(_recordingId, recording.Id);
        Assert.Equal("Brazilian Portuguese", recording.Language);
        Assert.Equal(RecordingOutcome.Recovered, recording.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(10), recording.CapturedDuration);
        Assert.EndsWith("-recovered.wav", recording.AudioPath);
        Assert.Equal(_directory.Root, Path.GetDirectoryName(recording.AudioPath));
        if (string.IsNullOrWhiteSpace(title))
        {
            Assert.Matches(@"^\d{8}-\d{6}-recovered.wav$", Path.GetFileName(recording.AudioPath));
        }
        else
        {
            Assert.Contains("-Meeting-- notes-recovered.wav", recording.AudioPath);
        }

        using (var reader = new WaveFileReader(recording.AudioPath))
        {
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.Equal(1, reader.WaveFormat.Channels);
            var actual = new byte[reader.Length];
            Assert.Equal(actual.Length, reader.Read(actual, 0, actual.Length));
            Assert.Equal(originalAudio[^960..], actual);
        }

        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(recording.AudioPath + ".recovered.json"));
        Assert.Equal(_recordingId, marker.RootElement.GetProperty("RecordingId").GetGuid());
        Assert.False(File.Exists(microphone));
        Assert.False(File.Exists(candidate.CapturedAudioPath));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task RecoverAsync_DataHeaderAtEndOfCapture_IsNotMistakenForMissingDataChunk()
    {
        var source = Wave("empty-capture.wav", 0, 1);
        var candidate = await Checkpoint(source, null);

        var recording = await new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default);

        Assert.Equal(RecordingOutcome.Recovered, recording.Outcome);
        Assert.Equal(TimeSpan.Zero, recording.CapturedDuration);
        using var reader = new WaveFileReader(recording.AudioPath);
        Assert.Equal(0, reader.Length);
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(candidate.CapturedAudioPath));
    }

    [Fact]
    public async Task RecoverAsync_TwoSources_UsesLongestCapturedDurationAndCleansBothSources()
    {
        var microphone = Wave("mic.wav", 480, 2);
        var system = Wave("system.wav", 960, 2);
        var candidate = await Checkpoint(microphone, system, source: AudioSource.MicrophoneAndSystemAudio);
        var recovered = await new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default);
        Assert.Equal(TimeSpan.FromMilliseconds(20), recovered.CapturedDuration);
        Assert.False(File.Exists(microphone));
        Assert.False(File.Exists(system));
        using var reader = new WaveFileReader(recovered.AudioPath);
        Assert.Equal(TimeSpan.FromMilliseconds(20), reader.TotalTime);
    }

    [Fact]
    public async Task RecoverAsync_SystemAudioOnly_DoesNotRequireMicrophone()
    {
        var system = Wave("system.wav", 480, 2);
        var recovered = await new LocalRecoveryStorage(_directory.Root).RecoverAsync(
            await Checkpoint(" ", system, source: AudioSource.SystemAudioOnly), default);
        Assert.Equal(TimeSpan.FromMilliseconds(10), recovered.CapturedDuration);
        Assert.True(File.Exists(recovered.AudioPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing.wav")]
    public async Task RecoverAsync_NoExistingAudio_ReportsFailureAndKeepsCheckpoint(string? microphone)
    {
        var candidate = await Checkpoint(microphone, null);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));
        Assert.Equal("No captured audio is available to recover.", exception.Message);
        Assert.True(File.Exists(candidate.CapturedAudioPath));
    }

    [Theory]
    [InlineData(12, "has no recoverable WAV data.")]
    [InlineData(44, "is not a WAV file.")]
    [InlineData(45, "is not a WAV file.")]
    public async Task RecoverAsync_InvalidWave_ReportsSpecificFailureWithoutDiscarding(int length, string message)
    {
        var path = _directory.PathFor("bad.wav");
        await File.WriteAllBytesAsync(path, new byte[length]);
        var candidate = await Checkpoint(path, null);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));
        Assert.Equal($"The capture 'bad.wav' {message}", exception.Message);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(candidate.CapturedAudioPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoverAsync_OneInvalidSignature_RejectsRiffAndWaveIndependently(bool corruptRiff)
    {
        var path = Wave("bad.wav", 480, 1);
        using (var stream = File.OpenWrite(path))
        {
            stream.Position = corruptRiff ? 0 : 8;
            stream.Write("NOPE"u8);
        }

        var candidate = await Checkpoint(path, null);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));
        Assert.Equal("The capture 'bad.wav' is not a WAV file.", exception.Message);
    }

    [Fact]
    public async Task RecoverAsync_NoDataChunk_ReportsFailureWithoutDiscardingCapture()
    {
        var path = Wave("nodata.wav", 480, 1);
        using (var stream = File.OpenWrite(path))
        {
            stream.Position = 36;
            stream.Write("JUNK"u8);
            stream.Write(BitConverter.GetBytes(960));
        }

        var candidate = await Checkpoint(path, null);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));
        Assert.Equal("The capture 'nodata.wav' has no WAV data chunk.", exception.Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RecoverAsync_OutputUnavailable_RepairsExactHeaderButKeepsSourceForRetry()
    {
        var path = Wave("capture.wav", 480, 1, oddChunk: true);
        var blockedDestination = _directory.PathFor("blocked");
        await File.WriteAllTextAsync(blockedDestination, "not a directory");
        var candidate = await Checkpoint(path, null, destination: blockedDestination);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(960, BitConverter.ToInt32(bytes, 50));
        Assert.True(File.Exists(candidate.CapturedAudioPath));
    }

    [Fact]
    public async Task RecoverAsync_OverTwoGiBAndOutputUnavailable_RepairsUnsignedWaveLengthsWithoutCopyingAudio()
    {
        var path = Wave("large.wav", 0, 2);
        const uint dataLength = (uint)int.MaxValue + 1;
        SetSparseLength(path, 44L + dataLength);

        var blockedDestination = _directory.PathFor("blocked");
        await File.WriteAllTextAsync(blockedDestination, "not a directory");
        var candidate = await Checkpoint(path, null, destination: blockedDestination);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));

        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 4;
        Assert.Equal(dataLength + 36, reader.ReadUInt32());
        reader.BaseStream.Position = 40;
        Assert.Equal(dataLength, reader.ReadUInt32());
        Assert.True(File.Exists(candidate.CapturedAudioPath));
    }

    [Fact]
    public async Task RecoverAsync_RiffSizeCannotRepresentCapture_RejectsWithoutWrappingLengthsOrDiscarding()
    {
        var path = Wave("oversized.wav", 0, 2);
        SetSparseLength(path, (long)uint.MaxValue + 45);
        var candidate = await Checkpoint(path, null);

        await Assert.ThrowsAsync<OverflowException>(() =>
            new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default));

        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 4;
        Assert.Equal(0u, reader.ReadUInt32());
        reader.BaseStream.Position = 40;
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.True(File.Exists(candidate.CapturedAudioPath));
    }

    [Fact]
    public async Task RecoverAsync_AudioBytesResembleAnotherDataHeader_DoesNotInterpretSamplesAsChunkMetadata()
    {
        var path = Wave("capture.wav", 480, 1);
        using (var stream = File.OpenWrite(path))
        {
            stream.Position = 44;
            stream.Write("data"u8);
            stream.Write(BitConverter.GetBytes(0));
        }

        var originalSamples = (await File.ReadAllBytesAsync(path))[44..];
        var candidate = await Checkpoint(path, null);
        var recovered = await new LocalRecoveryStorage(_directory.Root).RecoverAsync(candidate, default);
        using var reader = new WaveFileReader(recovered.AudioPath);
        var samples = new byte[reader.Length];
        Assert.Equal(samples.Length, reader.Read(samples, 0, samples.Length));
        Assert.Equal(originalSamples, samples);
        Assert.Equal(TimeSpan.FromMilliseconds(10), recovered.CapturedDuration);
    }

    [Fact]
    public async Task RecoverAsync_NameCollision_PreservesEarlierRecoveredAudio()
    {
        var path = Wave("first.wav", 480, 1);
        var storage = new LocalRecoveryStorage(_directory.Root, timeProvider: new FixedTimeProvider());
        var first = await storage.RecoverAsync(await Checkpoint(path, null), default);
        var original = await File.ReadAllBytesAsync(first.AudioPath);
        await File.WriteAllTextAsync(_directory.PathFor("20260304-030607-recovered-1.wav"), "earlier collision");
        path = Wave("second.wav", 960, 1);
        var second = await storage.RecoverAsync(await Checkpoint(path, null), default);
        Assert.NotEqual(first.AudioPath, second.AudioPath);
        Assert.EndsWith("20260304-030607-recovered-2.wav", second.AudioPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(first.AudioPath));
        Assert.True(File.Exists(second.AudioPath));
    }

    [Fact]
    public async Task DiscardAsync_ExplicitAction_DeletesCheckpointAndBothSourcesOnly()
    {
        var microphone = Wave("mic.wav", 480, 1);
        var system = Wave("system.wav", 480, 1);
        var unrelated = Wave("keep.wav", 480, 1);
        var candidate = await Checkpoint(microphone, system);
        await new LocalRecoveryStorage(_directory.Root).DiscardAsync(candidate, default);
        Assert.False(File.Exists(microphone));
        Assert.False(File.Exists(system));
        Assert.False(File.Exists(candidate.CapturedAudioPath));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task DiscardAsync_Canceled_DoesNotDeleteSourcesOrCheckpoint()
    {
        var microphone = Wave("mic.wav", 480, 1);
        var candidate = await Checkpoint(microphone, null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalRecoveryStorage(_directory.Root).DiscardAsync(candidate, cancellation.Token));
        Assert.True(File.Exists(microphone));
        Assert.True(File.Exists(candidate.CapturedAudioPath));
    }

    [Fact]
    public async Task DiscardAsync_NoSources_DeletesOnlyCheckpoint()
    {
        var candidate = await Checkpoint(" ", _directory.PathFor("missing"));
        await new LocalRecoveryStorage(_directory.Root).DiscardAsync(candidate, default);
        Assert.False(File.Exists(candidate.CapturedAudioPath));
    }

    [Fact]
    public async Task Checkpoint_NullJson_ReportsInvalidCheckpointForRecoverAndDiscard()
    {
        var candidate = await Checkpoint(null, null);
        await File.WriteAllTextAsync(candidate.CapturedAudioPath, "null");
        var storage = new LocalRecoveryStorage(_directory.Root);
        var recover = await Assert.ThrowsAsync<InvalidDataException>(() => storage.RecoverAsync(candidate, default));
        var discard = await Assert.ThrowsAsync<InvalidDataException>(() => storage.DiscardAsync(candidate, default));
        Assert.Equal("The recovery checkpoint is invalid.", recover.Message);
        Assert.Equal(recover.Message, discard.Message);
    }

    private async Task<RecoveryCandidate> Checkpoint(string? microphone, string? system, string? title = null,
        AudioSource source = AudioSource.MicrophoneOnly, string? destination = null)
    {
        var checkpointPath = _directory.PathFor("session.capture.json");
        await File.WriteAllTextAsync(checkpointPath, JsonSerializer.Serialize(new
        {
            RecordingId = _recordingId, MicrophonePath = microphone, SystemAudioPath = system,
            DestinationFolder = destination ?? _directory.Root, Title = title, Language = "Brazilian Portuguese",
            Source = source, CapturedTicks = 123456L, LastSavedAt = SavedAt,
        }));
        return new(_recordingId, checkpointPath, "Brazilian Portuguese", TimeSpan.FromTicks(123456), SavedAt);
    }

    private string Wave(string name, int sampleCount, short channels, bool oddChunk = false)
    {
        var path = _directory.PathFor(name);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(48000);
        writer.Write(48000 * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        if (oddChunk)
        {
            writer.Write("JUNK"u8);
            writer.Write(1);
            writer.Write((byte)42);
            writer.Write((byte)0);
        }

        writer.Write("data"u8);
        writer.Write(0);
        for (var index = 0; index < sampleCount * channels; index++)
        {
            writer.Write((short)(index % 100));
        }

        return path;
    }

    public void Dispose() => _directory.Dispose();

    private static void SetSparseLength(string path, long length)
    {
        using var stream = File.OpenWrite(path);
        Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x000900C4,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
            $"Sparse-file setup failed: {Marshal.GetLastWin32Error()}");
        stream.SetLength(length);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => SavedAt.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle,
        uint controlCode,
        IntPtr input,
        uint inputSize,
        IntPtr output,
        uint outputSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
