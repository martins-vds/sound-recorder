using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

public sealed class AudioFinalizerQualityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("recorder-finalizer-tests-").FullName;

    [Theory]
    [InlineData(48_000, 16, 1, false)]
    [InlineData(24_000, 16, 1, true)]
    [InlineData(48_000, 24, 1, true)]
    [InlineData(48_000, 16, 2, true)]
    public void Finalize_CaptureFormat_ConvertsOnlyWhenTargetPcmDoesNotMatch(
        int sampleRate, int bits, int channels, bool needsConversion)
    {
        var format = new WaveFormat(sampleRate, bits, channels);
        var input = Path.Combine(_directory, "input.wav");
        var original = Enumerable.Range(0, format.BlockAlign * 4).Select(index => (byte)(index + 1)).ToArray();
        using (var writer = new WaveFileWriter(input, format))
        {
            writer.Write(original, 0, original.Length);
        }

        var output = Path.Combine(_directory, "output.wav");
        var converted = new byte[] { 0, 32, 0, 64, 0, 0, 0, 192 };
        ConvertedProvider? converter = null;
        WaveRecordingFinalizer.Finalize(input, null, AudioSource.MicrophoneOnly, output, (source, target) =>
        {
            Assert.True(needsConversion, "Matching PCM must not enter the resampling path.");
            Assert.Equal(sampleRate, source.WaveFormat.SampleRate);
            Assert.Equal(bits, source.WaveFormat.BitsPerSample);
            Assert.Equal(channels, source.WaveFormat.Channels);
            Assert.Equal(48_000, target.SampleRate);
            Assert.Equal(16, target.BitsPerSample);
            Assert.Equal(1, target.Channels);
            converter = new ConvertedProvider(target, converted);
            return (converter, converter);
        });

        using var audio = new WaveFileReader(output);
        var expected = needsConversion ? converted : original;
        var actual = new byte[expected.Length];
        Assert.Equal(expected.Length, audio.Length);
        Assert.Equal(actual.Length, audio.Read(actual));
        Assert.Equal(expected, actual);
        if (needsConversion)
        {
            Assert.NotNull(converter);
            Assert.True(converter.Disposed);
        }
        else
        {
            Assert.Null(converter);
        }

        using var exclusive = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(24_000, 1)]
    [InlineData(44_100, 2)]
    public void Finalize_RealResampling_PreservesDurationAndAgreedOutputFormat(int sampleRate, int channels)
    {
        var input = Path.Combine(_directory, "resampled-input.wav");
        var frames = sampleRate / 10;
        using (var writer = new WaveFileWriter(input, new WaveFormat(sampleRate, 16, channels)))
        {
            var samples = Enumerable.Range(0, frames * channels)
                .Select(index => .1f * MathF.Sin(2 * MathF.PI * 440 * (index / channels) / sampleRate))
                .ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
        }

        var output = Path.Combine(_directory, "resampled-output.wav");
        WaveRecordingFinalizer.Finalize(null, input, AudioSource.SystemAudioOnly, output);

        using var audio = new WaveFileReader(output);
        Assert.Equal(48_000, audio.WaveFormat.SampleRate);
        Assert.Equal(16, audio.WaveFormat.BitsPerSample);
        Assert.Equal(2, audio.WaveFormat.Channels);
        Assert.InRange(audio.TotalTime.TotalMilliseconds, 99, 101);
        var actual = new float[9_600];
        var read = audio.ToSampleProvider().Read(actual);
        Assert.Contains(actual[..read], sample => MathF.Abs(sample) > .05f);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ConvertedProvider(WaveFormat format, byte[] bytes) : IWaveProvider, IDisposable
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = format;
        public bool Disposed { get; private set; }
        public int Read(Span<byte> buffer)
        {
            var count = Math.Min(bytes.Length - _position, buffer.Length);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public void Dispose() => Disposed = true;
    }
}
