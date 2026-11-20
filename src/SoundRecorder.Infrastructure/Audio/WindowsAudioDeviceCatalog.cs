using NAudio.CoreAudioApi;

namespace SoundRecorder.Infrastructure.Audio;

public sealed class WindowsAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceDescriptor> GetMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioDeviceDescriptor(device.ID, device.FriendlyName))
            .OrderBy(device => device.Name)
            .ToArray();
    }

    public IReadOnlyList<AudioDeviceDescriptor> GetPlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDeviceDescriptor(device.ID, device.FriendlyName))
            .OrderBy(device => device.Name)
            .ToArray();
    }
}
