using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using Whisper.net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Infrastructure.Transcription;

public sealed class WhisperLocalTranscriber : ILocalTranscriber
{
    private readonly ITranscriptionModelStore _models;
    private readonly ILogger<WhisperLocalTranscriber> _logger;
    private readonly Func<string, SavedRecording, Stream, CancellationToken,
        Task<IReadOnlyList<TranscriptSegment>>> _processAudio;

    public WhisperLocalTranscriber(
        ITranscriptionModelStore models,
        ILogger<WhisperLocalTranscriber>? logger = null)
        : this(models, logger, ProcessWithWhisperAsync)
    {
    }

    internal WhisperLocalTranscriber(
        ITranscriptionModelStore models,
        ILogger<WhisperLocalTranscriber>? logger,
        Func<string, SavedRecording, Stream, CancellationToken,
            Task<IReadOnlyList<TranscriptSegment>>> processAudio)
    {
        _models = models;
        _logger = logger ?? NullLogger<WhisperLocalTranscriber>.Instance;
        _processAudio = processAudio;
    }

    public async Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        var model = _models.GetModelPath(recording.Language)
            ?? throw new InvalidOperationException(
                $"No compatible local transcription model is installed for {recording.Language}.");
        var temporaryAudioPath = Path.Combine(
            Path.GetTempPath(),
            $"sound-recorder-whisper-{Guid.NewGuid():N}.wav");
        try
        {
            ResampleForWhisper(
                recording.AudioPath,
                temporaryAudioPath,
                cancellationToken);
            await using var audio = File.OpenRead(temporaryAudioPath);
            InfrastructureLog.TranscriptionStarted(_logger, recording.Id);
            var segments = NormalizeSegments(
                await _processAudio(
                    model,
                    recording,
                    audio,
                    cancellationToken),
                recording.CapturedDuration);
            InfrastructureLog.TranscriptionProducedSegments(
                _logger,
                segments.Count,
                recording.Id);
            return new TranscriptDocument(recording, recording.Language, segments);
        }
        finally
        {
            if (File.Exists(temporaryAudioPath))
            {
                File.Delete(temporaryAudioPath);
            }
        }
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
                segment.End <= duration + tolerance)
            .Select(segment => segment.End > duration
                ? segment with { End = duration }
                : segment)
            .ToArray();
    }

    private static void ResampleForWhisper(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(sourcePath);
        var targetFormat = new WaveFormat(16_000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, targetFormat)
        {
            ResamplerQuality = 60,
        };
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
        SavedRecording recording,
        Stream audio,
        CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(model);
        using var processor = factory.CreateBuilder()
            .WithLanguage(ToWhisperLanguage(recording.Language))
            .Build();
        var segments = new List<TranscriptSegment>();

        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            var text = segment.Text.Trim();
            if (text.Length > 0)
            {
                segments.Add(new TranscriptSegment(segment.Start, segment.End, text));
            }
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
