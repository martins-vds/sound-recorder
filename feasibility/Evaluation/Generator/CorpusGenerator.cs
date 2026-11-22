using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Evaluation.Generator;

/// <summary>Writes canonical synthetic transcript excerpts without accumulating a transcript.</summary>
public static class CorpusGenerator
{
    /// <summary>Writes one-based excerpt identities and substitutes the block marker in authored text.</summary>
    public static void Write(TextWriter writer, IReadOnlyList<string> seed, int blocks, bool timestamps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blocks);
        if (seed.Count == 0)
        {
            throw new ArgumentException("At least one authored excerpt is required.", nameof(seed));
        }
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        long index = 0;
        for (var block = 1; block <= blocks; block++)
        {
            foreach (var text in seed)
            {
                var start = index * 3000;
                index++;
                writer.Write(JsonSerializer.Serialize(new
                {
                    id = "s" + index.ToString("D6", CultureInfo.InvariantCulture),
                    startMs = timestamps ? (long?)start : null,
                    endMs = timestamps ? (long?)(start + 3000) : null,
                    text = text.Replace("{block}", block.ToString("D6", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                }, options));
                writer.Write('\n');
            }
        }
    }
}
