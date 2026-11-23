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
    public Task<RetrievalMatch[]> RetrieveAsync(string question, IReadOnlyList<Excerpt> excerpts, int topK = 3,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(question, AsAsync(excerpts), topK, cancellationToken);

    /// <summary>Streams token-fitted overlapping original ranges, embedding one query per request.</summary>
    public async Task<RetrievalMatch[]> RetrieveAsync(string question, IAsyncEnumerable<Excerpt> excerpts, int topK = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);
        await using var sources = excerpts.GetAsyncEnumerator(cancellationToken);
        if (!await sources.MoveNextAsync())
        {
            throw new InvalidDataException("No source excerpts supplied for retrieval.");
        }
        var query = await VectorAsync("search_query: " + question, cancellationToken);
        var matches = new List<RetrievalMatch>();
        do
        {
            foreach (var excerpt in Ranges(sources.Current))
            {
                var document = await VectorAsync("search_document: " + excerpt.Text, cancellationToken);
                double dot = 0;
                for (var i = 0; i < dimensions; i++)
                    dot += query[i] * document[i];
                matches.Add(new(excerpt, dot));
                matches = matches.OrderByDescending(m => m.Similarity).ThenBy(m => m.Excerpt.Id, StringComparer.Ordinal)
                    .Take(topK).ToList();
            }
        } while (await sources.MoveNextAsync());
        return matches.ToArray();
    }

    private IEnumerable<Excerpt> Ranges(Excerpt source)
    {
        if (string.IsNullOrWhiteSpace(source.Text)) throw new InvalidDataException("A source excerpt contains no text.");
        var offset = 0;
        while (offset < source.Text.Length)
        {
            var remaining = source.Text[offset..];
            var length = SourceText.Boundary(remaining, Math.Min(1024, remaining.Length));
            while (backend.CountTokens("search_document: " + remaining[..length]) > contextTokens)
                length = SourceText.Boundary(remaining, SourceText.ShorterScalarPrefix(remaining, length));
            var text = remaining[..length];
            var id = offset == 0 && length == source.Text.Length ? source.Id : $"{source.Id}@{offset}";
            yield return source with { Id = id, Text = text };
            var overlap = length < remaining.Length ? SourceText.OverlapLength(text, Math.Min(256, length / 2)) : 0;
            offset += length - overlap;
        }
    }

    private static async IAsyncEnumerable<Excerpt> AsAsync(IEnumerable<Excerpt> excerpts)
    {
        foreach (var excerpt in excerpts) yield return excerpt;
        await Task.CompletedTask;
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
