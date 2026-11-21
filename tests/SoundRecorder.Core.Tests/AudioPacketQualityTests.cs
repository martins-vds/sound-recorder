using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

public sealed class AudioPacketQualityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("recorder-packets-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Append_FloatSamples_SanitizesOnlyInvalidValuesAndRetainsLastSamplePeak(bool extensible)
    {
        var format = extensible
            ? new WaveFormatExtensible(48_000, 32, 1, true, 32, 1)
            : WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        float[] samples = [0, -.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1.01f, -1.01f, -1];
        var path = Path.Combine(_directory, "capture.wav");
        using (var writer = new WaveCaptureWriter(path, format))
        {
            var invalid = writer.Append(MemoryMarshal.AsBytes(samples.AsSpan()), 0);

            Assert.Equal(5, invalid);
            Assert.Equal(1f, writer.Level);
            writer.Flush(TimeSpan.MaxValue);
        }

        using var audio = new WaveFileReader(path);
        var bytes = new byte[samples.Length * sizeof(float)];
        Assert.Equal(bytes.Length, audio.Read(bytes));
        Assert.Equal(new float[] { 0, -.5f, 0, 0, 0, 0, 0, -1 }, MemoryMarshal.Cast<byte, float>(bytes).ToArray());
    }

    [Fact]
    public void Append_ExtensibleIntegerPcm_IsNotInterpretedAsFloatingPoint()
    {
        var format = new WaveFormatExtensible(48_000, 32, 1, false, 32, 1);
        int[] samples = [int.MinValue, int.MaxValue, 0x7f800000, 0x7fc00000];
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var path = Path.Combine(_directory, "integer.wav");
        using (var writer = new WaveCaptureWriter(path, format))
        {
            Assert.Equal(0, writer.Append(bytes, 0));
        }

        using var audio = new WaveFileReader(path);
        var actual = new byte[bytes.Length];
        Assert.Equal(actual.Length, audio.Read(actual));
        Assert.Equal(bytes, actual);
    }

    [Fact]
    public void Flush_OpenCapture_PersistsHeaderAndDoesNotTruncateAudioAheadOfClock()
    {
        var path = Path.Combine(_directory, "capture.wav");
        short[] samples = [short.MinValue, 16384, -8192, 0];
        using var writer = new WaveCaptureWriter(path, new WaveFormat(48_000, 16, 1));
        writer.Append(MemoryMarshal.AsBytes(samples.AsSpan()), 0);
        Assert.Equal(1f, writer.Level);

        writer.Flush(TimeSpan.Zero);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var audio = new WaveFileReader(stream);
        var actual = new byte[samples.Length * sizeof(short)];
        Assert.Equal(actual.Length, audio.Length);
        Assert.Equal(actual.Length, audio.Read(actual));
        Assert.Equal(samples, MemoryMarshal.Cast<byte, short>(actual).ToArray());
    }

    [Fact]
    public void Read_SanitizingProvider_RespectsOffsetAndPreservesSamplesOutsideRequestedRegion()
    {
        float[] samples = [.25f, float.NaN, -.75f];
        var sourceBytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        IWaveProvider source = new TestWaveProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1), sourceBytes);
        var sanitized = new SanitizingWaveProvider(source);
        var destination = Enumerable.Repeat((byte)0xab, 20).ToArray();

        Assert.Same(source.WaveFormat, sanitized.WaveFormat);
        var count = sanitized.Read(destination, 4, 12);

        Assert.Equal(12, count);
        Assert.All(destination[..4], value => Assert.Equal(0xab, value));
        Assert.All(destination[16..], value => Assert.Equal(0xab, value));
        Assert.Equal(new float[] { .25f, 0, -.75f }, MemoryMarshal.Cast<byte, float>(destination.AsSpan(4, 12)).ToArray());
        Assert.Equal(0, sanitized.Read(destination.AsSpan(4, 12)));
    }

    [Fact]
    public void Finalize_MissingSources_ReportsUnavailableAudioWithoutCreatingOutput()
    {
        var output = Path.Combine(_directory, "final.wav");

        var error = Assert.Throws<InvalidDataException>(() =>
            WaveRecordingFinalizer.Finalize(
                " ", Path.Combine(_directory, "missing.wav"), SoundRecorder.Core.AudioSource.MicrophoneAndSystemAudio, output));

        Assert.Contains("No captured audio", error.Message);
        Assert.False(File.Exists(output));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class TestWaveProvider(WaveFormat format, byte[] bytes) : IWaveProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = format;
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            var count = Math.Min(bytes.Length - _position, buffer.Length);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}
