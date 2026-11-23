namespace Models.Harness;

/// <summary>The embedding-only native seam; input already contains its task prefix.</summary>
public interface IEmbeddingBackend
{
    /// <summary>Counts the prefixed input including the native tokenizer's special tokens.</summary>
    int CountTokens(string input);
    /// <summary>Returns one pooled sequence vector, never per-token hidden states.</summary>
    Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Exact excerpt and candidate similarity; top-k is not whole-source coverage.</summary>
public sealed record RetrievalMatch(Excerpt Excerpt, double Similarity);

/// <summary>Small-fixture brute-force retrieval, deliberately not a persistent index.</summary>
public sealed class EmbeddingProbe(IEmbeddingBackend backend, int dimensions = 768, int contextTokens = 512)
{
    /// <summary>Embeds a question and all supplied excerpts using distinct task prefixes.</summary>
    public async Task<RetrievalMatch[]> RetrieveAsync(string question, IReadOnlyList<Excerpt> excerpts, int topK = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);
        if (excerpts.Count == 0)
        {
            throw new InvalidDataException("No source excerpts supplied for retrieval.");
        }
        var query = await VectorAsync("search_query: " + question, cancellationToken);
        var matches = new List<RetrievalMatch>();
        foreach (var excerpt in excerpts)
        {
            var document = await VectorAsync("search_document: " + excerpt.Text, cancellationToken);
            double dot = 0;
            for (var i = 0; i < dimensions; i++)
            {
                dot += query[i] * document[i];
            }
            matches.Add(new(excerpt, dot));
        }
        return matches.OrderByDescending(m => m.Similarity).ThenBy(m => m.Excerpt.Id, StringComparer.Ordinal)
            .Take(topK).ToArray();
    }

    private async Task<double[]> VectorAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = backend.CountTokens(input);
        if (count > contextTokens)
        {
            throw new InvalidDataException($"Embedding input including prefix/special tokens ({count}) exceeds {contextTokens}; no truncation.");
        }
        var vector = await backend.EmbedAsync(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (vector.Length != dimensions || vector.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException($"Embedding must contain {dimensions} finite values.");
        }
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (norm == 0)
        {
            throw new InvalidDataException("Zero embedding cannot be normalized.");
        }
        return vector.Select(value => value / norm).ToArray();
    }
}
