using Microsoft.Extensions.Logging;
using SoundRecorder.Core;

namespace SoundRecorder.Desktop;

internal static partial class DesktopLog
{
    [LoggerMessage(EventId = 950, Level = LogLevel.Error, Message = "Experimental transcript analysis failed ({ErrorType})")]
    public static partial void TranscriptAnalysisFailed(ILogger logger, string errorType);

    [LoggerMessage(EventId = 951, Level = LogLevel.Information, Message = "Native transcription runtime loaded: {RuntimeInfo}")]
    public static partial void NativeTranscriptionRuntimeReady(ILogger logger, string runtimeInfo);

    [LoggerMessage(EventId = 952, Level = LogLevel.Warning, Message = "Native transcription runtime unavailable ({ErrorType}); capture remains independent")]
    public static partial void NativeTranscriptionRuntimeFailed(ILogger logger, string errorType);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "Application starting")]
    public static partial void ApplicationStarting(ILogger logger);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Information,
        Message = "Application exited with code {ExitCode}")]
    public static partial void ApplicationExited(ILogger logger, int exitCode);

    [LoggerMessage(
        EventId = 900,
        Level = LogLevel.Critical,
        Message = "Unhandled dispatcher exception")]
    public static partial void DispatcherUnhandledException(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 901,
        Level = LogLevel.Critical,
        Message = "Unhandled process exception")]
    public static partial void ProcessUnhandledException(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 902,
        Level = LogLevel.Error,
        Message = "Unobserved task exception")]
    public static partial void UnobservedTaskException(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 200,
        Level = LogLevel.Information,
        Message = "Main window initializing")]
    public static partial void MainWindowInitializing(ILogger logger);

    [LoggerMessage(
        EventId = 901,
        Level = LogLevel.Critical,
        Message = "Main window initialization failed")]
    public static partial void MainWindowInitializationFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 201,
        Level = LogLevel.Information,
        Message = "Main window ready")]
    public static partial void MainWindowReady(ILogger logger);

    [LoggerMessage(
        EventId = 902,
        Level = LogLevel.Error,
        Message = "Main window load failed")]
    public static partial void MainWindowLoadFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 210,
        Level = LogLevel.Information,
        Message = "Recording start requested for {AudioSource}")]
    public static partial void RecordingStartRequested(
        ILogger logger,
        AudioSource audioSource);

    [LoggerMessage(
        EventId = 211,
        Level = LogLevel.Information,
        Message = "Recording paused")]
    public static partial void RecordingPaused(ILogger logger);

    [LoggerMessage(
        EventId = 212,
        Level = LogLevel.Information,
        Message = "Recording resumed")]
    public static partial void RecordingResumed(ILogger logger);

    [LoggerMessage(
        EventId = 213,
        Level = LogLevel.Information,
        Message = "Recording stopped with outcome {RecordingOutcome}")]
    public static partial void RecordingStopped(
        ILogger logger,
        RecordingOutcome recordingOutcome);

    [LoggerMessage(
        EventId = 910,
        Level = LogLevel.Error,
        Message = "Recording stop or finalization failed")]
    public static partial void RecordingStopFailed(
        ILogger logger,
        Exception exception);
}
