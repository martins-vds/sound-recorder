using NAudio.CoreAudioApi;

namespace SoundRecorder.Infrastructure.Audio;

public sealed class WindowsAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly Func<DataFlow, IReadOnlyList<AudioDeviceDescriptor>> _enumerate;

    public WindowsAudioDeviceCatalog() : this(EnumerateDevices)
    {
    }

    internal WindowsAudioDeviceCatalog(Func<DataFlow, IReadOnlyList<AudioDeviceDescriptor>> enumerate)
    {
        _enumerate = enumerate;
    }

    public IReadOnlyList<AudioDeviceDescriptor> GetMicrophones() => GetDevices(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceDescriptor> GetPlaybackDevices() => GetDevices(DataFlow.Render);

    private IReadOnlyList<AudioDeviceDescriptor> GetDevices(DataFlow flow) =>
        _enumerate(flow).OrderBy(device => device.Name).ToArray();

    private static IReadOnlyList<AudioDeviceDescriptor> EnumerateDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDeviceDescriptor(device.ID, device.FriendlyName))
            .ToArray();
    }
}
