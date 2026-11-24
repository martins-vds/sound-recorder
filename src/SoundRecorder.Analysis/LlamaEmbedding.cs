using System.Text;
using LLama.Common;
using LLama.Native;

namespace SoundRecorder.Analysis;

/// <summary>Separate multilingual embedding model; never uses generation hidden states as retrieval vectors.</summary>
public sealed class LlamaEmbedding : IEmbeddingBackend, IDisposable
{
    private readonly INativeModel weights;

    /// <summary>Native model tensor size, not total process memory.</summary>
    public ulong NativeModelBytes => weights.SizeInBytes;
    /// <summary>Loaded GGUF metadata for freezing the actual tokenizer/configuration identity.</summary>
    public IReadOnlyDictionary<string, string> Metadata => weights.Metadata;

    private LlamaEmbedding(INativeModel weights)
    {
        this.weights = weights;
    }

    /// <summary>Checks the embedding file before creating a native mean-pooling context.</summary>
    public static async Task<LlamaEmbedding> LoadAsync(string directory, CancellationToken cancellationToken = default)
        => await LoadAsync(directory, LlamaNativeModel.LoadAsync, cancellationToken);

    /// <summary>Uses a replaceable native loader after the same pinned local-file verification.</summary>
    public static async Task<LlamaEmbedding> LoadAsync(string directory, NativeModelLoader loader,
        CancellationToken cancellationToken = default)
        => await LoadAsync(directory, loader, ModelFiles.VerifyAsync, cancellationToken);

    /// <summary>Tests native policy deterministically without replacing or downloading the approved model files.</summary>
    public static async Task<LlamaEmbedding> LoadAsync(string directory, NativeModelLoader loader,
        ModelFileVerifier verifier, CancellationToken cancellationToken = default)
    {
        var files = ModelFiles.CandidateFiles().Where(f => f.Role == "embedding").ToArray();
        await verifier(directory, files, cancellationToken);
        var parameters = new NativeContextOptions(Path.Combine(directory, files[0].Name))
        {
            ContextSize = 512, GpuLayerCount = 0, NoKqvOffload = true, OpOffload = false,
            Threads = 4, BatchThreads = 4, BatchSize = 512, UBatchSize = 512,
            Embeddings = true, PoolingType = LLamaPoolingType.Mean
        };
        var weights = await loader(parameters, cancellationToken);
        try
        {
            if (weights.Metadata["general.architecture"] != "nomic-bert-moe" || weights.EmbeddingSize != 768)
            {
                throw new InvalidDataException("Expected nomic-bert-moe with 768 dimensions.");
            }
            weights.InitializeEmbedding(parameters);
            return new(weights);
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }
    /// <inheritdoc />
    public int CountTokens(string input) => weights.CountTokens(input, true, true, Encoding.UTF8);
    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CountTokens(input) > 512)
        {
            throw new InvalidDataException("Native embedding context budget exceeded; no truncation.");
        }
        var vectors = await weights.EmbedAsync(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (vectors.Count != 1)
        {
            throw new InvalidDataException("Mean pooling must return exactly one sequence vector.");
        }
        return vectors[0];
    }
    /// <inheritdoc />
    public void Dispose()
    {
        weights.Dispose();
    }
}
