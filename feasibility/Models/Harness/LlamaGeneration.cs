using System.Text;
using LLama.Common;

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
    private readonly INativeModel weights;
    private readonly NativeContextOptions parameters;

    /// <summary>Non-EOG sampled tokens yielded by the most recent call, including partial output on failure.</summary>
    public int LastGeneratedTokens { get; private set; }
    /// <summary>Native model tensor size, not total process memory.</summary>
    public ulong NativeModelBytes => weights.SizeInBytes;
    /// <summary>Loaded GGUF metadata for freezing the actual tokenizer/configuration identity.</summary>
    public IReadOnlyDictionary<string, string> Metadata => weights.Metadata;

    private LlamaGeneration(INativeModel weights, NativeContextOptions parameters)
    {
        this.weights = weights;
        this.parameters = parameters;
    }

    /// <summary>Checks every generation shard before loading the first shard.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, CancellationToken cancellationToken = default)
        => await LoadAsync(directory, LlamaNativeModel.LoadAsync, cancellationToken);

    /// <summary>Uses a replaceable native loader after the same pinned local-file verification.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, NativeModelLoader loader,
        CancellationToken cancellationToken = default)
        => await LoadAsync(directory, loader, ModelFiles.VerifyAsync, cancellationToken);

    /// <summary>Tests native policy deterministically without replacing or downloading the approved model files.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, NativeModelLoader loader,
        ModelFileVerifier verifier, CancellationToken cancellationToken = default)
    {
        var files = ModelFiles.CandidateFiles().Where(f => f.Role == "generation").ToArray();
        await verifier(directory, files, cancellationToken);
        var parameters = new NativeContextOptions(Path.Combine(directory, files[0].Name))
        {
            ContextSize = 4096, GpuLayerCount = 0, NoKqvOffload = true, OpOffload = false,
            Threads = 4, BatchThreads = 4, BatchSize = 128, UBatchSize = 128
        };
        var weights = await loader(parameters, cancellationToken);
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
    public int CountTokens(string prompt) => weights.CountTokens(prompt, true, true, Encoding.UTF8);
    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
    {
        LastGeneratedTokens = 0;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputTokens);
        cancellationToken.ThrowIfCancellationRequested();
        if (CountTokens(prompt) + (long)outputTokens >= parameters.ContextSize)
        {
            throw new InvalidDataException("Native generation context budget exceeded; no truncation.");
        }
        var inference = new NativeGenerationOptions(outputTokens, CandidateGrammar, 42, 0, ContextOverflowStrategy.ThrowException);
        var text = new StringBuilder();
        await foreach (var piece in weights.InferAsync(prompt, parameters, inference, cancellationToken))
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
