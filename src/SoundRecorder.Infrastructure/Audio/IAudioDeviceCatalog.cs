namespace SoundRecorder.Infrastructure.Audio;

/// <summary>Lists available microphone and playback selections without starting capture.</summary>
public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceDescriptor> GetMicrophones();
    IReadOnlyList<AudioDeviceDescriptor> GetPlaybackDevices();
}
