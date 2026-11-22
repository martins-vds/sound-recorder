using Whisper.net.Ggml;

namespace SoundRecorder.Infrastructure.Models;

public static class TranscriptionModelCatalog
{
    // Whisper.net 1.9.1, commit 98278acc38ae23590cdfa9859f78f089abae52a7, uses the v4 raw GGML mapping.
    public static IReadOnlyList<ModelCatalogEntry> Entries { get; } = CreateEntries();

    public static ModelCatalogEntry? Find(string key) =>
        Entries.FirstOrDefault(entry => entry.Key == key);

    private static IReadOnlyList<ModelCatalogEntry> CreateEntries()
    {
        string[] families =
        [
            "tiny", "tiny.en", "base", "base.en", "small", "small.en",
            "medium", "medium.en", "large-v1", "large-v2", "large-v3", "large-v3-turbo"
        ];
        QuantizationType[] encodings =
        [
            QuantizationType.NoQuantization, QuantizationType.Q4_0, QuantizationType.Q4_1,
            QuantizationType.Q5_0, QuantizationType.Q5_1, QuantizationType.Q8_0
        ];
        var entries = new List<ModelCatalogEntry>();
        foreach (var family in families)
        foreach (var encoding in encodings)
        {
            var directory = encoding == QuantizationType.NoQuantization
                ? "classic" : encoding.ToString().ToLowerInvariant();
            var languages = family.EndsWith(".en", StringComparison.Ordinal)
                ? new[] { "English" } : ["English", "Brazilian Portuguese"];
            entries.Add(new ModelCatalogEntry(
                $"whisper.net:{family}:{encoding}", family,
                $"{family} {(encoding == QuantizationType.NoQuantization ? "original" : encoding)}",
                encoding, Array.AsReadOnly(languages),
                new Uri($"https://huggingface.co/sandrohanea/whisper.net/resolve/v4/{directory}/ggml-{family}.bin")));
        }
        return entries.AsReadOnly();
    }
}
