using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

public sealed class WasapiDeviceQualityTests
{
    [LocalAudioTheory]
    [Trait("type", "IntegrationTests")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_SelectedDevice_UsesFixedPcmAndReleasesEventSubscriptions(bool loopback)
    {
        IAudioDeviceCatalog catalog = new WindowsAudioDeviceCatalog();
        var choices = loopback ? catalog.GetPlaybackDevices() : catalog.GetMicrophones();
        Assert.NotEmpty(choices);
        Assert.All(choices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        });
        var format = new WaveFormat(48_000, 16, loopback ? 2 : 1);
        using var capture = WasapiCaptureDevice.Open(choices[0].Id, loopback, format);
        Assert.Same(format, capture.WaveFormat);
        var packets = 0;
        var stops = 0;
        var priority = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureDataAvailableHandler onData = (_, _, _, _) =>
        {
            Interlocked.Increment(ref packets);
            priority.TrySetResult(GetThreadPriority(GetCurrentThread()));
        };
        EventHandler<StoppedEventArgs> onStopped = (_, args) =>
        {
            Interlocked.Increment(ref stops);
            if (args.Exception is null)
            {
                stopped.TrySetResult();
            }
            else
            {
                stopped.TrySetException(args.Exception);
            }
        };
        capture.DataAvailable += onData;
        capture.RecordingStopped += onStopped;
        try
        {
            capture.StartRecording();
            await WaitForStateAsync(capture, CaptureState.Capturing);
            if (!loopback)
            {
                Assert.True(await priority.Task.WaitAsync(TimeSpan.FromSeconds(5)) > 0,
                    "Capture should run at the elevated MMCSS audio priority.");
            }

            capture.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CaptureState.Stopped, capture.CaptureState);
            Assert.Equal(1, stops);
            capture.DataAvailable -= onData;
            capture.RecordingStopped -= onStopped;
            var originalPackets = packets;
            capture.StartRecording();
            await WaitForStateAsync(capture, CaptureState.Capturing);
            await Task.Delay(100);
            capture.StopRecording();
            await WaitForStateAsync(capture, CaptureState.Stopped);
            Assert.Equal(originalPackets, packets);
            Assert.Equal(1, stops);
        }
        finally
        {
            capture.DataAvailable -= onData;
            capture.RecordingStopped -= onStopped;
        }
    }

    private static async Task WaitForStateAsync(IAudioCaptureDevice capture, CaptureState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (capture.CaptureState != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadPriority(IntPtr thread);
}

public sealed class LocalAudioTheoryAttribute : TheoryAttribute
{
    public LocalAudioTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_AUDIO") != "1")
        {
            Skip = "Set SOUND_RECORDER_TEST_AUDIO=1 to run real selected-device capture checks.";
        }
    }
}
