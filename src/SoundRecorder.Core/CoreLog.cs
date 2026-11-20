using Microsoft.Extensions.Logging;

namespace SoundRecorder.Core;

internal static partial class CoreLog
{
    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Warning,
        Message = "Recording start blocked because permission was not confirmed")]
    public static partial void RecordingPermissionMissing(ILogger logger);

    [LoggerMessage(
        EventId = 211,
        Level = LogLevel.Warning,
        Message = "Recording start blocked because another recording is active")]
    public static partial void RecordingAlreadyActive(ILogger logger);

    [LoggerMessage(
        EventId = 212,
        Level = LogLevel.Warning,
        Message = "Recording start blocked because a prerequisite is missing")]
    public static partial void RecordingPrerequisiteMissing(ILogger logger);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Information,
        Message = "Recording started with source {AudioSource}")]
    public static partial void RecordingStarted(ILogger logger, AudioSource audioSource);

    [LoggerMessage(
        EventId = 901,
        Level = LogLevel.Error,
        Message = "Recording start failed")]
    public static partial void RecordingStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 201,
        Level = LogLevel.Information,
        Message = "Recording paused")]
    public static partial void RecordingPaused(ILogger logger);

    [LoggerMessage(
        EventId = 202,
        Level = LogLevel.Information,
        Message = "Recording resumed")]
    public static partial void RecordingResumed(ILogger logger);

    [LoggerMessage(
        EventId = 203,
        Level = LogLevel.Information,
        Message = "Recording {RecordingId} completed with outcome {RecordingOutcome}")]
    public static partial void RecordingCompleted(
        ILogger logger,
        Guid recordingId,
        RecordingOutcome recordingOutcome);

    [LoggerMessage(
        EventId = 902,
        Level = LogLevel.Error,
        Message = "Recording {RecordingId} completion processing failed")]
    public static partial void RecordingCompletionFailed(
        ILogger logger,
        Exception exception,
        Guid recordingId);

    [LoggerMessage(
        EventId = 300,
        Level = LogLevel.Information,
        Message = "Restored {JobCount} transcription jobs")]
    public static partial void QueueRestored(ILogger logger, int jobCount);

    [LoggerMessage(
        EventId = 301,
        Level = LogLevel.Information,
        Message = "Queued transcription job {JobId} for recording {RecordingId}")]
    public static partial void JobQueued(ILogger logger, Guid jobId, Guid recordingId);

    [LoggerMessage(
        EventId = 302,
        Level = LogLevel.Information,
        Message = "Cancellation requested for transcription job {JobId}")]
    public static partial void RunningJobCancellationRequested(ILogger logger, Guid jobId);

    [LoggerMessage(
        EventId = 303,
        Level = LogLevel.Information,
        Message = "Cancellation completed for transcription job {JobId}")]
    public static partial void JobCancellationCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(
        EventId = 304,
        Level = LogLevel.Information,
        Message = "Queued retry job {JobId} for recording {RecordingId}")]
    public static partial void JobRetryQueued(ILogger logger, Guid jobId, Guid recordingId);

    [LoggerMessage(
        EventId = 305,
        Level = LogLevel.Information,
        Message = "Transcription job {JobId} started")]
    public static partial void JobStarted(ILogger logger, Guid jobId);

    [LoggerMessage(
        EventId = 306,
        Level = LogLevel.Information,
        Message = "Transcription job {JobId} completed successfully")]
    public static partial void JobSucceeded(ILogger logger, Guid jobId);

    [LoggerMessage(
        EventId = 307,
        Level = LogLevel.Information,
        Message = "Transcription job {JobId} was canceled")]
    public static partial void JobCanceled(ILogger logger, Guid jobId);

    [LoggerMessage(
        EventId = 903,
        Level = LogLevel.Error,
        Message = "Transcription job {JobId} failed")]
    public static partial void JobFailed(
        ILogger logger,
        Exception exception,
        Guid jobId);

    [LoggerMessage(
        EventId = 400,
        Level = LogLevel.Information,
        Message = "Searching for interrupted recordings")]
    public static partial void RecoveryCandidatesRequested(ILogger logger);

    [LoggerMessage(
        EventId = 401,
        Level = LogLevel.Information,
        Message = "Recovered recording {RecordingId}")]
    public static partial void RecordingRecovered(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 904,
        Level = LogLevel.Error,
        Message = "Recovery failed for recording {RecordingId}")]
    public static partial void RecordingRecoveryFailed(
        ILogger logger,
        Exception exception,
        Guid recordingId);
}
