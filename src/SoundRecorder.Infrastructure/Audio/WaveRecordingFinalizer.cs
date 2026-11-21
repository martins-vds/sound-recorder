using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Audio;

internal static class WaveRecordingFinalizer
{
    public static void Finalize(
        string? microphonePath,
        string? systemAudioPath,
        AudioSource source,
        string outputPath)
    {
        Finalize(microphonePath, systemAudioPath, source, outputPath, CreateResampler);
    }

    internal static void Finalize(
        string? microphonePath,
        string? systemAudioPath,
        AudioSource source,
        string outputPath,
        Func<IWaveProvider, WaveFormat, (IWaveProvider Provider, IDisposable Resource)> createResampler)
    {
        var channels = source == AudioSource.MicrophoneOnly ? 1 : 2;
        var targetFormat = new WaveFormat(
            RecordingFormat.SampleRate,
            RecordingFormat.BitsPerSample,
            channels);
        var readers = new List<IWaveProvider>();
        var disposables = new List<IDisposable>();
        try
        {
            AddReader(microphonePath, targetFormat, readers, disposables, createResampler);
            AddReader(systemAudioPath, targetFormat, readers, disposables, createResampler);
            if (readers.Count == 0)
            {
                throw new InvalidDataException("No captured audio is available to finalize.");
            }

            IWaveProvider output = readers.Count == 1
                ? readers[0]
                : new SampleToWaveProvider16(
                    new MixingSampleProvider(
                        readers.Select(ToSampleProvider)));
            using var finalWriter = new WaveFileWriter(outputPath, targetFormat);
            var buffer = new byte[32 * 1024];
            int bytesRead;
            while ((bytesRead = output.Read(buffer)) > 0)
            {
                finalWriter.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            foreach (var reader in disposables)
            {
                reader.Dispose();
            }
        }
    }

    private static void AddReader(
        string? path,
        WaveFormat targetFormat,
        List<IWaveProvider> readers,
        List<IDisposable> disposables,
        Func<IWaveProvider, WaveFormat, (IWaveProvider Provider, IDisposable Resource)> createResampler)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var waveReader = new WaveFileReader(path);
        if (IsMatchingPcmFormat(waveReader.WaveFormat, targetFormat))
        {
            readers.Add(waveReader);
            disposables.Add(waveReader);
            return;
        }

        IWaveProvider sanitizedReader = new SanitizingWaveProvider(waveReader);
        var resampler = createResampler(sanitizedReader, targetFormat);
        readers.Add(resampler.Provider);
        disposables.Add(resampler.Resource);
        disposables.Add(waveReader);
    }

    private static (IWaveProvider, IDisposable) CreateResampler(IWaveProvider source, WaveFormat target)
    {
        var resampler = new MediaFoundationResampler(source, target);
        return (resampler, resampler);
    }

    private static bool IsMatchingPcmFormat(
        WaveFormat source,
        WaveFormat target)
    {
        return source.Encoding == WaveFormatEncoding.Pcm &&
               source.SampleRate == target.SampleRate &&
               source.BitsPerSample == target.BitsPerSample &&
               source.Channels == target.Channels;
    }

    private static ISampleProvider ToSampleProvider(IWaveProvider reader)
    {
        ISampleProvider provider = new Pcm16BitToSampleProvider(reader);
        return provider.WaveFormat.Channels == 1
            ? new MonoToStereoSampleProvider(provider)
            : provider;
    }
}
