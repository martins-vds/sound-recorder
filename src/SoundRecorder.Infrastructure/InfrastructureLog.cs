using Microsoft.Extensions.Logging;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure;

internal static partial class InfrastructureLog
{
    [LoggerMessage(
        EventId = 500,
        Level = LogLevel.Information,
        Message = "Capture {RecordingId} starting with source {AudioSource}")]
    public static partial void CaptureStarting(
        ILogger logger,
        Guid recordingId,
        AudioSource audioSource);

    [LoggerMessage(
        EventId = 501,
        Level = LogLevel.Information,
        Message = "Capture {RecordingId} started")]
    public static partial void CaptureStarted(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 906,
        Level = LogLevel.Error,
        Message = "Capture {RecordingId} could not start")]
    public static partial void CaptureStartFailed(
        ILogger logger,
        Exception exception,
        Guid recordingId);

    [LoggerMessage(
        EventId = 502,
        Level = LogLevel.Information,
        Message = "Capture {RecordingId} paused")]
    public static partial void CapturePaused(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 503,
        Level = LogLevel.Information,
        Message = "Capture {RecordingId} resumed")]
    public static partial void CaptureResumed(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 504,
        Level = LogLevel.Information,
        Message = "Capture {RecordingId} finalized with outcome {RecordingOutcome}")]
    public static partial void CaptureFinalized(
        ILogger logger,
        Guid recordingId,
        RecordingOutcome recordingOutcome);

    [LoggerMessage(
        EventId = 905,
        Level = LogLevel.Error,
        Message = "Capture {RecordingId} failed")]
    public static partial void CaptureFailed(
        ILogger logger,
        Exception exception,
        Guid recordingId);

    [LoggerMessage(
        EventId = 600,
        Level = LogLevel.Information,
        Message = "Model package import started")]
    public static partial void ModelImportStarted(ILogger logger);

    [LoggerMessage(
        EventId = 601,
        Level = LogLevel.Information,
        Message = "Model {ModelId} activated")]
    public static partial void ModelActivated(ILogger logger, string modelId);

    [LoggerMessage(
        EventId = 907,
        Level = LogLevel.Error,
        Message = "Model package import failed")]
    public static partial void ModelImportFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 602,
        Level = LogLevel.Information,
        Message = "Explicit model package download started")]
    public static partial void ModelDownloadStarted(ILogger logger);

    [LoggerMessage(
        EventId = 908,
        Level = LogLevel.Error,
        Message = "Model package download or activation failed")]
    public static partial void ModelDownloadFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 700,
        Level = LogLevel.Information,
        Message = "Local transcription started for recording {RecordingId}")]
    public static partial void TranscriptionStarted(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 701,
        Level = LogLevel.Information,
        Message = "Local transcription produced {SegmentCount} segments for recording {RecordingId}")]
    public static partial void TranscriptionProducedSegments(
        ILogger logger,
        int segmentCount,
        Guid recordingId);

    [LoggerMessage(
        EventId = 800,
        Level = LogLevel.Information,
        Message = "Loaded {JobCount} persisted transcription jobs")]
    public static partial void TranscriptionJobsLoaded(ILogger logger, int jobCount);

    [LoggerMessage(
        EventId = 801,
        Level = LogLevel.Information,
        Message = "Persisted {JobCount} transcription jobs")]
    public static partial void TranscriptionJobsPersisted(ILogger logger, int jobCount);

    [LoggerMessage(
        EventId = 810,
        Level = LogLevel.Information,
        Message = "Saved recording {RecordingId} to local history")]
    public static partial void RecordingHistorySaved(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 820,
        Level = LogLevel.Information,
        Message = "Found {RecoveryCandidateCount} recovery candidates")]
    public static partial void RecoveryCandidatesFound(
        ILogger logger,
        int recoveryCandidateCount);

    [LoggerMessage(
        EventId = 821,
        Level = LogLevel.Information,
        Message = "Recovered recording {RecordingId}")]
    public static partial void RecoveryCompleted(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 830,
        Level = LogLevel.Information,
        Message = "Saved TXT/SRT pair for recording {RecordingId}")]
    public static partial void TranscriptPairSaved(ILogger logger, Guid recordingId);

    [LoggerMessage(
        EventId = 909,
        Level = LogLevel.Error,
        Message = "Saving TXT/SRT pair failed for recording {RecordingId}")]
    public static partial void TranscriptPairSaveFailed(
        ILogger logger,
        Exception exception,
        Guid recordingId);
}
