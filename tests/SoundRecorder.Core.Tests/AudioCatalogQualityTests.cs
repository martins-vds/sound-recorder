using NAudio.CoreAudioApi;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

public sealed class AudioCatalogQualityTests
{
    [Fact]
    public void GetDevices_MicrophoneAndPlayback_UseSeparateFlowsAndStableNameOrdering()
    {
        var microphone = new AudioDeviceDescriptor("mic-z", "Zulu microphone");
        var otherMicrophone = new AudioDeviceDescriptor("mic-a", "Alpha microphone");
        var playback = new AudioDeviceDescriptor("play-z", "Zulu playback");
        var otherPlayback = new AudioDeviceDescriptor("play-a", "Alpha playback");
        var calls = new List<DataFlow>();
        IAudioDeviceCatalog catalog = new WindowsAudioDeviceCatalog(flow =>
        {
            calls.Add(flow);
            return flow == DataFlow.Capture
                ? new[] { microphone, otherMicrophone }
                : new[] { playback, otherPlayback };
        });

        Assert.Equal(new[] { otherMicrophone, microphone }, catalog.GetMicrophones());
        Assert.Equal(new[] { otherPlayback, playback }, catalog.GetPlaybackDevices());
        Assert.Equal(new[] { DataFlow.Capture, DataFlow.Render }, calls);
    }
}
