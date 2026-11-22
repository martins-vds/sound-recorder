using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Audio;

public sealed class WindowsRecordingEngine : IRecordingEngine, IRecordingMonitor, IDisposable
{
    private readonly string _stateDirectory;
    private readonly ILogger<WindowsRecordingEngine> _logger;
    private readonly Func<string, bool, WaveFormat, IAudioCaptureDevice> _openCapture;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, long> _availableStorage;
    private readonly Func<string, WaveFormat, WaveCaptureWriter> _createWriter;
    private readonly Action<string?, string?, AudioSource, string> _finalize;
    private readonly object _captureLock = new();
    private CaptureSource? _microphone;
    private CaptureSource? _systemAudio;
    private RecordingRequest? _request;
    private Guid _recordingId;
    private long? _captureStartedAt;
    private bool _hasCaptureClock;
    private TimeSpan _capturedDuration;
    private ITimer? _limitTimer;
    private string? _checkpointPath;
    private volatile bool _paused;
    private bool _captureFailed;
    private string? _captureError;
    private DateTimeOffset _lastFlush;
    private readonly object _completionLock = new();
    private Task<SavedRecording>? _completionTask;
    private int _completionEventRaised;

    public WindowsRecordingEngine(
        string stateDirectory,
        ILogger<WindowsRecordingEngine>? logger = null)
        : this(
            stateDirectory,
            WasapiCaptureDevice.Open,
            TimeProvider.System,
            static path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace,
            logger)
    {
    }

    internal WindowsRecordingEngine(
        string stateDirectory,
        Func<string, bool, WaveFormat, IAudioCaptureDevice> openCapture,
        TimeProvider timeProvider,
        Func<string, long> availableStorage,
        ILogger<WindowsRecordingEngine>? logger = null,
        Func<string, WaveFormat, WaveCaptureWriter>? createWriter = null,
        Action<string?, string?, AudioSource, string>? finalize = null)
    {
        _stateDirectory = stateDirectory;
        _openCapture = openCapture;
        _timeProvider = timeProvider;
        _availableStorage = availableStorage;
        _createWriter = createWriter ?? (static (path, format) => new WaveCaptureWriter(path, format));
        _finalize = finalize ?? WaveRecordingFinalizer.Finalize;
        _lastFlush = timeProvider.GetUtcNow();
        _logger = logger ?? NullLogger<WindowsRecordingEngine>.Instance;
        Directory.CreateDirectory(_stateDirectory);
    }
    public TimeSpan CapturedDuration
    {
        get
        {
            lock (_captureLock)
            {
                return _capturedDuration +
                    (_captureStartedAt is { } started ? _timeProvider.GetElapsedTime(started) : TimeSpan.Zero);
            }
        }
    }

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
        lock (_captureLock)
        {
            _capturedDuration = TimeSpan.Zero;
            _hasCaptureClock = true;
            _captureStartedAt = _timeProvider.GetTimestamp();
        }
        _completionTask = null;
        Volatile.Write(ref _completionEventRaised, 0);

        try
        {
            if (microphoneRequired)
            {
                _microphone = CreateCaptureSource(
                    _openCapture(
                        request.MicrophoneId!,
                        false,
                        new WaveFormat(
                            RecordingFormat.SampleRate,
                            RecordingFormat.BitsPerSample,
                            1)),
                    Path.Combine(_stateDirectory, $"{_recordingId:N}-microphone.wav"));
            }

            if (playbackRequired)
            {
                _systemAudio = CreateCaptureSource(
                    _openCapture(
                        request.PlaybackDeviceId!,
                        true,
                        new WaveFormat(
                            RecordingFormat.SampleRate,
                            RecordingFormat.BitsPerSample,
                            2)),
                    Path.Combine(_stateDirectory, $"{_recordingId:N}-system.wav"));
            }

            _checkpointPath = Path.Combine(_stateDirectory, $"{_recordingId:N}.capture.json");
            SaveCheckpoint();
            _microphone?.Start();
            _systemAudio?.Start();
            _limitTimer = _timeProvider.CreateTimer(
                CheckDurationLimit, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
        lock (_captureLock)
        {
            if (_hasCaptureClock && !_paused)
            {
                _capturedDuration = CapturedDuration;
                _captureStartedAt = null;
                _paused = true;
                SaveCheckpoint();
                InfrastructureLog.CapturePaused(_logger, _recordingId);
            }
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_captureLock)
        {
            if (_hasCaptureClock && _paused)
            {
                _paused = false;
                _captureStartedAt = _timeProvider.GetTimestamp();
                SaveCheckpoint();
                InfrastructureLog.CaptureResumed(_logger, _recordingId);
            }
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
        lock (_captureLock)
        {
            _capturedDuration = CapturedDuration;
            _captureStartedAt = null;
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

    private CaptureSource CreateCaptureSource(IAudioCaptureDevice capture, string path)
    {
        var source = new CaptureSource(capture, path, IsPaused, HandleCaptureFailure, _createWriter);
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
        _ = CompleteAutomaticallyAsync();
    }

    private void CheckDurationLimit(object? state)
    {
        if (CapturedDuration < RecordingFormat.MaximumCapturedDuration)
        {
            if (_timeProvider.GetUtcNow() - _lastFlush < TimeSpan.FromSeconds(4))
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

        _lastFlush = _timeProvider.GetUtcNow();
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
        _finalize(
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
            CapturedDuration)
        {
            Title = request.Title,
        };
    }

    private void SaveCheckpoint()
    {
        if (_checkpointPath is null)
        {
            return;
        }

        var checkpoint = new CaptureCheckpoint(
            _recordingId,
            _microphone?.Path ?? string.Empty,
            _systemAudio?.Path,
            _request!.DestinationFolder!,
            _request.Title,
            _request.Language,
            _request.Source,
            CapturedDuration.Ticks,
            _timeProvider.GetUtcNow());
        File.WriteAllText(
            $"{_checkpointPath}.tmp",
            JsonSerializer.Serialize(checkpoint));
        File.Move($"{_checkpointPath}.tmp", _checkpointPath, overwrite: true);
    }

    private void ValidateDestination(RecordingRequest request)
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

        Directory.CreateDirectory(fullPath);
        var storageError = StoragePreflight.Validate(
            _availableStorage(fullPath),
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
        string? microphonePath,
        string? systemAudioPath)
    {
        foreach (var path in new[]
        {
            microphonePath,
            systemAudioPath,
            _checkpointPath,
        })
        {
            DeleteIfExists(path);
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
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
        private readonly IAudioCaptureDevice _capture;
        private readonly WaveCaptureWriter _writer;
        private readonly Func<bool> _isPaused;
        private readonly Action<Exception> _failure;
        private int _failureReported;
        private readonly TaskCompletionSource _stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CaptureSource(
            IAudioCaptureDevice capture,
            string path,
            Func<bool> isPaused,
            Action<Exception> failure,
            Func<string, WaveFormat, WaveCaptureWriter> createWriter)
        {
            _capture = capture;
            _writer = createWriter(path, capture.WaveFormat);
            _isPaused = isPaused;
            _failure = failure;
            Path = path;
            capture.DataAvailable += CaptureOnDataAvailable;
            capture.RecordingStopped += CaptureOnRecordingStopped;
        }

        public string Path { get; }

        public float Level => _writer.Level;

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
            _writer.Flush(capturedDuration);
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
                var invalidSamples = _writer.Append(buffer, flags);
                if (invalidSamples > Math.Max(
                        1,
                        buffer.Length / _capture.WaveFormat.BlockAlign / 100) &&
                    Interlocked.Exchange(ref _failureReported, 1) == 0)
                {
                    _failure(new InvalidDataException(
                        "The audio device returned invalid samples. " +
                        "The recording was stopped to prevent damaged audio."));
                }
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                {
                    _failure(exception);
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
    }
}
