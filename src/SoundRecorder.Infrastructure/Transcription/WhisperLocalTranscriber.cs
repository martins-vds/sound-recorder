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

    public WhisperLocalTranscriber(
        ITranscriptionModelStore models,
        ILogger<WhisperLocalTranscriber>? logger = null)
    {
        _models = models;
        _logger = logger ?? NullLogger<WhisperLocalTranscriber>.Instance;
    }

    public async Task<TranscriptDocument> TranscribeAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        var model = _models.GetModelPath(recording.Language)
            ?? throw new InvalidOperationException(
                $"No compatible local transcription model is installed for {recording.Language}.");
        using var factory = WhisperFactory.FromPath(model);
        using var processor = factory.CreateBuilder()
            .WithLanguage(ToWhisperLanguage(recording.Language))
            .Build();
        await using var audio = File.OpenRead(recording.AudioPath);
        var segments = new List<TranscriptSegment>();
        InfrastructureLog.TranscriptionStarted(_logger, recording.Id);

        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            var start = segment.Start;
            var end = segment.End;
            if (end <= start || end > recording.CapturedDuration)
            {
                continue;
            }

            var text = segment.Text.Trim();
            if (text.Length > 0)
            {
                segments.Add(new TranscriptSegment(start, end, text));
            }
        }

        InfrastructureLog.TranscriptionProducedSegments(
            _logger,
            segments.Count,
            recording.Id);
        return new TranscriptDocument(recording, recording.Language, segments);
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
