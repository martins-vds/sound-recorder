using Models.Harness;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evaluation.Generator;

namespace Models.Harness.Tests;

internal sealed record EvaluationFixture(TranscriptIdentity Identity, string Language, Excerpt[] Excerpts)
{
    public static EvaluationFixture Minute(string language)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Evaluation");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "corpus-manifest.json")));
        var entry = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Single(e => e.GetProperty("corpusId").GetString() == $"sr-eval-v1-{language}-minute");
        var seed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, $"{language}.seed.json")))!;
        using var writer = new StringWriter();
        CorpusGenerator.Write(writer, seed, blocks: 1, timestamps: true);
        var jsonl = writer.ToString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(jsonl)));
        if (hash != entry.GetProperty("transcriptSha256").GetString())
        {
            throw new InvalidDataException("The read-only evaluation source no longer matches its frozen manifest.");
        }
        var excerpts = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<Excerpt>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToArray();
        return new(new(entry.GetProperty("successfulTranscriptionAttemptId").GetString()!, hash), language, excerpts);
    }
}
