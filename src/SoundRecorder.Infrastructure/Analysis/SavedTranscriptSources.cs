using System.Globalization;
using System.Text;
using Models.Harness;

namespace SoundRecorder.Infrastructure.Analysis;

internal static class SavedTranscriptSources
{
    public static IEnumerable<Excerpt> Read(string textPath, string subtitlePath, CancellationToken token)
    {
        using var text = new StreamReader(textPath, new UTF8Encoding(false, true));
        using var subtitles = new StreamReader(subtitlePath, new UTF8Encoding(false, true));
        var index = 0;
        var previousEnd = TimeSpan.Zero;
        foreach (var cue in ReadCues(subtitles, token))
        {
            if (cue.Start < previousEnd) throw new InvalidDataException("Saved SRT cues overlap or are out of order.");
            previousEnd = cue.End;
            if (index++ > 0) Match(text, Environment.NewLine);
            var quote = ReadQuote(text, cue.Start, cue.Body);
            yield return new($"cue-{index} [{cue.Timing}]", (long)cue.Start.TotalMilliseconds, (long)cue.End.TotalMilliseconds, quote);
        }
        if (index == 0 || text.Read() != -1)
            throw new InvalidDataException("The saved TXT/SRT pair is empty or disagrees; no source text was discarded.");
    }

    private static IEnumerable<SavedCue> ReadCues(StreamReader subtitles, CancellationToken token)
    {
        var index = 0;
        string? number;
        while ((number = ReadLine(subtitles, token)) is not null)
        {
            if (number.Length == 0) continue;
            ValidateOrdinal(number, ++index);
            yield return ReadCue(subtitles, token);
        }
    }

    private static void ValidateOrdinal(string number, int expected)
    {
        if (!int.TryParse(number, CultureInfo.InvariantCulture, out var ordinal) || ordinal != expected)
            throw new InvalidDataException("Saved SRT cue numbering is invalid.");
    }

    private static SavedCue ReadCue(StreamReader reader, CancellationToken token)
    {
        var timing = ReadLine(reader, token) ?? throw new InvalidDataException("Saved SRT timing is missing.");
        var times = timing.Split(" --> ", StringSplitOptions.None);
        if (times.Length != 2) throw new InvalidDataException("Saved SRT timing is invalid.");
        var start = ParseTime(times[0]);
        var end = ParseTime(times[1]);
        if (start < TimeSpan.Zero || end <= start) throw new InvalidDataException("Saved SRT cue times are invalid.");
        return new(timing, start, end, ReadBody(reader, token));
    }

    private sealed record SavedCue(string Timing, TimeSpan Start, TimeSpan End, IReadOnlyList<string> Body);

    private static TimeSpan ParseTime(string value) =>
        TimeSpan.TryParseExact(value, @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture, out var time) ? time :
            throw new InvalidDataException("Saved SRT timestamp is invalid.");

    private static IReadOnlyList<string> ReadBody(StreamReader reader, CancellationToken token)
    {
        var body = new List<string>();
        var length = 0;
        string? line;
        while ((line = ReadLine(reader, token)) is not null && line.Length != 0)
        {
            length += line.Length + Environment.NewLine.Length;
            if (length > 65_536) throw new InvalidDataException("A saved SRT cue exceeds the bounded reader capacity; no truncation.");
            body.Add(line);
        }
        return body.Count > 0 ? body : throw new InvalidDataException("A saved SRT cue contains no text.");
    }

    private static string ReadQuote(StreamReader reader, TimeSpan start, IReadOnlyList<string> body)
    {
        var prefix = $"[{start:hh\\:mm\\:ss\\.fff}] ";
        Match(reader, prefix);
        var quote = new StringBuilder(prefix);
        for (var index = 0; index < body.Count; index++)
        {
            if (index > 0) quote.Append(ReadLineBreak(reader));
            Match(reader, body[index]);
            quote.Append(body[index]);
        }
        return quote.ToString();
    }

    private static string ReadLineBreak(StreamReader reader) => reader.Read() switch
    {
        '\n' => "\n",
        '\r' when reader.Read() == '\n' => "\r\n",
        _ => throw new InvalidDataException("Saved TXT/SRT line boundaries disagree.")
    };

    private static string? ReadLine(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        int character;
        while ((character = reader.Read()) != -1)
        {
            token.ThrowIfCancellationRequested();
            if (character == '\n') return line.ToString();
            if (character == '\r')
            {
                if (reader.Read() != '\n') throw new InvalidDataException("A saved SRT line has an invalid line ending.");
                return line.ToString();
            }
            line.Append((char)character);
            if (line.Length > 65_536) throw new InvalidDataException("A saved transcript line exceeds the bounded reader capacity; no truncation.");
        }
        return line.Length == 0 ? null : line.ToString();
    }

    private static void Match(StreamReader reader, string expected)
    {
        foreach (var character in expected)
            if (reader.Read() != character)
                throw new InvalidDataException("The saved TXT/SRT pair disagrees; original excerpts cannot be attributed safely.");
    }
}
