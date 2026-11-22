using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Transcription;

/// <summary>Isolates native inference so cancellation cannot terminate the recorder.</summary>
public sealed class WhisperProcessTranscriber : IJobTranscriber
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
    public Task<TranscriptDocument> TranscribeAsync(
        TranscriptionRequest request, IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken) =>
        TranscribeAsync(request, progress, true, cancellationToken);

    public Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        CancellationToken cancellationToken) =>
        TranscribeAsync(new(Guid.NewGuid(), recording, null, null), null, false, cancellationToken);

    private async Task<TranscriptDocument> TranscribeAsync(
        TranscriptionRequest job, IProgress<TranscriptionProgress>? progress, bool pinned,
        CancellationToken cancellationToken)
    {
        var recording = job.Recording;
        cancellationToken.ThrowIfCancellationRequested();
        var model = (pinned
            ? job.ModelPath ?? _models.GetModelPath(recording.Language, job.ModelId)
            : _models.GetModelPath(recording.Language))
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
                var request = new WorkerRequest(model, recording, Path.Combine(directory, "processing.wav"), job.JobId);
                using (var input = process.Input)
                {
                    await input.WriteLineAsync(
                        JsonSerializer.Serialize(request).AsMemory(),
                        cancellationToken);
                }
                var result = await ReadResultAsync(process, recording.Id, job.JobId, progress, cancellationToken);
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
        Guid jobId = Guid.Empty;
        try
        {
            var line = await input.ReadLineAsync()
                ?? throw new InvalidDataException("No local transcription request was supplied.");
            var request = JsonSerializer.Deserialize<WorkerRequest>(line)
                ?? throw new InvalidDataException("The local transcription request is invalid.");
            jobId = request.JobId;
            var frames = Channel.CreateUnbounded<WorkerMessage>(new() { SingleReader = true });
            var pump = Task.Run(async () =>
            {
                await foreach (var frame in frames.Reader.ReadAllAsync())
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(frame));
                    await output.FlushAsync();
                }
            });
            var transcriber = createTranscriber(
                new WorkerModelStore(request.ModelPath),
                new WorkerLogger(output, jobId, frames.Writer));
            TranscriptDocument document;
            try
            {
                document = jobId == Guid.Empty
                    ? await transcriber.TranscribeAsync(request.Recording, request.ProcessingAudioPath, CancellationToken.None)
                    : await transcriber.TranscribeAsync(
                        new(jobId, request.Recording, null, request.ModelPath), request.ProcessingAudioPath,
                        new FrameProgress(frames.Writer, jobId), CancellationToken.None);
            }
            finally
            {
                frames.Writer.TryComplete();
                await pump;
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage(document.Segments, null, JobId: jobId, Kind: "Result")));
            await output.FlushAsync();
            return 0;
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new WorkerMessage(null, exception.Message, JobId: jobId, Kind: "Error")));
            await output.FlushAsync();
            return 1;
        }
    }

    private async Task<WorkerMessage> ReadResultAsync(
        ITranscriptionWorker process,
        Guid recordingId,
        Guid jobId,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (await process.Output.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = ParseWorkerMessage(line);
            if (IsForeignMessage(message, jobId)) continue;
            ValidateFrameIdentity(message);
            if (message.Kind == "Progress")
            {
                ReportWorkerProgress(message, progress);
                continue;
            }
            ValidateFrameKind(message);
            if (IsResultMessage(message))
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

    private static WorkerMessage ParseWorkerMessage(string line) =>
        JsonSerializer.Deserialize<WorkerMessage>(line)
            ?? throw new InvalidDataException("The local transcription worker returned an invalid response.");

    private static bool IsForeignMessage(WorkerMessage message, Guid jobId) =>
        message.JobId != Guid.Empty && message.JobId != jobId;

    private static void ValidateFrameIdentity(WorkerMessage message)
    {
        if (message.Kind is not null && message.JobId == Guid.Empty)
            throw new InvalidDataException("A typed worker frame must identify its job.");
    }

    private static void ReportWorkerProgress(WorkerMessage message, IProgress<TranscriptionProgress>? progress)
    {
        var value = message.Progress ?? throw new InvalidDataException("Progress frame has no progress.");
        ValidateWorkerProgress(value);
        progress?.Report(value);
    }

    private static void ValidateWorkerProgress(TranscriptionProgress value)
    {
        if (HasInvalidWorkerProgress(value))
            throw new InvalidDataException("Invalid worker progress.");
    }

    private static bool HasInvalidWorkerProgress(TranscriptionProgress value) =>
        !Enum.IsDefined(value.Phase) || value.Percent is < 0 or > 100;

    private static void ValidateFrameKind(WorkerMessage message)
    {
        if (HasUnknownFrameKind(message.Kind))
            throw new InvalidDataException("Unknown local transcription worker frame.");
    }

    private static bool HasUnknownFrameKind(string? kind) =>
        kind is not (null or "Started" or "Result" or "Error");

    private static bool IsResultMessage(WorkerMessage message) =>
        !message.Started && message.Kind != "Started";

    private sealed record WorkerRequest(
        string ModelPath,
        SavedRecording Recording,
        string ProcessingAudioPath,
        Guid JobId = default);

    private sealed record WorkerMessage(
        IReadOnlyList<TranscriptSegment>? Segments,
        string? Error,
        bool Started = false,
        Guid JobId = default,
        string? Kind = null,
        TranscriptionProgress? Progress = null);

    private sealed class FrameProgress(ChannelWriter<WorkerMessage> frames, Guid jobId) : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) =>
            frames.TryWrite(new(null, null, JobId: jobId, Kind: "Progress", Progress: value));
    }

    private sealed class WorkerModelStore(string modelPath) : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => File.Exists(modelPath);

        public string? GetModelPath(string language) => modelPath;
    }

    private sealed class WorkerLogger(TextWriter output, Guid jobId, ChannelWriter<WorkerMessage> frames) : ILogger<WhisperLocalTranscriber>
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
                var message = new WorkerMessage(null, null, Started: true, JobId: jobId, Kind: "Started");
                if (jobId == Guid.Empty)
                {
                    output.WriteLine(JsonSerializer.Serialize(message));
                    output.Flush();
                }
                else frames.TryWrite(message);
            }
        }
    }
}
