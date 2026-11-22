using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using Whisper.net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Infrastructure.Transcription;

public sealed class WhisperLocalTranscriber : IJobTranscriber
{
    private readonly ITranscriptionModelStore _models;
    private readonly ILogger<WhisperLocalTranscriber> _logger;
    private readonly Func<string, string, Stream, CancellationToken,
        Task<IReadOnlyList<TranscriptSegment>>> _processAudio;
    private readonly bool _nativeProgress;

    public WhisperLocalTranscriber(
        ITranscriptionModelStore models,
        ILogger<WhisperLocalTranscriber>? logger = null)
        : this(models, logger, ProcessWithWhisperAsync)
    {
        _nativeProgress = true;
    }

    internal WhisperLocalTranscriber(
        ITranscriptionModelStore models,
        ILogger<WhisperLocalTranscriber>? logger,
        Func<string, string, Stream, CancellationToken,
            Task<IReadOnlyList<TranscriptSegment>>> processAudio)
    {
        _models = models;
        _logger = logger ?? NullLogger<WhisperLocalTranscriber>.Instance;
        _processAudio = processAudio;
    }

    public Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        return TranscribeAsync(
            recording,
            Path.Combine(Path.GetTempPath(), $"sound-recorder-whisper-{Guid.NewGuid():N}.wav"),
            cancellationToken);
    }

    internal async Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        string temporaryAudioPath,
        CancellationToken cancellationToken)
        => await TranscribeAsync(new(Guid.NewGuid(), recording, null, null), temporaryAudioPath, null, cancellationToken);

    public Task<TranscriptDocument> TranscribeAsync(
        TranscriptionRequest request, IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken) =>
        TranscribeAsync(request,
            Path.Combine(Path.GetTempPath(), $"sound-recorder-whisper-{Guid.NewGuid():N}.wav"), progress, cancellationToken);

    internal async Task<TranscriptDocument> TranscribeAsync(
        TranscriptionRequest request, string temporaryAudioPath,
        IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
    {
        var recording = request.Recording;
        var model = ResolveModelPath(request, recording);
        try
        {
            PrepareAudio(recording, temporaryAudioPath, progress, cancellationToken);
            await using var audio = File.OpenRead(temporaryAudioPath);
            var segments = await RecognizeAudioAsync(recording, model, audio, progress, cancellationToken);
            InfrastructureLog.TranscriptionProducedSegments(
                _logger,
                segments.Count,
                recording.Id);
            return new TranscriptDocument(recording, recording.Language, segments);
        }
        finally
        {
            DeleteProcessingAudio(temporaryAudioPath);
        }
    }

    private string ResolveModelPath(TranscriptionRequest request, SavedRecording recording)
    {
        var model = request.ModelPath ?? _models.GetModelPath(recording.Language, request.ModelId);
        return RequireModelPath(model, recording);
    }

    private static string RequireModelPath(string? model, SavedRecording recording) =>
        model ?? throw new InvalidOperationException(
            $"No compatible local transcription model is installed for {recording.Language}.");

    private static void PrepareAudio(
        SavedRecording recording, string temporaryAudioPath,
        IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(TranscriptionPhase.Preparing));
        ResampleForWhisper(recording.AudioPath, temporaryAudioPath, cancellationToken);
    }

    private async Task<IReadOnlyList<TranscriptSegment>> RecognizeAudioAsync(
        SavedRecording recording, string model, Stream audio,
        IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken)
    {
        InfrastructureLog.TranscriptionStarted(_logger, recording.Id);
        progress?.Report(new(TranscriptionPhase.Recognizing));
        var segments = await ProcessAudioAsync(recording, model, audio, progress, cancellationToken);
        return NormalizeSegments(segments, recording.CapturedDuration);
    }

    private Task<IReadOnlyList<TranscriptSegment>> ProcessAudioAsync(
        SavedRecording recording, string model, Stream audio,
        IProgress<TranscriptionProgress>? progress, CancellationToken cancellationToken) =>
        _nativeProgress
            ? ProcessWithWhisperAsync(model, ToWhisperLanguage(recording.Language), audio, cancellationToken, progress)
            : _processAudio(model, ToWhisperLanguage(recording.Language), audio, cancellationToken);

    private static void DeleteProcessingAudio(string temporaryAudioPath)
    {
        if (File.Exists(temporaryAudioPath)) File.Delete(temporaryAudioPath);
    }

    private static IReadOnlyList<TranscriptSegment> NormalizeSegments(
        IReadOnlyList<TranscriptSegment> segments,
        TimeSpan duration)
    {
        var tolerance = TimeSpan.FromMilliseconds(100);
        return segments
            .Where(segment =>
                segment.Start < duration &&
                segment.End > segment.Start &&
                segment.End <= duration + tolerance &&
                segment.Text.Trim().Length > 0)
            .Select(segment => segment with
            {
                End = TimeSpan.FromTicks(Math.Min(segment.End.Ticks, duration.Ticks)),
                Text = segment.Text.Trim(),
            })
            .ToArray();
    }

    private static void ResampleForWhisper(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(sourcePath);
        var targetFormat = new WaveFormat(16_000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, targetFormat);
        using var writer = new WaveFileWriter(destinationPath, targetFormat);
        var buffer = new byte[32 * 1024];
        int bytesRead;
        while ((bytesRead = resampler.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(buffer, 0, bytesRead);
        }
    }

    private static async Task<IReadOnlyList<TranscriptSegment>> ProcessWithWhisperAsync(
        string model,
        string language,
        Stream audio,
        CancellationToken cancellationToken)
        => await ProcessWithWhisperAsync(model, language, audio, cancellationToken, null);

    private static async Task<IReadOnlyList<TranscriptSegment>> ProcessWithWhisperAsync(
        string model, string language, Stream audio, CancellationToken cancellationToken,
        IProgress<TranscriptionProgress>? progress)
    {
        using var factory = WhisperFactory.FromPath(model);
        // Cancellation ends enumeration before native inference necessarily releases the model.
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithProgressHandler(percent => progress?.Report(new(TranscriptionPhase.Recognizing, percent)))
            .Build();
        var segments = new List<TranscriptSegment>();

        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            segments.Add(new TranscriptSegment(segment.Start, segment.End, segment.Text));
        }

        return segments;
    }

    private static string ToWhisperLanguage(string language)
    {
        return language switch
        {
            "English" => "en",
            "Brazilian Portuguese" => "pt",
            _ => throw new NotSupportedException($"Unsupported transcription language '{language}'."),
        };
    }
}
