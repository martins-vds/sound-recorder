using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundRecorder.Infrastructure.Audio;

/// <summary>Persists capture packets and publishes their peak level.</summary>
public sealed class WaveCaptureWriter : IDisposable
{
    private readonly WaveFileWriter _writer;
    private float _level;

    /// <summary>Creates a WAV file in the capture device's agreed format.</summary>
    public WaveCaptureWriter(string path, WaveFormat format)
    {
        _writer = new WaveFileWriter(path, format);
    }

    /// <summary>The latest nonempty packet's peak amplitude, from zero to one.</summary>
    public float Level => Volatile.Read(ref _level);

    /// <summary>Writes one packet, returning the number of invalid samples replaced.</summary>
    public int Append(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var capturedBytes = buffer.ToArray();
        var invalidSamples = WaveCaptureSanitizer.Sanitize(
            capturedBytes,
            _writer.WaveFormat,
            flags);
        lock (_writer)
        {
            _writer.Write(capturedBytes, 0, capturedBytes.Length);
        }

        Volatile.Write(ref _level, CalculateLevel(capturedBytes, _writer.WaveFormat));
        return invalidSamples;
    }

    /// <summary>Persists available audio, padding missing capture time with silence.</summary>
    public void Flush(TimeSpan capturedDuration)
    {
        lock (_writer)
        {
            if (capturedDuration != TimeSpan.MaxValue)
            {
                // Partial-frame padding shifts every subsequent sample into noise.
                var expectedFrames = (long)(
                    capturedDuration.TotalSeconds * _writer.WaveFormat.SampleRate);
                FillSilenceTo(expectedFrames * _writer.WaveFormat.BlockAlign);
            }

            _writer.Flush();
        }
    }

    /// <summary>Finalizes the capture file and releases its handle.</summary>
    public void Dispose()
    {
        _writer.Dispose();
    }

    private static float CalculateLevel(byte[] buffer, WaveFormat format)
    {
        var max = 0f;
        if (WaveCaptureSanitizer.IsIeeeFloat(format))
        {
            for (var index = 0; index <= buffer.Length - 4; index += 4)
            {
                max = Math.Max(max, MathF.Abs(BitConverter.ToSingle(buffer, index)));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var index = 0; index <= buffer.Length - 2; index += 2)
            {
                max = Math.Max(max, MathF.Abs(BitConverter.ToInt16(buffer, index) / 32768f));
            }
        }

        return max;
    }

    private void FillSilenceTo(long expectedLength)
    {
        var missingBytes = expectedLength - _writer.Position;
        if (missingBytes <= 0)
        {
            return;
        }

        var silence = new byte[Math.Min(missingBytes, 64 * 1024)];
        while (missingBytes > 0)
        {
            var count = (int)Math.Min(missingBytes, silence.Length);
            _writer.Write(silence, 0, count);
            missingBytes -= count;
        }
    }
}
