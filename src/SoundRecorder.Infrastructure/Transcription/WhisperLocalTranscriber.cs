using SoundRecorder.Core;
using Whisper.net;

namespace SoundRecorder.Infrastructure.Transcription;

public sealed class WhisperLocalTranscriber : ILocalTranscriber
{
    private readonly ITranscriptionModelStore _models;

    public WhisperLocalTranscriber(ITranscriptionModelStore models)
    {
        _models = models;
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
