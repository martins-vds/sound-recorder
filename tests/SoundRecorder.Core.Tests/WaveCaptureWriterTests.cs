using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

public sealed class WaveCaptureWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public WaveCaptureWriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Flush_FractionalFrameDuration_PreservesSubsequentPcmSamples(int channels)
    {
        var path = Path.Combine(_directory, "capture.wav");
        var format = new WaveFormat(48_000, 16, channels);
        short[] samples = [8192, -8192, 16384, -16384];
        var packet = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        using (var writer = new WaveCaptureWriter(path, format))
        {
            writer.Append(packet, 0);
            // 160 ticks is 0.768 frames: padding it as bytes splits a 16-bit sample.
            writer.Flush(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(160));
            writer.Append(packet, 0);
            writer.Flush(TimeSpan.MaxValue);
        }

        using var reader = new WaveFileReader(path);
        var audio = new byte[reader.Length];
        Assert.Equal(audio.Length, reader.Read(audio));
        Assert.Equal(format.AverageBytesPerSecond + packet.Length, audio.Length);
        Assert.Equal(packet, audio[..packet.Length]);
        Assert.All(audio[packet.Length..format.AverageBytesPerSecond], value => Assert.Equal(0, value));
        Assert.Equal(packet, audio[format.AverageBytesPerSecond..]);
    }

    [Fact]
    public void Append_EmptyCallbackAfterAudio_KeepsLevelUntilNextAudioPacket()
    {
        var path = Path.Combine(_directory, "capture.wav");
        using (var writer = new WaveCaptureWriter(path, new WaveFormat(48_000, 16, 1)))
        {
            short[] samples = [-8192, 16384];
            var packet = MemoryMarshal.AsBytes(samples.AsSpan());
            writer.Append(packet, 0);
            Assert.Equal(.5f, writer.Level);

            writer.Append([], 0);
            writer.Append([], AudioClientBufferFlags.Silent);

            Assert.Equal(.5f, writer.Level);
            writer.Append(packet, AudioClientBufferFlags.Silent);
            Assert.Equal(0f, writer.Level);
        }

        using var reader = new WaveFileReader(path);
        var audio = new byte[8];
        Assert.Equal(audio.Length, reader.Read(audio));
        Assert.Equal(new byte[] { 0, 224, 0, 64, 0, 0, 0, 0 }, audio);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
