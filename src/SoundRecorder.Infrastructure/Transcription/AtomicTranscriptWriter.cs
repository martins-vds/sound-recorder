using System.Globalization;
using System.Text;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Infrastructure.Transcription;

public sealed class AtomicTranscriptWriter : ITranscriptWriter
{
    private readonly ILogger<AtomicTranscriptWriter> _logger;

    public AtomicTranscriptWriter(
        ILogger<AtomicTranscriptWriter>? logger = null)
    {
        _logger = logger ?? NullLogger<AtomicTranscriptWriter>.Instance;
    }

    public async Task<TranscriptOutputs> WriteAsync(
        TranscriptDocument document,
        CancellationToken cancellationToken)
    {
        Validate(document);
        var (textPath, subtitlePath) = ChooseOutputPaths(document.Recording.AudioPath);
        var temporaryTextPath = $"{textPath}.{Guid.NewGuid():N}.tmp";
        var temporarySubtitlePath = $"{subtitlePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(
                temporaryTextPath,
                FormatText(document),
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                temporarySubtitlePath,
                FormatSubtitles(document),
                new UTF8Encoding(false),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryTextPath, textPath);
            File.Move(temporarySubtitlePath, subtitlePath);
            InfrastructureLog.TranscriptPairSaved(_logger, document.Recording.Id);
            return new TranscriptOutputs(textPath, subtitlePath);
        }
        catch (Exception exception)
        {
            DeleteIfExists(temporaryTextPath);
            DeleteIfExists(temporarySubtitlePath);
            DeleteIfExistsIfNew(textPath, temporaryTextPath);
            DeleteIfExistsIfNew(subtitlePath, temporarySubtitlePath);
            InfrastructureLog.TranscriptPairSaveFailed(
                _logger,
                exception,
                document.Recording.Id);
            throw;
        }
    }

    private static (string Text, string Subtitle) ChooseOutputPaths(string audioPath)
    {
        var directory = Path.GetDirectoryName(audioPath)!;
        var stem = Path.GetFileNameWithoutExtension(audioPath);
        for (var attempt = 1; ; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : $".transcript-{attempt}";
            var text = Path.Combine(directory, $"{stem}{suffix}.txt");
            var subtitle = Path.Combine(directory, $"{stem}{suffix}.srt");
            if (!File.Exists(text) && !File.Exists(subtitle))
            {
                return (text, subtitle);
            }
        }
    }

    private static string FormatText(TranscriptDocument document)
    {
        return string.Join(
            Environment.NewLine,
            document.Segments.Select(segment =>
                $"[{FormatTextTime(segment.Start)}] {segment.Text}"));
    }

    private static string FormatSubtitles(TranscriptDocument document)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < document.Segments.Count; index++)
        {
            var segment = document.Segments[index];
            builder.Append(index + 1).AppendLine();
            builder.Append(FormatSubtitleTime(segment.Start))
                .Append(" --> ")
                .AppendLine(FormatSubtitleTime(segment.End));
            builder.AppendLine(segment.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void Validate(TranscriptDocument document)
    {
        var previousEnd = TimeSpan.Zero;
        foreach (var segment in document.Segments)
        {
            if (segment.Start < previousEnd ||
                segment.Start >= segment.End ||
                segment.End > document.Duration)
            {
                throw new InvalidDataException(
                    "Transcript segments must be ordered, non-overlapping, and within the saved audio duration.");
            }

            previousEnd = segment.End;
        }
    }

    private static string FormatTextTime(TimeSpan value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}");
    }

    private static string FormatSubtitleTime(TimeSpan value)
    {
        return FormatTextTime(value).Replace('.', ',');
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteIfExistsIfNew(string finalPath, string temporaryPath)
    {
        if (!File.Exists(temporaryPath) && File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }
    }
}
