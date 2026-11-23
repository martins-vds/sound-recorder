using System.Text;
using LLama.Common;

namespace SoundRecorder.Analysis;

/// <summary>Generation-only LLamaSharp CPU adapter; files must already be approved and installed locally.</summary>
public sealed class LlamaGeneration : IGenerationBackend, IDisposable
{
    private const string CandidateGrammar = """
        root ::= "{" ws "\"claims\"" ws ":" ws "[" ws (claim (ws "," ws claim)*)? ws "]" ws "," ws "\"limitation\"" ws ":" ws (string | "null") ws "}" ws
        claim ::= "{" ws "\"kind\"" ws ":" ws ("\"Topic\"" | "\"Decision\"" | "\"Action\"" | "\"Report\"") ws "," ws "\"statement\"" ws ":" ws string ws "," ws "\"owner\"" ws ":" ws field ws "," ws "\"deadline\"" ws ":" ws field ws "," ws "\"qualifiers\"" ws ":" ws "[" ws (string (ws "," ws string)*)? ws "]" ws "," ws "\"references\"" ws ":" ws "[" ws reference (ws "," ws reference)* ws "]" ws "}"
        reference ::= [0-3]
        field ::= "{" ws "\"state\"" ws ":" ws ("\"Absent\"" ws "," ws "\"value\"" ws ":" ws "null" | "\"Stated\"" ws "," ws "\"value\"" ws ":" ws string) ws "}"
        string ::= "\"" ([^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]))* "\""
        ws ::= [ \t\n\r]*
        """;
    private readonly INativeModel weights;
    private readonly NativeContextOptions parameters;
    private readonly bool qwen3;
    private readonly bool phiMini;

    /// <summary>Non-EOG sampled tokens yielded by the most recent call, including partial output on failure.</summary>
    public int LastGeneratedTokens { get; private set; }
    /// <summary>Native model tensor size, not total process memory.</summary>
    public ulong NativeModelBytes => weights.SizeInBytes;
    /// <summary>Loaded GGUF metadata for freezing the actual tokenizer/configuration identity.</summary>
    public IReadOnlyDictionary<string, string> Metadata => weights.Metadata;

    private LlamaGeneration(INativeModel weights, NativeContextOptions parameters, bool qwen3, bool phiMini)
    {
        this.weights = weights;
        this.parameters = parameters;
        this.qwen3 = qwen3;
        this.phiMini = phiMini;
    }

    /// <summary>Checks every generation shard before loading the first shard.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, CancellationToken cancellationToken = default)
        => await LoadAsync(directory, LlamaNativeModel.LoadAsync, cancellationToken);

    /// <summary>Explicitly loads the separately pinned non-thinking alternate, never as a fallback.</summary>
    public static Task<LlamaGeneration> LoadQwen3Async(string directory, CancellationToken cancellationToken = default)
        => LoadAsync(directory, LlamaNativeModel.LoadAsync, ModelFiles.VerifyAsync, cancellationToken, qwen3: true);

    /// <summary>Explicitly loads the approved Phi-4-mini conversion, never another Phi model or a fallback.</summary>
    public static Task<LlamaGeneration> LoadPhiMiniAsync(string directory, CancellationToken cancellationToken = default)
        => LoadAsync(directory, LlamaNativeModel.LoadAsync, ModelFiles.VerifyAsync, cancellationToken, phiMini: true);

    /// <summary>Uses a replaceable native loader after the same pinned local-file verification.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, NativeModelLoader loader,
        CancellationToken cancellationToken = default)
        => await LoadAsync(directory, loader, ModelFiles.VerifyAsync, cancellationToken);

    /// <summary>Tests native policy deterministically without replacing or downloading the approved model files.</summary>
    public static async Task<LlamaGeneration> LoadAsync(string directory, NativeModelLoader loader,
        ModelFileVerifier verifier, CancellationToken cancellationToken = default, bool qwen3 = false, bool phiMini = false)
    {
        var (profileFiles, architecture) = (qwen3, phiMini) switch
        {
            (false, false) => (ModelFiles.CandidateFiles(), "qwen2"),
            (true, false) => (ModelFiles.Qwen3Files(), "qwen3"),
            (false, true) => (ModelFiles.PhiMiniFiles(), "phi3"),
            _ => throw new ArgumentException("Select one explicit generation profile, not both alternates.")
        };
        var files = profileFiles.Where(f => f.Role == "generation").ToArray();
        await verifier(directory, files, cancellationToken);
        var parameters = new NativeContextOptions(Path.Combine(directory, files[0].Name))
        {
            ContextSize = 4096, GpuLayerCount = 0, NoKqvOffload = true, OpOffload = false,
            Threads = 4, BatchThreads = 4, BatchSize = 128, UBatchSize = 128
        };
        var weights = await loader(parameters, cancellationToken);
        try
        {
            if (weights.Metadata["general.architecture"] != architecture)
            {
                throw new InvalidDataException($"The pinned generation model is not {architecture}.");
            }
            return new(weights, parameters, qwen3, phiMini);
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string RenderPrompt(string instruction, string input) =>
        phiMini ? $"<|system|>{instruction}<|end|><|user|>{input}<|end|><|assistant|>" :
        $"<|im_start|>system\n{instruction}{(qwen3 ? " /no_think" : "")}<|im_end|>\n<|im_start|>user\n{input}<|im_end|>\n<|im_start|>assistant\n" +
        (qwen3 ? "<think>\n\n</think>\n\n" : "");
    /// <inheritdoc />
    public int CountTokens(string prompt) => weights.CountTokens(prompt, true, true, Encoding.UTF8);
    /// <inheritdoc />
    public async Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
    {
        LastGeneratedTokens = 0;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputTokens);
        cancellationToken.ThrowIfCancellationRequested();
        if (CountTokens(prompt) + (long)outputTokens >= parameters.ContextSize)
        {
            throw new InvalidDataException("Native generation context budget exceeded; no truncation.");
        }
        var inference = qwen3
            ? new NativeGenerationOptions(outputTokens, CandidateGrammar, 42, 0.7f, ContextOverflowStrategy.ThrowException)
                { TopK = 20, TopP = 0.8f, MinP = 0, PresencePenalty = 1.5f }
            : new NativeGenerationOptions(outputTokens, CandidateGrammar, 42, 0, ContextOverflowStrategy.ThrowException);
        try
        {
            var result = await weights.InferAsync(prompt, parameters, inference, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            LastGeneratedTokens = weights.LastGeneratedTokens;
        }
    }
    /// <inheritdoc />
    public void Dispose() => weights.Dispose();
}
