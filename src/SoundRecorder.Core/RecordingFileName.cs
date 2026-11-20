namespace SoundRecorder.Core;

public static class RecordingFileName
{
    public static string CreateUnique(
        string destinationFolder,
        string? title,
        DateTimeOffset timestamp)
    {
        var stem = string.IsNullOrWhiteSpace(title)
            ? timestamp.ToString("yyyyMMdd-HHmmss")
            : $"{timestamp:yyyyMMdd-HHmmss}-{Sanitize(title)}";
        var candidate = Path.Combine(destinationFolder, $"{stem}.wav");
        for (var attempt = 1; File.Exists(candidate); attempt++)
        {
            candidate = Path.Combine(destinationFolder, $"{stem}-{attempt}.wav");
        }

        return candidate;
    }

    public static string Sanitize(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(
                title.Select(character => invalid.Contains(character) ? '-' : character))
            .Trim();
    }
}
