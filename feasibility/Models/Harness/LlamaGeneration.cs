using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Models.Harness;

/// <summary>Generation-only LLamaSharp CPU adapter; files must already be approved and installed locally.</summary>
public sealed class LlamaGeneration : IGenerationBackend, IDisposable
{
    private const string CandidateGrammar = """
        root ::= "{" ws "\"claims\"" ws ":" ws "[" ws (claim (ws "," ws claim)*)? ws "]" ws "," ws "\"limitation\"" ws ":" ws (string | "null") ws "}" ws
        claim ::= "{" ws "\"text\"" ws ":" ws string ws "," ws "\"evidenceIds\"" ws ":" ws "[" ws id (ws "," ws id)* ws "]" ws "}"
        id ::= "\"s" [0-9] [0-9] [0-9] [0-9] [0-9] [0-9] "\""
        string ::= "\"" ([^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]))* "\""
        ws ::= [ \t\n\r]*
        """;
    private readonly LLamaWeights weights;
    private readonly ModelParams parameters;

    /// <summary>Non-EOG sampled tokens yielded by the most recent call, including partial output on failure.</summary>
    public int LastGeneratedTokens { get; private set; }
    /// <summary>Native model tensor size, not total process memory.</summary>
    public ulong NativeModelBytes => weights.SizeInBytes;
    /// <summary>Loaded GGUF metadata for freezing the actual tokenizer/configuration identity.</summary>
    public IReadOnlyDictionary<string, string> Metadata => weights.Metadata;

    private LlamaGeneration(LLamaWeights weights, ModelParams parameters)
    {
        this.weights = weights;
        this.parameters = parameters;
    }

    /// <summary>Checks every generation shard before loading the first shard.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var files = ModelFiles.CandidateFiles().Where(f => f.Role == "generation").ToArray();
        await ModelFiles.VerifyAsync(directory, files, cancellationToken);
        NativeCpu.Probe();
        var parameters = new ModelParams(Path.Combine(directory, files[0].Name))
        {
            ContextSize = 4096, GpuLayerCount = 0, NoKqvOffload = true, OpOffload = false,
            Threads = 4, BatchThreads = 4, BatchSize = 128, UBatchSize = 128
        };
        var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
        try
        {
            if (weights.Metadata["general.architecture"] != "qwen2")
            {
                throw new InvalidDataException("The pinned generation model is not qwen2.");
            }
            return new(weights, parameters);
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string RenderPrompt(string instruction, string input) =>
        $"<|im_start|>system\n{instruction}<|im_end|>\n<|im_start|>user\n{input}<|im_end|>\n<|im_start|>assistant\n";
    /// <inheritdoc />
    public int CountTokens(string prompt) => weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputTokens);
        cancellationToken.ThrowIfCancellationRequested();
        if (CountTokens(prompt) + (long)outputTokens >= parameters.ContextSize)
        {
            throw new InvalidDataException("Native generation context budget exceeded; no truncation.");
        }
        var executor = new StatelessExecutor(weights, parameters) { ApplyTemplate = false };
        using var sampling = new DefaultSamplingPipeline
        {
            Seed = 42, Temperature = 0, Grammar = new Grammar(CandidateGrammar, "root")
        };
        var inference = new InferenceParams
        {
            MaxTokens = outputTokens, SamplingPipeline = sampling,
            OverflowStrategy = ContextOverflowStrategy.ThrowException
        };
        LastGeneratedTokens = 0;
        var text = new StringBuilder();
        await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken))
        {
            LastGeneratedTokens++;
            text.Append(piece);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return text.ToString();
    }
    /// <inheritdoc />
    public void Dispose() => weights.Dispose();
}
