using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundRecorder.Infrastructure.Audio;

internal interface IAudioCaptureDevice : IDisposable
{
    WaveFormat WaveFormat { get; }
    CaptureState CaptureState { get; }
    event CaptureDataAvailableHandler? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

internal sealed class WasapiCaptureDevice(WasapiRecorder recorder) : IAudioCaptureDevice
{
    public WaveFormat WaveFormat => recorder.WaveFormat;
    public CaptureState CaptureState => recorder.CaptureState;

    public event CaptureDataAvailableHandler? DataAvailable
    {
        add => recorder.DataAvailable += value;
        remove => recorder.DataAvailable -= value;
    }

    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add => recorder.RecordingStopped += value;
        remove => recorder.RecordingStopped -= value;
    }

    public void StartRecording() => recorder.StartRecording();
    public void StopRecording() => recorder.StopRecording();
    public void Dispose() => recorder.Dispose();

    public static IAudioCaptureDevice Open(string deviceId, bool loopback, WaveFormat format)
    {
        using var enumerator = new MMDeviceEnumerator();
        var builder = new WasapiRecorderBuilder()
            .WithDevice(enumerator.GetDevice(deviceId))
            .WithFormat(format)
            .WithBufferLength(100)
            .WithMmcssThreadPriority("Audio");
        if (loopback)
        {
            builder.WithLoopbackCapture();
        }

        return new WasapiCaptureDevice(builder.Build());
    }
}
