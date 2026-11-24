using System.Text.Json;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis;

public sealed partial class AnalysisProcessingService
{
    private async Task IndexAsync(TranscriptAnalysisRequest request, AnalysisStoreRevision? previous, CancellationToken token)
    {
        var snapshot = await store.LoadAsync(revision.Identity, token);
        var checkpoint = snapshot.Checkpoint;
        ValidateCheckpoint(checkpoint);
        var retained = checkpoint?.NextOrdinal ?? 0;
        var reusable = retained > 0 ? revision : previous;
        var spool = Path.Combine(artifactDirectory, Guid.NewGuid().ToString("N") + ".index.jsonl");
        try
        {
            await SpoolAsync(reusable, spool, token);
            await CommitIndexAsync(request, spool, retained, token);
        }
        finally
        {
            if (File.Exists(spool)) File.Delete(spool);
        }
    }

    private void ValidateCheckpoint(AnalysisStoreCheckpoint? checkpoint)
    {
        if (checkpoint is not null && checkpoint.Cursor != revision.Identity.AlgorithmVersion)
            throw new InvalidDataException("Incompatible processing checkpoint; explicit rebuild is required.");
    }

    private async Task CommitIndexAsync(TranscriptAnalysisRequest request, string spool, int retained, CancellationToken token)
    {
        using var reader = new StreamReader(spool);
        var ordinal = 0;
        foreach (var chunk in SourceChunks(request, token))
        {
            var line = await reader.ReadLineAsync(token);
            var indexed = await ReuseOrEmbedAsync(line, chunk, retained, token);
            if (ordinal++ >= retained)
                await store.SaveCheckpointAsync(revision, new(ordinal, "Index", revision.Identity.AlgorithmVersion),
                    new[] { indexed }.ToAsyncEnumerable(), token);
        }
        if (await reader.ReadLineAsync(token) is not null || ordinal < retained)
            throw new InvalidDataException("Retained index is not an exact complete source prefix.");
        await store.SaveCheckpointAsync(revision, new(ordinal, "Indexed", revision.Identity.AlgorithmVersion),
            Array.Empty<AnalysisStoreChunk>().ToAsyncEnumerable(), token);
    }

    private async Task<AnalysisStoreChunk> ReuseOrEmbedAsync(string? line, AnalysisStoreChunk chunk, int retained, CancellationToken token)
    {
        if (line is not null) return ValidateReused(JsonSerializer.Deserialize<AnalysisStoreChunk>(line)!, chunk);
        if (chunk.Ordinal < retained)
            throw new InvalidDataException("A committed source/index prefix is missing; explicit rebuild is required.");
        return chunk with { Vector = await VectorAsync("search_document: " + chunk.Text, token) };
    }

    private async Task SpoolAsync(AnalysisStoreRevision? reusable, string path, CancellationToken token)
    {
        await using var writer = new StreamWriter(path);
        if (reusable is null) return;
        await foreach (var chunk in store.ReadChunksAsync(reusable, token))
            await writer.WriteLineAsync(JsonSerializer.Serialize(chunk).AsMemory(), token);
    }

    private AnalysisStoreChunk ValidateReused(AnalysisStoreChunk saved, AnalysisStoreChunk source)
    {
        if (saved with { Vector = source.Vector } != source)
            throw new InvalidDataException("Retained source/index ranges are incompatible.");
        ValidateVector(saved.Vector);
        return saved;
    }

    private IEnumerable<AnalysisStoreChunk> SourceChunks(TranscriptAnalysisRequest request, CancellationToken token)
    {
        var ordinal = 0;
        foreach (var source in SavedTranscriptSources.Read(request.Outputs.TextPath, request.Outputs.SubtitlePath, token))
        {
            for (var offset = 0; offset < source.Text.Length;)
            {
                token.ThrowIfCancellationRequested();
                var length = Math.Min(1024, source.Text.Length - offset);
                if (char.IsHighSurrogate(source.Text[offset + length - 1])) length--;
                while (embedding.CountTokens("search_document: " + source.Text.Substring(offset, length)) > 512)
                {
                    length = Shorter(source.Text, offset, length);
                }
                yield return new(ordinal++, source.Id, offset, source.StartMs, source.EndMs,
                    source.Text.Substring(offset, length), []);
                offset += length;
            }
        }
    }

    private static int Shorter(string text, int offset, int length)
    {
        var shorter = length / 2;
        if (shorter > 0 && char.IsHighSurrogate(text[offset + shorter - 1])) shorter--;
        if (shorter == 0) throw new InvalidDataException("A complete source scalar cannot fit embedding context; no truncation.");
        return shorter;
    }

    private async Task<IReadOnlyList<float>> VectorAsync(string input, CancellationToken token)
    {
        if (embedding.CountTokens(input) > 512) throw new InvalidDataException("Query exceeds embedding token budget; no truncation.");
        using var lease = enterInferenceAsync is null ? null : await enterInferenceAsync(token);
        var vector = await embedding.EmbedAsync(input, token);
        token.ThrowIfCancellationRequested();
        ValidateVector(vector);
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    private void ValidateVector(IReadOnlyList<float> vector)
    {
        if (vector.Count != revision.Identity.VectorDimensions || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Embedding identity dimensions or finite values are incompatible.");
        if (vector.All(value => value == 0))
            throw new InvalidDataException("A zero embedding is not usable.");
    }

    private async Task<AnalysisStoreChunk[]> RetrieveAsync(string question, CancellationToken token)
    {
        var query = await VectorAsync("search_query: " + question, token);
        var matches = new List<(AnalysisStoreChunk Chunk, double Score)>();
        await foreach (var chunk in store.ReadChunksAsync(revision, token))
        {
            var score = chunk.Vector.Select((value, index) => (double)value * query[index]).Sum();
            matches.Add((chunk, score));
            matches = matches.OrderByDescending(match => match.Score).ThenBy(match => match.Chunk.Ordinal).Take(3).ToList();
        }
        return matches.Select(match => match.Chunk).ToArray();
    }
}
