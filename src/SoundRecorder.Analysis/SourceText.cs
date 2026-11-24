using System.Text;

namespace SoundRecorder.Analysis;

internal static class SourceText
{
    internal static int OverlapLength(string text, int maximum)
    {
        var start = Math.Max(0, text.Length - maximum);
        while (start > 0 && start < text.Length && !char.IsWhiteSpace(text[start - 1])) start++;
        return text.Length - start;
    }

    internal static int ShorterScalarPrefix(string text, int length)
    {
        var shorter = length / 2;
        if (shorter > 0 && char.IsHighSurrogate(text[shorter - 1])) shorter--;
        shorter = Math.Max(shorter, Rune.GetRuneAt(text, 0).Utf16SequenceLength);
        if (shorter >= length) throw new InvalidDataException("No complete Unicode scalar fits the context budget.");
        return shorter;
    }

    internal static int Boundary(string text, int length)
    {
        if (length == text.Length) return length;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        for (var index = length - 1; index > length / 2; index--)
            if (IsSentenceBoundary(text, index))
                return index + 1;
        var space = text.AsSpan(0, length).LastIndexOfAny(" \t\r\n".AsSpan());
        return space > length / 2 ? space + 1 : length;
    }

    private static bool IsSentenceBoundary(string text, int index) =>
        char.IsWhiteSpace(text[index]) && text[index - 1] is '.' or '!' or '?';
}
