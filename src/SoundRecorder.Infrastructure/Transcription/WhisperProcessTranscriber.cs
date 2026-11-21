using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Transcription;

/// <summary>Isolates native inference so cancellation cannot terminate the recorder.</summary>
public sealed class WhisperProcessTranscriber : ILocalTranscriber
{
    private readonly string _executablePath;
    private readonly ITranscriptionModelStore _models;
    private readonly Func<string, ITranscriptionWorker> _startWorker;
    private readonly ILogger<WhisperProcessTranscriber> _logger;

    public WhisperProcessTranscriber(
        string executablePath,
        ITranscriptionModelStore models,
        ILogger<WhisperProcessTranscriber>? logger = null)
        : this(executablePath, models, WindowsTranscriptionWorker.Start, logger)
    {
    }

    internal WhisperProcessTranscriber(
        string executablePath,
        ITranscriptionModelStore models,
        Func<string, ITranscriptionWorker> startWorker,
        ILogger<WhisperProcessTranscriber>? logger = null)
    {
        _executablePath = executablePath;
        _models = models;
        _startWorker = startWorker;
        _logger = logger ?? NullLogger<WhisperProcessTranscriber>.Instance;
    }

    /// <summary>Runs one local worker and releases it before completing or canceling the job.</summary>
    public async Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = _models.GetModelPath(recording.Language)
            ?? throw new InvalidOperationException(
                $"No compatible local transcription model is installed for {recording.Language}.");
        var directory = Path.Combine(Path.GetTempPath(), $"sound-recorder-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var process = _startWorker(_executablePath);
            try
            {
                InfrastructureLog.TranscriptionWorkerStarted(_logger, process.Id, recording.Id);
                using var registration = cancellationToken.Register(process.Abort);
                var request = new WorkerRequest(model, recording, Path.Combine(directory, "processing.wav"));
                using (var input = process.Input)
                {
                    await input.WriteLineAsync(
                        JsonSerializer.Serialize(request).AsMemory(),
                        cancellationToken);
                }
                var result = await ReadResultAsync(process, recording.Id, cancellationToken);
                var exitCode = await process.WaitForExitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var segments = GetSegments(result, exitCode);
                InfrastructureLog.TranscriptionProducedSegments(_logger, segments.Count, recording.Id);
                return new TranscriptDocument(recording, recording.Language, segments);
            }
            catch (IOException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Local transcription was canceled.", exception, cancellationToken);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<TranscriptSegment> GetSegments(WorkerMessage result, int exitCode)
    {
        if (exitCode != 0 || result.Error is not null)
        {
            throw new InvalidOperationException(
                result.Error ??
                $"The local transcription worker exited unexpectedly (code {exitCode}). Retry the job.");
        }

        return result.Segments
            ?? throw new InvalidDataException("The local transcription worker returned no result.");
    }

    /// <summary>Processes the private stdin/stdout protocol without opening the desktop UI.</summary>
    public static Task<int> RunWorkerAsync(TextReader input, TextWriter output) =>
        RunWorkerAsync(input, output, static (models, logger) => new WhisperLocalTranscriber(models, logger));

    internal static async Task<int> RunWorkerAsync(
        TextReader input,
        TextWriter output,
        Func<ITranscriptionModelStore, ILogger<WhisperLocalTranscriber>, WhisperLocalTranscriber> createTranscriber)
    {
        try
        {
            var line = await input.ReadLineAsync()
                ?? throw new InvalidDataException("No local transcription request was supplied.");
            var request = JsonSerializer.Deserialize<WorkerRequest>(line)
                ?? throw new InvalidDataException("The local transcription request is invalid.");
            var transcriber = createTranscriber(
                new WorkerModelStore(request.ModelPath),
                new WorkerLogger(output));
            var document = await transcriber.TranscribeAsync(
                request.Recording,
                request.ProcessingAudioPath,
                CancellationToken.None);
            await output.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage(document.Segments, null)));
            await output.FlushAsync();
            return 0;
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage(null, exception.Message)));
            await output.FlushAsync();
            return 1;
        }
    }

    private async Task<WorkerMessage> ReadResultAsync(
        ITranscriptionWorker process,
        Guid recordingId,
        CancellationToken cancellationToken)
    {
        while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
        {
            var message = JsonSerializer.Deserialize<WorkerMessage>(line)
                ?? throw new InvalidDataException("The local transcription worker returned an invalid response.");
            if (!message.Started)
            {
                return message;
            }

            InfrastructureLog.TranscriptionStarted(_logger, recordingId);
        }

        var exitCode = await process.WaitForExitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException(
            $"The local transcription worker stopped without returning a result (code {exitCode}). Retry the job.");
    }

    private sealed record WorkerRequest(
        string ModelPath,
        SavedRecording Recording,
        string ProcessingAudioPath);

    private sealed record WorkerMessage(
        IReadOnlyList<TranscriptSegment>? Segments,
        string? Error,
        bool Started = false);

    private sealed class WorkerModelStore(string modelPath) : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => File.Exists(modelPath);

        public string? GetModelPath(string language) => modelPath;
    }

    private sealed class WorkerLogger(TextWriter output) : ILogger<WhisperLocalTranscriber>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 700)
            {
                output.WriteLine(JsonSerializer.Serialize(new WorkerMessage(null, null, Started: true)));
                output.Flush();
            }
        }
    }
}
