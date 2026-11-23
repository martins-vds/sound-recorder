using System.Text;
using System.Runtime.CompilerServices;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Native;

namespace Models.Harness;

/// <summary>Replaceable native model/resource boundary, separate from policy and file integrity.</summary>
public interface INativeModel : IDisposable
{
    IReadOnlyDictionary<string, string> Metadata { get; }
    ulong SizeInBytes { get; }
    int EmbeddingSize { get; }
    int LastGeneratedTokens { get; }
    int CountTokens(string text, bool addSpecial, bool parseSpecial, Encoding encoding);
    Task<GenerationResult> InferAsync(string prompt, NativeContextOptions context, NativeGenerationOptions inference, CancellationToken cancellationToken);
    void InitializeEmbedding(NativeContextOptions context);
    Task<IReadOnlyList<float[]>> EmbedAsync(string input, CancellationToken cancellationToken);
}

/// <summary>One in-process native load; callers retain explicit role and integrity validation.</summary>
public delegate Task<INativeModel> NativeModelLoader(NativeContextOptions parameters, CancellationToken cancellationToken);

/// <summary>Managed CPU/context settings; no LLamaSharp constructors run during model-free policy tests.</summary>
public sealed record NativeContextOptions(string ModelPath)
{
    public uint ContextSize { get; init; }
    public int GpuLayerCount { get; init; }
    public bool NoKqvOffload { get; init; }
    public bool? OpOffload { get; init; }
    public int Threads { get; init; }
    public int BatchThreads { get; init; }
    public uint BatchSize { get; init; }
    public uint UBatchSize { get; init; }
    public bool Embeddings { get; init; }
    public LLamaPoolingType PoolingType { get; init; } = LLamaPoolingType.Unspecified;
}

/// <summary>Managed inference policy; constructing it never initializes a native backend.</summary>
public sealed record NativeGenerationOptions(int MaxTokens, string Grammar, uint Seed, float Temperature, ContextOverflowStrategy OverflowStrategy)
{
    public int TopK { get; init; } = 40;
    public float TopP { get; init; } = 0.9f;
    public float MinP { get; init; } = 0.1f;
    public float PresencePenalty { get; init; }
}

/// <summary>Thin LLamaSharp implementation of the native boundary; no model selection or fallback.</summary>
public sealed class LlamaNativeModel : INativeModel
{
    private readonly LLamaWeights weights;
    private readonly ModelParams parameters;
    private LLamaEmbedder? embedder;

    private LlamaNativeModel(LLamaWeights weights, ModelParams parameters)
    {
        this.weights = weights;
        this.parameters = parameters;
    }

    public IReadOnlyDictionary<string, string> Metadata => weights.Metadata;
    public ulong SizeInBytes => weights.SizeInBytes;
    public int EmbeddingSize => weights.EmbeddingSize;
    /// <summary>Non-EOG native samples from the most recent call, including failed/cancelled calls.</summary>
    public int LastGeneratedTokens { get; private set; }

    public static async Task<INativeModel> LoadAsync(NativeContextOptions options, CancellationToken cancellationToken)
    {
        NativeCpu.Probe();
        var parameters = new ModelParams(options.ModelPath)
        {
            ContextSize = options.ContextSize, GpuLayerCount = options.GpuLayerCount,
            NoKqvOffload = options.NoKqvOffload, OpOffload = options.OpOffload,
            Threads = options.Threads, BatchThreads = options.BatchThreads, BatchSize = options.BatchSize,
            UBatchSize = options.UBatchSize, Embeddings = options.Embeddings, PoolingType = options.PoolingType
        };
        return new LlamaNativeModel(await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken), parameters);
    }

    public int CountTokens(string text, bool addSpecial, bool parseSpecial, Encoding encoding) =>
        weights.Tokenize(text, addSpecial, parseSpecial, encoding).Length;

    public async Task<GenerationResult> InferAsync(string prompt, NativeContextOptions context, NativeGenerationOptions settings,
        CancellationToken cancellationToken)
    {
        using var sampling = new ObservedSamplingPipeline
        {
            Seed = settings.Seed, Temperature = settings.Temperature, Grammar = new Grammar(settings.Grammar, "root"),
            TopK = settings.TopK, TopP = settings.TopP, MinP = settings.MinP, PresencePenalty = settings.PresencePenalty
        };
        var inference = new InferenceParams
        {
            MaxTokens = settings.MaxTokens, SamplingPipeline = sampling, OverflowStrategy = settings.OverflowStrategy
        };
        var executor = new StatelessExecutor(weights, parameters) { ApplyTemplate = false };
        var text = new StringBuilder();
        LastGeneratedTokens = 0;
        try
        {
            await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken))
            {
                text.Append(piece);
            }
            return new(text.ToString(), sampling.EndOfGeneration ? GenerationFinish.Completed : GenerationFinish.TokenLimit,
                sampling.GeneratedTokens);
        }
        finally
        {
            LastGeneratedTokens = sampling.GeneratedTokens;
        }
    }

    public void InitializeEmbedding(NativeContextOptions context) => embedder = new LLamaEmbedder(weights, parameters);

    public Task<IReadOnlyList<float[]>> EmbedAsync(string input, CancellationToken cancellationToken) =>
        (embedder ?? throw new InvalidOperationException("Embedding context has not been initialized.")).GetEmbeddings(input, cancellationToken);

    public void Dispose()
    {
        embedder?.Dispose();
        weights.Dispose();
    }

    private sealed class ObservedSamplingPipeline : DefaultSamplingPipeline
    {
        public int GeneratedTokens { get; private set; }
        public bool EndOfGeneration { get; private set; }

        public override LLamaToken Sample(SafeLLamaContextHandle context, int index)
        {
            var token = base.Sample(context, index);
            EndOfGeneration = token.IsEndOfGeneration(context.ModelHandle.Vocab);
            if (!EndOfGeneration)
            {
                GeneratedTokens++;
            }
            return token;
        }
    }
}
