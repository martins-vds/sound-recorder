using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundRecorder.Infrastructure.Audio;

internal static class WaveCaptureSanitizer
{
    public static int Sanitize(
        Span<byte> bytes,
        WaveFormat format,
        AudioClientBufferFlags flags = 0)
    {
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            bytes.Clear();
            return 0;
        }

        if (!IsIeeeFloat(format))
        {
            return 0;
        }

        var invalidSamples = 0;
        for (var offset = 0; offset <= bytes.Length - 4; offset += 4)
        {
            var sample = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
            if (float.IsFinite(sample) && MathF.Abs(sample) <= 1)
            {
                continue;
            }

            BinaryPrimitives.WriteSingleLittleEndian(bytes[offset..], 0);
            invalidSamples++;
        }

        return invalidSamples;
    }

    private static bool IsIeeeFloat(WaveFormat format)
    {
        return format.BitsPerSample == 32 &&
               (format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible
                {
                    SubFormat: var subFormat,
                } &&
                subFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
    }
}

internal sealed class SanitizingWaveProvider(IWaveProvider inner) : IWaveProvider
{
    public WaveFormat WaveFormat => inner.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        var bytesRead = inner.Read(buffer);
        WaveCaptureSanitizer.Sanitize(buffer[..bytesRead], WaveFormat);
        return bytesRead;
    }
}
