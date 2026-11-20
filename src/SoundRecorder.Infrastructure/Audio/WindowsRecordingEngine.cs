using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Audio;

public sealed class WindowsRecordingEngine : IRecordingEngine, IDisposable
{
    private readonly string _stateDirectory;
    private readonly ILogger<WindowsRecordingEngine> _logger;
    private readonly object _captureLock = new();
    private CaptureSource? _microphone;
    private CaptureSource? _systemAudio;
    private RecordingRequest? _request;
    private Guid _recordingId;
    private Stopwatch? _capturedClock;
    private TimeSpan _capturedDuration;
    private Timer? _limitTimer;
    private string? _checkpointPath;
    private bool _paused;
    private bool _captureFailed;
    private string? _captureError;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;
    private readonly object _completionLock = new();
    private Task<SavedRecording>? _completionTask;
    private int _completionEventRaised;

    public WindowsRecordingEngine(
        string stateDirectory,
        ILogger<WindowsRecordingEngine>? logger = null)
    {
        _stateDirectory = stateDirectory;
        _logger = logger ?? NullLogger<WindowsRecordingEngine>.Instance;
        Directory.CreateDirectory(_stateDirectory);
    }
    public TimeSpan CapturedDuration => _capturedDuration + (_capturedClock?.Elapsed ?? TimeSpan.Zero);

    public float MicrophoneLevel => _microphone?.Level ?? 0;

    public float SystemAudioLevel => _systemAudio?.Level ?? 0;

    public string? LastFailure => _captureError;

    public event EventHandler<SavedRecording>? RecordingCompleted;

    public async Task StartAsync(
        RecordingRequest request,
        CancellationToken cancellationToken)
    {
        ValidateDestination(request);
        cancellationToken.ThrowIfCancellationRequested();
        var microphoneRequired = request.Source is
            AudioSource.MicrophoneOnly or AudioSource.MicrophoneAndSystemAudio;
        var playbackRequired = request.Source is
            AudioSource.SystemAudioOnly or AudioSource.MicrophoneAndSystemAudio;
        _recordingId = Guid.NewGuid();
        InfrastructureLog.CaptureStarting(_logger, _recordingId, request.Source);
        _request = request;
        _captureFailed = false;
        _captureError = null;
        _paused = false;
        _capturedDuration = TimeSpan.Zero;
        _capturedClock = Stopwatch.StartNew();
        _completionTask = null;
        Volatile.Write(ref _completionEventRaised, 0);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (microphoneRequired)
            {
                var device = enumerator.GetDevice(request.MicrophoneId!);
                _microphone = CreateCaptureSource(
                    new WasapiRecorderBuilder()
                        .WithDevice(device)
                        .WithFormat(
                            WaveFormat.CreateIeeeFloatWaveFormat(
                                RecordingFormat.SampleRate,
                                1))
                        .WithBufferLength(100)
                        .WithMmcssThreadPriority("Audio")
                        .Build(),
                    Path.Combine(_stateDirectory, $"{_recordingId:N}-microphone.wav"));
            }

            if (playbackRequired)
            {
                var device = enumerator.GetDevice(request.PlaybackDeviceId!);
                _systemAudio = CreateCaptureSource(
                    new WasapiRecorderBuilder()
                        .WithDevice(device)
                        .WithLoopbackCapture()
                        .WithFormat(
                            WaveFormat.CreateIeeeFloatWaveFormat(
                                RecordingFormat.SampleRate,
                                2))
                        .WithBufferLength(100)
                        .WithMmcssThreadPriority("Audio")
                        .Build(),
                    Path.Combine(_stateDirectory, $"{_recordingId:N}-system.wav"));
            }

            _checkpointPath = Path.Combine(_stateDirectory, $"{_recordingId:N}.capture.json");
            SaveCheckpoint();
            _microphone?.Start();
            _systemAudio?.Start();
            _limitTimer = new Timer(CheckDurationLimit, null, 1000, 1000);
            InfrastructureLog.CaptureStarted(_logger, _recordingId);
        }
        catch (Exception exception)
        {
            InfrastructureLog.CaptureStartFailed(_logger, exception, _recordingId);
            await StopCaptureSourcesAsync();
            var microphonePath = _microphone?.Path;
            var systemAudioPath = _systemAudio?.Path;
            DisposeSources();
            DeletePartialFiles(microphonePath, systemAudioPath);
            throw new RecordingStartException(
                $"Recording could not start: {exception.Message}",
                exception);
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capturedClock is not null && !_paused)
        {
            _capturedClock.Stop();
            _capturedDuration += _capturedClock.Elapsed;
            _capturedClock.Reset();
            _paused = true;
            SaveCheckpoint();
            InfrastructureLog.CapturePaused(_logger, _recordingId);
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capturedClock is not null && _paused)
        {
            _paused = false;
            _capturedClock.Start();
            SaveCheckpoint();
            InfrastructureLog.CaptureResumed(_logger, _recordingId);
        }

        return Task.CompletedTask;
    }

    public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_completionLock)
        {
            return _completionTask ??= CompleteInternalAsync();
        }
    }

    private async Task<SavedRecording> CompleteInternalAsync()
    {
        _limitTimer?.Dispose();
        _capturedClock?.Stop();
        if (_capturedClock is not null && !_paused)
        {
            _capturedDuration += _capturedClock.Elapsed;
            _capturedClock.Reset();
        }

        await StopCaptureSourcesAsync();
        var microphonePath = _microphone?.Path;
        var systemAudioPath = _systemAudio?.Path;
        DisposeSources();
        var recording = FinalizeRecording(microphonePath, systemAudioPath);
        DeletePartialFiles(microphonePath, systemAudioPath);
        InfrastructureLog.CaptureFinalized(_logger, recording.Id, recording.Outcome);
        return recording;
    }

    public void Dispose()
    {
        _limitTimer?.Dispose();
        DisposeSources();
    }

    private CaptureSource CreateCaptureSource(WasapiRecorder capture, string path)
    {
        var source = new CaptureSource(capture, path, IsPaused, HandleCaptureFailure);
        return source;
    }

    private bool IsPaused()
    {
        return _paused;
    }

    private void HandleCaptureFailure(Exception exception)
    {
        _captureFailed = true;
        _captureError = exception.Message;
        InfrastructureLog.CaptureFailed(_logger, exception, _recordingId);
        if (_request is not null)
        {
            _ = CompleteAutomaticallyAsync();
        }
    }

    private void CheckDurationLimit(object? state)
    {
        if (CapturedDuration < RecordingFormat.MaximumCapturedDuration)
        {
            if (DateTimeOffset.UtcNow - _lastFlush < TimeSpan.FromSeconds(4))
            {
                return;
            }

            FlushSources();
            return;
        }

        _ = CompleteAutomaticallyAsync();
    }

    private async Task CompleteAutomaticallyAsync()
    {
        try
        {
            var recording = await StopAsync(CancellationToken.None);
            if (Interlocked.Exchange(ref _completionEventRaised, 1) == 0)
            {
                RecordingCompleted?.Invoke(this, recording);
            }
        }
        catch (Exception exception)
        {
            _captureFailed = true;
            _captureError = $"The recording could not be finalized: {exception.Message}";
        }
    }

    private void FlushSources()
    {
        lock (_captureLock)
        {
            _microphone?.Flush(CapturedDuration);
            _systemAudio?.Flush(CapturedDuration);
        }

        _lastFlush = DateTimeOffset.UtcNow;
        SaveCheckpoint();
    }

    private async Task StopCaptureSourcesAsync()
    {
        var sources = new[] { _microphone, _systemAudio }
            .Where(source => source is not null)
            .Cast<CaptureSource>()
            .ToArray();
        var capturedDuration = CapturedDuration;
        await Task.WhenAll(sources.Select(source => source.StopAsync(capturedDuration)));
    }

    private SavedRecording FinalizeRecording(
        string? microphonePath,
        string? systemAudioPath)
    {
        var request = _request
            ?? throw new InvalidOperationException("No recording is active.");
        var outputPath = RecordingFileName.CreateUnique(
            request.DestinationFolder!,
            request.Title,
            DateTimeOffset.Now);
        WaveRecordingFinalizer.Finalize(
            microphonePath,
            systemAudioPath,
            request.Source,
            outputPath);

        var outcome = _captureFailed
            ? RecordingOutcome.Interrupted
            : RecordingOutcome.Complete;
        return new SavedRecording(
            _recordingId,
            outputPath,
            request.Language,
            outcome,
            CapturedDuration);
    }

    private void SaveCheckpoint()
    {
        if (_request is null || _checkpointPath is null)
        {
            return;
        }

        var checkpoint = new CaptureCheckpoint(
            _recordingId,
            _microphone?.Path ?? string.Empty,
            _systemAudio?.Path,
            _request.DestinationFolder!,
            _request.Title,
            _request.Language,
            _request.Source,
            CapturedDuration.Ticks,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            $"{_checkpointPath}.tmp",
            JsonSerializer.Serialize(checkpoint));
        File.Move($"{_checkpointPath}.tmp", _checkpointPath, overwrite: true);
    }

    private static void ValidateDestination(RecordingRequest request)
    {
        var destination = request.DestinationFolder
            ?? throw new RecordingStartException("Select a local destination folder.");
        var fullPath = Path.GetFullPath(destination);
        try
        {
            LocalPathPolicy.RejectNetworkDestination(fullPath);
        }
        catch (InvalidOperationException exception)
        {
            throw new RecordingStartException(exception.Message, exception);
        }

        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        Directory.CreateDirectory(fullPath);
        var storageError = StoragePreflight.Validate(
            drive.AvailableFreeSpace,
            StoragePreflight.RequiredBytes(request.Source),
            request.AllowShortRecordingWhenStorageIsLow);
        if (storageError is not null)
        {
            throw new RecordingStartException(
                storageError + " " +
                "Confirm a shorter recording or choose another local folder.");
        }

        var probe = Path.Combine(fullPath, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception exception)
        {
            throw new RecordingStartException(
                "The selected destination is not writable. Choose another local folder.",
                exception);
        }
    }

    private void DeletePartialFiles(
        string? microphonePath = null,
        string? systemAudioPath = null)
    {
        microphonePath ??= _microphone?.Path;
        systemAudioPath ??= _systemAudio?.Path;
        if (microphonePath is not null && File.Exists(microphonePath))
        {
            File.Delete(microphonePath);
        }

        if (systemAudioPath is not null && File.Exists(systemAudioPath))
        {
            File.Delete(systemAudioPath);
        }

        if (_checkpointPath is not null && File.Exists(_checkpointPath))
        {
            File.Delete(_checkpointPath);
        }
    }

    private void DisposeSources()
    {
        _microphone?.Dispose();
        _systemAudio?.Dispose();
        _microphone = null;
        _systemAudio = null;
    }

    private sealed class CaptureSource : IDisposable
    {
        private readonly WasapiRecorder _capture;
        private readonly WaveFileWriter _writer;
        private readonly Func<bool> _isPaused;
        private readonly Action<Exception> _failure;
        private int _failureReported;
        private readonly TaskCompletionSource _stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CaptureSource(
            WasapiRecorder capture,
            string path,
            Func<bool> isPaused,
            Action<Exception> failure)
        {
            _capture = capture;
            _writer = new WaveFileWriter(path, capture.WaveFormat);
            _isPaused = isPaused;
            _failure = failure;
            Path = path;
            capture.DataAvailable += CaptureOnDataAvailable;
            capture.RecordingStopped += CaptureOnRecordingStopped;
        }

        public string Path { get; }

        public float Level { get; private set; }

        public void Start()
        {
            _capture.StartRecording();
        }

        public async Task StopAsync(TimeSpan capturedDuration)
        {
            if (_capture.CaptureState == CaptureState.Stopped)
            {
                Flush(capturedDuration);
                return;
            }

            _capture.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Flush(capturedDuration);
        }

        public void Flush(TimeSpan capturedDuration)
        {
            lock (_writer)
            {
                if (capturedDuration != TimeSpan.MaxValue)
                {
                    var expectedLength = (long)(
                        capturedDuration.TotalSeconds * _writer.WaveFormat.AverageBytesPerSecond);
                    FillSilenceTo(expectedLength);
                }

                _writer.Flush();
            }
        }

        public void Dispose()
        {
            _capture.DataAvailable -= CaptureOnDataAvailable;
            _capture.RecordingStopped -= CaptureOnRecordingStopped;
            _capture.Dispose();
            _writer.Dispose();
        }

        private void CaptureOnDataAvailable(
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long devicePosition,
            long qpcPosition)
        {
            if (_isPaused())
            {
                return;
            }

            try
            {
                var capturedBytes = buffer.ToArray();
                var invalidSamples = WaveCaptureSanitizer.Sanitize(
                    capturedBytes,
                    _writer.WaveFormat,
                    flags);
                lock (_writer)
                {
                    _writer.Write(capturedBytes, 0, capturedBytes.Length);
                }

                Level = CalculateLevel(
                    capturedBytes,
                    capturedBytes.Length,
                    _writer.WaveFormat);
                if (invalidSamples > Math.Max(
                        1,
                        capturedBytes.Length / _writer.WaveFormat.BlockAlign / 100) &&
                    Interlocked.Exchange(ref _failureReported, 1) == 0)
                {
                    _failure(new InvalidDataException(
                        "The audio device returned invalid samples. " +
                        "The recording was stopped to prevent damaged audio."));
                    _capture.StopRecording();
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                {
                    _failure(exception);
                    _capture.StopRecording();
                }
            }
        }

        private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is not null &&
                Interlocked.Exchange(ref _failureReported, 1) == 0)
            {
                _failure(args.Exception);
            }

            _stopped.TrySetResult();
        }

        private static float CalculateLevel(
            byte[] buffer,
            int bytesRecorded,
            WaveFormat format)
        {
            var max = 0f;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (var index = 0; index <= bytesRecorded - 4; index += 4)
                {
                    max = Math.Max(max, MathF.Abs(BitConverter.ToSingle(buffer, index)));
                }

                return max;
            }

            if (format.BitsPerSample == 16)
            {
                for (var index = 0; index <= bytesRecorded - 2; index += 2)
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
}
