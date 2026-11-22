using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Core.Tests;

public sealed class AudioEngineQualityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("recorder-engine-tests-").FullName;
    private readonly RecordingLogger<WindowsRecordingEngine> _logger = new();

    [Theory]
    [InlineData(AudioSource.MicrophoneOnly, 1, 1)]
    [InlineData(AudioSource.SystemAudioOnly, 1, 2)]
    [InlineData(AudioSource.MicrophoneAndSystemAudio, 2, 2)]
    public async Task StartAndStop_SelectedSources_PreservePcmAndRecordingMetadata(
        AudioSource source, int captureCount, int channels)
    {
        var clock = new CaptureTimeProvider();
        var devices = new List<TestCaptureDevice>();
        var state = Path.Combine(_directory, "state");
        using var engine = CreateEngine(clock, devices, state);
        var request = Request(source) with { Title = "quality", Language = "Brazilian Portuguese" };
        await engine.StartAsync(request, CancellationToken.None);
        Assert.Equal(captureCount, devices.Count);
        Assert.All(devices, device =>
        {
            Assert.Equal(48_000, device.WaveFormat.SampleRate);
            Assert.Equal(16, device.WaveFormat.BitsPerSample);
            Assert.Equal(1, device.StartCount);
            Assert.Equal(device.Loopback ? "playback" : "microphone", device.DeviceId);
            Assert.Equal(device.Loopback ? 2 : 1, device.WaveFormat.Channels);
            device.Send([8192, 8192, 8192, 8192]);
        });
        Assert.Equal(source == AudioSource.SystemAudioOnly ? 0f : .25f, engine.MicrophoneLevel);
        Assert.Equal(source == AudioSource.MicrophoneOnly ? 0f : .25f, engine.SystemAudioLevel);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(
            Assert.Single(Directory.GetFiles(state, "*.capture.json"))));
        Assert.Equal((int)source, checkpoint.RootElement.GetProperty("Source").GetInt32());
        var microphone = checkpoint.RootElement.GetProperty("MicrophonePath").GetString();
        if (source == AudioSource.SystemAudioOnly)
        {
            Assert.Equal(string.Empty, microphone);
        }
        else
        {
            Assert.EndsWith("-microphone.wav", microphone);
            Assert.True(File.Exists(microphone));
        }
        var id = checkpoint.RootElement.GetProperty("RecordingId").GetGuid();

        var recording = await engine.StopAsync(CancellationToken.None);

        Assert.Equal(id, recording.Id);
        Assert.Equal(request.Language, recording.Language);
        Assert.Equal("quality", recording.Title);
        Assert.Equal(RecordingOutcome.Complete, recording.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(100), recording.CapturedDuration);
        Assert.Contains("quality", Path.GetFileName(recording.AudioPath));
        Assert.Null(engine.LastFailure);
        Assert.Contains(500, _logger.EventIds);
        Assert.Contains(501, _logger.EventIds);
        Assert.Contains(504, _logger.EventIds);
        Assert.Equal(0f, engine.MicrophoneLevel);
        Assert.Equal(0f, engine.SystemAudioLevel);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(recording.CapturedDuration, engine.CapturedDuration);
        Assert.Empty(Directory.GetFiles(state));
        Assert.All(devices, device =>
        {
            Assert.Equal(1, device.StopCount);
            Assert.True(device.Disposed);
            Assert.Equal(0, device.Subscribers);
        });
        using var audio = new WaveFileReader(recording.AudioPath);
        Assert.Equal(channels, audio.WaveFormat.Channels);
        Assert.Equal(WaveFormatEncoding.Pcm, audio.WaveFormat.Encoding);
        var samples = new float[4];
        Assert.Equal(4, audio.ToSampleProvider().Read(samples));
        var expected = source == AudioSource.MicrophoneAndSystemAudio ? .5f : .25f;
        Assert.All(samples, sample => Assert.InRange(sample, expected - .001f, expected + .001f));
        Assert.Same(recording, await engine.StopAsync(CancellationToken.None));
        Assert.True(clock.Timer!.Disposed);
    }

    [Fact]
    public async Task PauseAndResume_RepeatedCalls_ExcludePausedAudioAndTime()
    {
        var clock = new CaptureTimeProvider();
        var devices = new List<TestCaptureDevice>();
        using var engine = CreateEngine(clock, devices);
        await engine.PauseAsync(CancellationToken.None);
        await engine.ResumeAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, engine.CapturedDuration);
        await engine.StartAsync(Request(), CancellationToken.None);
        devices[0].Send([8192, 8192]);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await engine.ResumeAsync(CancellationToken.None);
        await engine.PauseAsync(CancellationToken.None);
        await engine.PauseAsync(CancellationToken.None);
        var checkpointPath = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "state"), "*.capture.json"));
        using (var paused = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath)))
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100).Ticks, paused.RootElement.GetProperty("CapturedTicks").GetInt64());
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        devices[0].Send([30000, 30000]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), engine.CapturedDuration);
        Assert.Equal(.25f, engine.MicrophoneLevel);
        await engine.ResumeAsync(CancellationToken.None);
        await engine.ResumeAsync(CancellationToken.None);
        devices[0].Send([16384, 16384]);
        Assert.Equal(.5f, engine.MicrophoneLevel);
        using (var resumed = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath)))
        {
            Assert.Equal(clock.GetUtcNow(), resumed.RootElement.GetProperty("LastSavedAt").GetDateTimeOffset());
        }
        clock.Advance(TimeSpan.FromMilliseconds(100));

        var recording = await engine.StopAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(200), recording.CapturedDuration);
        using var audio = new WaveFileReader(recording.AudioPath);
        Assert.Equal(TimeSpan.FromMilliseconds(200), audio.TotalTime);
        var values = new float[9_600];
        var count = audio.ToSampleProvider().Read(values);
        Assert.Equal(values.Length, count);
        Assert.All(values, value => Assert.InRange(value, 0f, .501f));
        Assert.Contains(values, value => value > .499f && value < .501f);
        Assert.Single(_logger.EventIds, id => id == 502);
        Assert.Single(_logger.EventIds, id => id == 503);
    }

    [Theory]
    [InlineData(AudioSource.MicrophoneOnly)]
    [InlineData(AudioSource.SystemAudioOnly)]
    [InlineData(AudioSource.MicrophoneAndSystemAudio)]
    public async Task Timer_ElapsedFlushIntervals_PersistsAlignedRecoveryAudio(AudioSource source)
    {
        var clock = new CaptureTimeProvider();
        var devices = new List<TestCaptureDevice>();
        var state = Path.Combine(_directory, "state");
        using var engine = CreateEngine(clock, devices, state);
        await engine.StartAsync(Request(source), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Timer!.DueTime);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Timer.Period);
        clock.Advance(TimeSpan.FromSeconds(3));
        clock.Timer.Fire();
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Timer.Fire();
        var partials = Directory.GetFiles(state, "*.wav");
        Assert.All(partials, partial => Assert.Equal(TimeSpan.FromSeconds(4), ReadOpenWaveDuration(partial)));
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Timer.Fire();
        Assert.All(partials, partial => Assert.Equal(TimeSpan.FromSeconds(4), ReadOpenWaveDuration(partial)));
        clock.Advance(TimeSpan.FromSeconds(3));
        clock.Timer.Fire();
        Assert.All(partials, partial => Assert.Equal(TimeSpan.FromSeconds(8), ReadOpenWaveDuration(partial)));
        using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(
            Assert.Single(Directory.GetFiles(state, "*.capture.json"))));
        Assert.Equal(TimeSpan.FromSeconds(8).Ticks, checkpoint.RootElement.GetProperty("CapturedTicks").GetInt64());
        Assert.Equal(clock.GetUtcNow(), checkpoint.RootElement.GetProperty("LastSavedAt").GetDateTimeOffset());
        await engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_UnavailableDevice_ReportsCauseAndDeletesPartialSources()
    {
        var clock = new CaptureTimeProvider();
        var first = new TestCaptureDevice("microphone", false, new WaveFormat(48_000, 16, 1));
        var state = Path.Combine(_directory, "state");
        var failure = new IOException("Selected playback device unavailable");
        using var engine = new WindowsRecordingEngine(
            state,
            (_, loopback, _) => loopback ? throw failure : first,
            clock,
            _ => long.MaxValue);

        var error = await Assert.ThrowsAsync<RecordingStartException>(() =>
            engine.StartAsync(Request(AudioSource.MicrophoneAndSystemAudio), CancellationToken.None));

        Assert.Same(failure, error.InnerException);
        Assert.Contains(failure.Message, error.Message);
        Assert.True(first.Disposed);
        Assert.Empty(Directory.GetFiles(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"\\server\share\recordings")]
    public async Task StartAsync_InvalidDestination_DoesNotOpenAudioDevice(string? destination)
    {
        var devices = new List<TestCaptureDevice>();
        using var engine = CreateEngine(new CaptureTimeProvider(), devices);

        var error = await Assert.ThrowsAsync<RecordingStartException>(() =>
            engine.StartAsync(Request() with { DestinationFolder = destination }, CancellationToken.None));

        if (destination is null)
        {
            Assert.Equal("Select a local destination folder.", error.Message);
        }
        Assert.Empty(devices);
    }

    [Fact]
    public async Task StartAsync_LowSpace_RequiresExplicitShortRecordingConsent()
    {
        var devices = new List<TestCaptureDevice>();
        using var engine = new WindowsRecordingEngine(
            Path.Combine(_directory, "state"),
            (id, loopback, format) =>
            {
                var device = new TestCaptureDevice(id, loopback, format);
                devices.Add(device);
                return device;
            },
            new CaptureTimeProvider(),
            _ => 1_000_000);
        var storage = await Assert.ThrowsAsync<RecordingStartException>(() =>
            engine.StartAsync(Request(), CancellationToken.None));
        Assert.Contains("four-hour", storage.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Confirm a shorter recording or choose another local folder.", storage.Message);
        Assert.Empty(devices);
        await engine.StartAsync(
            Request() with { AllowShortRecordingWhenStorageIsLow = true },
            CancellationToken.None);
        Assert.Single(devices);
        await engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Capture_DeviceFailure_SavesInterruptedAudioAndRaisesCompletionOnce()
    {
        var devices = new List<TestCaptureDevice>();
        var clock = new CaptureTimeProvider();
        using var engine = CreateEngine(clock, devices);
        var completions = new List<SavedRecording>();
        engine.RecordingCompleted += (_, saved) => completions.Add(saved);
        await engine.StartAsync(Request(), CancellationToken.None);
        devices[0].Send([4096, 4096]);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        devices[0].Fail(new IOException("Microphone disconnected"));
        var saved = await engine.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingOutcome.Interrupted, saved.Outcome);
        Assert.Equal("Microphone disconnected", engine.LastFailure);
        Assert.Same(saved, Assert.Single(completions));
        Assert.True(File.Exists(saved.AudioPath));
        Assert.True(devices[0].Disposed);
        Assert.Contains(905, _logger.EventIds);
        Assert.Equal(0, devices[0].StopCount);
        using var audio = new WaveFileReader(saved.AudioPath);
        Assert.Equal(saved.CapturedDuration, audio.TotalTime);
    }

    [Fact]
    public async Task StartAsync_AfterPausedAndInterruptedSessions_ResetsDurationFailureAndCompletion()
    {
        var devices = new List<TestCaptureDevice>();
        var clock = new CaptureTimeProvider();
        using var engine = CreateEngine(clock, devices);
        var completions = new List<SavedRecording>();
        engine.RecordingCompleted += (_, saved) => completions.Add(saved);
        await engine.StartAsync(Request(), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await engine.PauseAsync(CancellationToken.None);
        var first = await engine.StopAsync(CancellationToken.None);

        await engine.StartAsync(Request(AudioSource.SystemAudioOnly), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, engine.CapturedDuration);
        Assert.Null(engine.LastFailure);
        devices[1].Send([8192, 8192]);
        Assert.Equal(.25f, engine.SystemAudioLevel);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        devices[1].Fail(new IOException("first disconnect"));
        var second = await engine.StopAsync(CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(TimeSpan.FromMilliseconds(200), second.CapturedDuration);
        Assert.Equal(RecordingOutcome.Interrupted, second.Outcome);

        await engine.StartAsync(Request(), CancellationToken.None);
        Assert.Null(engine.LastFailure);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        devices[2].Fail(new IOException("second disconnect"));
        var third = await engine.StopAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(300), third.CapturedDuration);
        Assert.Equal(new[] { second, third }, completions);

        await engine.StartAsync(Request(), CancellationToken.None);
        devices[3].Send([4096, 4096]);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var fourth = await engine.StopAsync(CancellationToken.None);
        Assert.Equal(RecordingOutcome.Complete, fourth.Outcome);
        Assert.Null(engine.LastFailure);
        Assert.Equal(TimeSpan.FromMilliseconds(50), fourth.CapturedDuration);
        Assert.Equal(4, Directory.GetFiles(_directory, "*.wav").Length);
    }

    [Fact]
    public async Task Capture_ExcessInvalidFloatSamples_StopsAtTheDamageThreshold()
    {
        var clock = new CaptureTimeProvider();
        var device = new TestCaptureDevice(
            "microphone", false, WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1));
        using var engine = new WindowsRecordingEngine(
            Path.Combine(_directory, "state"), (_, _, _) => device, clock, _ => long.MaxValue);
        await engine.StartAsync(Request(), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var oneInvalid = new float[1_000];
        Array.Fill(oneInvalid, float.NaN, 0, 10);
        device.SendFloats(oneInvalid);
        Assert.Null(engine.LastFailure);
        Assert.False(device.Disposed);
        var damaged = new float[1_000];
        Array.Fill(damaged, float.PositiveInfinity, 0, 11);

        device.SendFloats(damaged);
        var saved = await engine.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingOutcome.Interrupted, saved.Outcome);
        Assert.Equal(
            "The audio device returned invalid samples. The recording was stopped to prevent damaged audio.",
            engine.LastFailure);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task Commands_PreCanceledToken_LeaveTheActiveSessionUntouched()
    {
        var clock = new CaptureTimeProvider();
        var devices = new List<TestCaptureDevice>();
        using var engine = CreateEngine(clock, devices);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.StartAsync(Request(), canceled.Token));
        Assert.Empty(devices);
        await engine.StartAsync(Request(), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PauseAsync(canceled.Token));
        await engine.PauseAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ResumeAsync(canceled.Token));
        await engine.ResumeAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StopAsync(canceled.Token));
        Assert.Equal(0, devices[0].StopCount);
        await engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_NoSession_ReportsThatNoRecordingIsActive()
    {
        using var engine = CreateEngine(new CaptureTimeProvider(), []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StopAsync(CancellationToken.None));

        Assert.Equal("No recording is active.", error.Message);
    }

    [Fact]
    public async Task StartAsync_SecondSourceCannotStart_StopsAlreadyStartedMicrophone()
    {
        var clock = new CaptureTimeProvider();
        var devices = new List<TestCaptureDevice>();
        var state = Path.Combine(_directory, "state");
        var destination = Path.Combine(_directory, "new-output");
        using var failureEngine = new WindowsRecordingEngine(
            state,
            (id, loopback, format) =>
            {
                var device = new TestCaptureDevice(id, loopback, format)
                {
                    StartFailure = loopback ? new IOException("playback startup failed") : null,
                };
                devices.Add(device);
                return device;
            },
            clock,
            _ => long.MaxValue,
            _logger);

        var error = await Assert.ThrowsAsync<RecordingStartException>(() =>
            failureEngine.StartAsync(
                Request(AudioSource.MicrophoneAndSystemAudio) with { DestinationFolder = destination },
                CancellationToken.None));

        Assert.True(Directory.Exists(destination));
        Assert.Contains("playback startup failed", error.Message);
        Assert.Equal(1, devices[0].StopCount);
        Assert.All(devices, device => Assert.True(device.Disposed));
        Assert.Empty(Directory.GetFiles(state));
        Assert.Contains(906, _logger.EventIds);
    }

    [Fact]
    public async Task StopAsync_CaptureStopsLater_WaitsBeforeFinalizing()
    {
        var clock = new CaptureTimeProvider();
        var devices = new List<TestCaptureDevice>();
        using var engine = CreateEngine(clock, devices);
        await engine.StartAsync(Request(), CancellationToken.None);
        devices[0].CompleteStopImmediately = false;
        clock.Advance(TimeSpan.FromMilliseconds(10));

        var pending = engine.StopAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Empty(Directory.GetFiles(_directory, "*.wav"));
        devices[0].CompleteStop();
        var recording = await pending;

        Assert.True(File.Exists(recording.AudioPath));
        Assert.True(devices[0].Disposed);
    }

    [Fact]
    public async Task Dispose_ActiveCapture_ReleasesDevicesAndTimerButKeepsRecoveryFiles()
    {
        var devices = new List<TestCaptureDevice>();
        var clock = new CaptureTimeProvider();
        var state = Path.Combine(_directory, "state");
        using var engine = CreateEngine(clock, devices, state);
        await engine.StartAsync(Request(), CancellationToken.None);

        engine.Dispose();

        Assert.True(clock.Timer!.Disposed);
        Assert.All(devices, device =>
        {
            Assert.True(device.Disposed);
            Assert.Equal(0, device.Subscribers);
        });
        Assert.Single(Directory.GetFiles(state, "*.capture.json"));
        using var partial = new FileStream(
            Assert.Single(Directory.GetFiles(state, "*.wav")),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task StartAsync_DestinationCannotCreateFiles_ReportsAnActionableErrorBeforeCapture()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_directory, "denied"));
        var original = destination.GetAccessControl(AccessControlSections.Access);
        var denied = destination.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(
            identity.User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
        var devices = new List<TestCaptureDevice>();
        using var engine = CreateEngine(new CaptureTimeProvider(), devices);
        try
        {
            destination.SetAccessControl(denied);

            var error = await Assert.ThrowsAsync<RecordingStartException>(() =>
                engine.StartAsync(Request() with { DestinationFolder = destination.FullName }, CancellationToken.None));

            Assert.Equal("The selected destination is not writable. Choose another local folder.", error.Message);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
            Assert.Empty(devices);
        }
        finally
        {
            original.SetSecurityDescriptorSddlForm(
                original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                AccessControlSections.Access);
            destination.SetAccessControl(original);
        }
    }

    [Fact]
    public async Task Timer_CapturedDurationLimit_StopsExactlyAtFourHoursAndReportsStorageFailure()
    {
        var devices = new List<TestCaptureDevice>();
        var clock = new CaptureTimeProvider();
        using var engine = new WindowsRecordingEngine(
            Path.Combine(_directory, "state"),
            (id, loopback, format) =>
            {
                var device = new TestCaptureDevice(id, loopback, format);
                devices.Add(device);
                return device;
            },
            clock,
            _ => long.MaxValue,
            createWriter: (_, format) => new WaveCaptureWriter(new CappedMemoryStream(), format));
        await engine.StartAsync(Request(), CancellationToken.None);
        devices[0].StopFailure = new IOException("storage unavailable");
        clock.Advance(TimeSpan.FromHours(4) - TimeSpan.FromTicks(1), advanceUtc: false);
        clock.Timer!.Fire();
        Assert.Equal(0, devices[0].StopCount);
        clock.Advance(TimeSpan.FromTicks(1), advanceUtc: false);

        clock.Timer.Fire();

        Assert.Equal(1, devices[0].StopCount);
        Assert.Equal(TimeSpan.FromHours(4), engine.CapturedDuration);
        Assert.Equal(
            "The recording could not be finalized: storage unavailable",
            engine.LastFailure);
        Assert.True(clock.Timer.Disposed);
    }

    [Fact]
    public async Task Timer_FourHourLimit_FinalizesNormallyAndNotifiesOnlyOnce()
    {
        var clock = new CaptureTimeProvider();
        var device = new TestCaptureDevice("microphone", false, new WaveFormat(48_000, 16, 1));
        var completions = new List<SavedRecording>();
        var finalized = 0;
        using var engine = new WindowsRecordingEngine(
            Path.Combine(_directory, "state"),
            (_, _, _) => device,
            clock,
            _ => long.MaxValue,
            createWriter: (_, format) => new WaveCaptureWriter(new CountingWriteStream(), format),
            finalize: (microphone, system, source, output) =>
            {
                Assert.EndsWith("-microphone.wav", microphone);
                Assert.Null(system);
                Assert.Equal(AudioSource.MicrophoneOnly, source);
                finalized++;
                File.WriteAllBytes(output, [1]);
            });
        engine.RecordingCompleted += (_, saved) => completions.Add(saved);
        await engine.StartAsync(Request(), CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(4), advanceUtc: false);

        clock.Timer!.Fire();
        clock.Timer.Fire();
        var saved = await engine.StopAsync(CancellationToken.None);

        Assert.Equal(1, finalized);
        Assert.Same(saved, Assert.Single(completions));
        Assert.Equal(TimeSpan.FromHours(4), saved.CapturedDuration);
        Assert.Equal(RecordingOutcome.Complete, saved.Outcome);
        Assert.Null(engine.LastFailure);
        Assert.Equal(1, device.StopCount);
    }

    [Fact]
    public async Task Capture_WriteFails_StopsAndPreservesAnInterruptedRecording()
    {
        var clock = new CaptureTimeProvider();
        var device = new TestCaptureDevice("microphone", false, new WaveFormat(48_000, 16, 1));
        OneShotFailureStream? stream = null;
        using var engine = new WindowsRecordingEngine(
            Path.Combine(_directory, "state"),
            (_, _, _) => device,
            clock,
            _ => long.MaxValue,
            createWriter: (path, format) =>
            {
                stream = new OneShotFailureStream(path);
                return new WaveCaptureWriter(stream, format);
            });
        await engine.StartAsync(Request(), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        stream!.FailNextWrite = true;

        device.Send([8192, 8192]);
        var recording = await engine.StopAsync(CancellationToken.None);

        Assert.Equal(RecordingOutcome.Interrupted, recording.Outcome);
        Assert.Equal("Captured audio write failed", engine.LastFailure);
        Assert.True(File.Exists(recording.AudioPath));
        Assert.True(device.Disposed);
    }

    private RecordingRequest Request(AudioSource source = AudioSource.MicrophoneOnly) =>
        new(source, "microphone", "playback", _directory, null, "English");

    private WindowsRecordingEngine CreateEngine(
        CaptureTimeProvider clock,
        List<TestCaptureDevice> devices,
        string? state = null) =>
        new(
            state ?? Path.Combine(_directory, "state"),
            (id, loopback, format) =>
            {
                var device = new TestCaptureDevice(id, loopback, format);
                devices.Add(device);
                return device;
            },
            clock,
            _ => long.MaxValue,
            _logger);

    private static TimeSpan ReadOpenWaveDuration(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new WaveFileReader(stream);
        return reader.TotalTime;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    internal sealed class TestCaptureDevice(
        string deviceId, bool loopback, WaveFormat format) : IAudioCaptureDevice
    {
        public string DeviceId { get; } = deviceId;
        public bool Loopback { get; } = loopback;
        public WaveFormat WaveFormat { get; } = format;
        public CaptureState CaptureState { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? StopFailure { get; set; }
        public Exception? StartFailure { get; set; }
        public bool CompleteStopImmediately { get; set; } = true;
        public int Subscribers => (DataAvailable?.GetInvocationList().Length ?? 0) +
            (RecordingStopped?.GetInvocationList().Length ?? 0);
        public event CaptureDataAvailableHandler? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public void StartRecording()
        {
            StartCount++;
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
            CaptureState = CaptureState.Capturing;
        }

        public void StopRecording()
        {
            StopCount++;
            if (StopFailure is not null)
            {
                throw StopFailure;
            }

            CaptureState = CaptureState.Stopping;
            if (CompleteStopImmediately)
            {
                CompleteStop();
            }
        }

        public void CompleteStop()
        {
            CaptureState = CaptureState.Stopped;
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Send(short[] samples) =>
            DataAvailable?.Invoke(MemoryMarshal.AsBytes(samples.AsSpan()), 0, 0, 0);

        public void SendFloats(float[] samples) =>
            DataAvailable?.Invoke(MemoryMarshal.AsBytes(samples.AsSpan()), 0, 0, 0);

        public void Fail(Exception error)
        {
            CaptureState = CaptureState.Stopped;
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }

        public void Dispose() => Disposed = true;
    }

    internal sealed class CaptureTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public TestTimer? Timer { get; private set; }
        public void Advance(TimeSpan value, bool advanceUtc = true)
        {
            _timestamp += value.Ticks;
            if (advanceUtc)
            {
                _utc += value;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new TestTimer(callback, state, dueTime, period);
            return Timer;
        }
    }

    internal sealed class TestTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public bool Disposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan due, TimeSpan interval) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CappedMemoryStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > 65_536)
            {
                throw new IOException("Unexpected long-duration padding in a threshold test");
            }

            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
        }
    }

    private sealed class OneShotFailureStream(string path)
        : FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)
    {
        public bool FailNextWrite { get; set; }
        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Captured audio write failed");
            }

            var bytes = buffer.ToArray();
            base.Write(bytes, 0, bytes.Length);
        }
    }

    private sealed class CountingWriteStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => RecordWrite(count);
        public override void Write(ReadOnlySpan<byte> buffer) => RecordWrite(buffer.Length);
        public override void SetLength(long value) => _length = value;
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        private void RecordWrite(int count)
        {
            Position += count;
            _length = Math.Max(_length, Position);
        }
    }
}
