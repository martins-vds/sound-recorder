using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Core;

public sealed class RecorderApplication
{
    private readonly IRecordingEngine _recordingEngine;
    private readonly ITranscriptionQueue? _transcriptionQueue;
    private readonly ITranscriptionModelStore? _modelStore;
    private readonly IRecordingHistory? _history;
    private readonly ILogger<RecorderApplication> _logger;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private readonly HashSet<Guid> _completedRecordingIds = [];
    private readonly HashSet<Guid> _persistedRecordingIds = [];

    public RecorderApplication(
        IRecordingEngine recordingEngine,
        ITranscriptionQueue? transcriptionQueue = null,
        ITranscriptionModelStore? modelStore = null,
        IRecordingHistory? history = null,
        ILogger<RecorderApplication>? logger = null)
    {
        _recordingEngine = recordingEngine;
        _transcriptionQueue = transcriptionQueue;
        _modelStore = modelStore;
        _history = history;
        _logger = logger ?? NullLogger<RecorderApplication>.Instance;
    }

    public bool IsActive => Status == RecordingStatus.Recording;

    public RecordingStatus Status { get; private set; } = RecordingStatus.Idle;

    public string VisibleStatus => Status.ToString();

    public string? LastError { get; private set; }

    public string? TranscriptionNotice { get; private set; }

    public async Task StartAsync(
        RecordingRequest request,
        bool permissionConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!permissionConfirmed)
        {
            LastError = "Confirm that you have permission to record before starting.";
            CoreLog.RecordingPermissionMissing(_logger);
            return;
        }

        if (Status is RecordingStatus.Recording or RecordingStatus.Paused)
        {
            LastError = "A recording is already active. Stop it before starting another.";
            CoreLog.RecordingAlreadyActive(_logger);
            return;
        }

        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            LastError = validationError;
            CoreLog.RecordingPrerequisiteMissing(_logger);
            return;
        }

        try
        {
            await _recordingEngine.StartAsync(request, cancellationToken);
            LastError = null;
            Status = RecordingStatus.Recording;
            CoreLog.RecordingStarted(_logger, request.Source);
        }
        catch (RecordingStartException exception)
        {
            LastError = exception.Message;
            CoreLog.RecordingStartFailed(_logger, exception);
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (Status != RecordingStatus.Recording)
        {
            return;
        }

        await _recordingEngine.PauseAsync(cancellationToken);
        Status = RecordingStatus.Paused;
        CoreLog.RecordingPaused(_logger);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (Status != RecordingStatus.Paused)
        {
            return;
        }

        await _recordingEngine.ResumeAsync(cancellationToken);
        Status = RecordingStatus.Recording;
        CoreLog.RecordingResumed(_logger);
    }

    public async Task<SavedRecording?> StopAsync(
        CancellationToken cancellationToken = default,
        bool enqueueTranscription = true)
    {
        if (Status is not (RecordingStatus.Recording or RecordingStatus.Paused))
        {
            return null;
        }

        try
        {
            var recording = await _recordingEngine.StopAsync(cancellationToken);
            await AcceptCompletedRecordingAsync(recording, enqueueTranscription, cancellationToken);
            return recording;
        }
        catch (Exception exception)
        {
            Status = RecordingStatus.Idle;
            LastError =
                "The recording could not be finalized. Available partial audio remains for recovery.";
            TranscriptionNotice = null;
            CoreLog.RecordingStopFailed(_logger, exception);
            return null;
        }
    }

    public async Task AcceptCompletedRecordingAsync(
        SavedRecording recording,
        bool enqueueTranscription = true,
        CancellationToken cancellationToken = default)
    {
        await _completionGate.WaitAsync(cancellationToken);
        try
        {
            if (!_completedRecordingIds.Add(recording.Id))
            {
                return;
            }

            Status = RecordingStatus.Idle;
            LastError = CompletionError(recording);
            CoreLog.RecordingCompleted(_logger, recording.Id, recording.Outcome);

            if (_history is not null && !_persistedRecordingIds.Contains(recording.Id))
            {
                await _history.AddAsync(recording, cancellationToken);
                _persistedRecordingIds.Add(recording.Id);
            }

            if (ShouldAutomaticallyTranscribe(recording, enqueueTranscription))
            {
                await _transcriptionQueue!.EnqueueAsync(recording, cancellationToken);
                TranscriptionNotice = null;
            }
            else
            {
                TranscriptionNotice = MissingModelNotice(recording);
            }

        }
        catch (Exception exception)
        {
            _completedRecordingIds.Remove(recording.Id);
            CoreLog.RecordingCompletionFailed(_logger, exception, recording.Id);
            throw;
        }
        finally
        {
            _completionGate.Release();
        }
    }

    private string? CompletionError(SavedRecording recording)
    {
        return recording.Outcome == RecordingOutcome.Interrupted
            ? _recordingEngine.LastFailure ??
              "Recording was interrupted. The available captured audio was preserved."
            : null;
    }

    private bool ShouldAutomaticallyTranscribe(
        SavedRecording recording,
        bool enqueueTranscription)
    {
        return enqueueTranscription &&
               recording.Outcome == RecordingOutcome.Complete &&
               _transcriptionQueue is not null &&
               _modelStore?.HasCompatibleModel(recording.Language) == true;
    }

    private string? MissingModelNotice(SavedRecording recording)
    {
        return recording.Outcome == RecordingOutcome.Complete &&
               _modelStore?.HasCompatibleModel(recording.Language) == false
            ? "Audio was saved. Install a compatible transcription model to transcribe this recording."
            : null;
    }

    private static string? ValidateRequest(RecordingRequest request)
    {
        return RequiredDeviceError(
                   UsesMicrophone(request.Source),
                   request.MicrophoneId,
                   "Select an available microphone before starting.")
               ?? RequiredDeviceError(
                   UsesPlaybackDevice(request.Source),
                   request.PlaybackDeviceId,
                   "Select an available playback device before starting.")
               ?? RequiredDeviceError(
                   required: true,
                   request.DestinationFolder,
                   "Select a writable local destination folder before starting.");
    }

    private static bool UsesMicrophone(AudioSource source)
    {
        return source is AudioSource.MicrophoneOnly or AudioSource.MicrophoneAndSystemAudio;
    }

    private static bool UsesPlaybackDevice(AudioSource source)
    {
        return source is AudioSource.SystemAudioOnly or AudioSource.MicrophoneAndSystemAudio;
    }

    private static string? RequiredDeviceError(
        bool required,
        string? selectedDevice,
        string error)
    {
        return required && string.IsNullOrWhiteSpace(selectedDevice)
            ? error
            : null;
    }
}
