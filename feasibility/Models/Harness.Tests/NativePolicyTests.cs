using System.Runtime.CompilerServices;
using System.Text;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class NativePolicyTests
{
    [Fact]
    public async Task LoadAsync_ConflictingAlternateProfiles_FailsBeforeVerificationOrLoad()
    {
        var native = new NativeDouble { Architecture = "phi3" };
        var verified = false;
        Task VerifyNeither(string _, IReadOnlyList<PinnedModelFile> __, CancellationToken ___)
        {
            verified = true;
            return Task.CompletedTask;
        }
        var error=await Assert.ThrowsAsync<ArgumentException>(() =>
            LlamaGeneration.LoadAsync("cache", native.Load, VerifyNeither, qwen3: true, phiMini: true));
        Assert.Contains("one explicit generation profile",error.Message);
        Assert.False(verified);
        Assert.Null(native.Parameters);
    }

    [Fact]
    public async Task GenerateAsync_ExplicitPhiMiniProfile_UsesItsOwnTemplateAndGreedySampling()
    {
        var native = new NativeDouble { Architecture = "phi3" };
        string[]? verified = null;
        Task VerifyPhiMini(string _, IReadOnlyList<PinnedModelFile> files, CancellationToken __)
        {
            verified = files.Select(file => file.Name).ToArray();
            return Task.CompletedTask;
        }
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, VerifyPhiMini, phiMini: true);
        var prompt = generation.RenderPrompt("Use evidence.", "ação");
        Assert.Equal("firstsecond", (await generation.GenerateAsync(prompt, 8, CancellationToken.None)).Text);
        Assert.Equal("<|system|>Use evidence.<|end|><|user|>ação<|end|><|assistant|>", native.Prompt);
        Assert.NotNull(verified);
        Assert.Equal(["Phi-4-mini-instruct-Q4_K_M.gguf"], verified);
        Assert.Equal(0f, native.Inference!.Temperature);
        Assert.Equal(0f, native.Inference.PresencePenalty);
        Assert.Equal(ContextOverflowStrategy.ThrowException, native.Inference.OverflowStrategy);
        Assert.Equal(4096u, native.Parameters!.ContextSize);
        Assert.True(native.AddSpecial && native.ParseSpecial);
    }

    [Fact]
    public async Task GenerateAsync_ExplicitQwen3Profile_UsesNonThinkingTemplateAndSampling()
    {
        var native = new NativeDouble { Architecture = "qwen3" };
        string[]? verified = null;
        Task VerifyQwen3(string _, IReadOnlyList<PinnedModelFile> files, CancellationToken __)
        {
            verified = files.Select(file => file.Name).ToArray();
            return Task.CompletedTask;
        }
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, VerifyQwen3, qwen3: true);
        var prompt = generation.RenderPrompt("Use evidence.", "ação");
        Assert.Equal("<|im_start|>system\nUse evidence. /no_think<|im_end|>\n<|im_start|>user\nação<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);
        Assert.Equal("firstsecond", (await generation.GenerateAsync(prompt, 8, CancellationToken.None)).Text);
        Assert.NotNull(verified);
        Assert.Equal(["Qwen3-8B-Q4_K_M.gguf"], verified);
        Assert.Equal(prompt, native.Prompt);
        Assert.Equal(0.7f, native.Inference!.Temperature);
        Assert.Equal(0.8f, native.Inference.TopP);
        Assert.Equal(20, native.Inference.TopK);
        Assert.Equal(0f, native.Inference.MinP);
        Assert.Equal(1.5f, native.Inference.PresencePenalty);
        Assert.Equal(4096u, native.Parameters!.ContextSize);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("embedding")]
    public async Task LoadAsync_IntegrityFailure_NeverOpensNativeModel(string role)
    {
        var opened = false;
        Task<INativeModel> Load(NativeContextOptions _, CancellationToken __)
        {
            opened = true;
            return Task.FromResult<INativeModel>(new NativeDouble());
        }
        Task Verify(string _, IReadOnlyList<PinnedModelFile> __, CancellationToken ___) =>
            throw new InvalidDataException("SHA-256 mismatch in approved cache.");
        var error = role == "generation"
            ? await Assert.ThrowsAsync<InvalidDataException>(() => LlamaGeneration.LoadAsync("cache", Load, Verify))
            : await Assert.ThrowsAsync<InvalidDataException>(() => LlamaEmbedding.LoadAsync("cache", Load, Verify));
        Assert.Contains("SHA-256", error.Message);
        Assert.False(opened);
    }

    [Theory]
    [InlineData("generation", "bert", 768, false, false)]
    [InlineData("generation", "qwen3", 768, false, false)]
    [InlineData("generation", "qwen2", 768, true, false)]
    [InlineData("generation", "qwen2", 768, false, true)]
    [InlineData("generation", "phi4", 768, false, true)]
    [InlineData("embedding", "qwen2", 768, false, false)]
    [InlineData("embedding", "nomic-bert-moe", 384, false, false)]
    public async Task LoadAsync_IncompatibleNativeModel_DisposesWithoutInference(string role, string architecture, int dimensions, bool qwen3, bool phiMini)
    {
        var native = new NativeDouble { Architecture = architecture, EmbeddingSize = dimensions };
        var error = role == "generation"
            ? await Assert.ThrowsAsync<InvalidDataException>(() => LlamaGeneration.LoadAsync("cache", native.Load, Verify(role), qwen3: qwen3, phiMini: phiMini))
            : await Assert.ThrowsAsync<InvalidDataException>(() => LlamaEmbedding.LoadAsync("cache", native.Load, Verify(role)));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.True(native.Disposed);
        Assert.Equal(0, native.Inferences);
    }

    [Fact]
    public async Task GenerateAsync_CancelledSecondCall_DoesNotReportPreviousTokens()
    {
        var native = new NativeDouble();
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, Verify("generation"));
        Assert.Equal("firstsecond", (await generation.GenerateAsync("prompt", 8, CancellationToken.None)).Text);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.GenerateAsync("prompt", 8, cancellation.Token));
        Assert.Equal(0, generation.LastGeneratedTokens);
        Assert.Equal(1, native.Inferences);
    }

    [Fact]
    public async Task GenerateAsync_CpuBoundedStructuredCall_ForwardsSafeNativeConfiguration()
    {
        var native = new NativeDouble();
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, Verify("generation"));
        var rendered = generation.RenderPrompt("instruction", "original evidence");
        Assert.Equal("firstsecond", (await generation.GenerateAsync(rendered, 8, CancellationToken.None)).Text);
        Assert.Equal(rendered, native.Prompt);
        Assert.Equal(2, generation.LastGeneratedTokens);
        Assert.Equal(0, native.Parameters!.GpuLayerCount);
        Assert.True(native.Parameters.NoKqvOffload);
        Assert.False(native.Parameters.OpOffload);
        Assert.True(native.Parameters.ContextSize > 8);
        Assert.Equal(8, native.Inference!.MaxTokens);
        Assert.Equal(ContextOverflowStrategy.ThrowException, native.Inference.OverflowStrategy);
        Assert.False(string.IsNullOrWhiteSpace(native.Inference.Grammar));
        Assert.True(native.AddSpecial && native.ParseSpecial);
        Assert.Equal(Encoding.UTF8, native.Encoding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4096)]
    [InlineData(4093)]
    public async Task GenerateAsync_InvalidBudget_FailsBeforeDecode(int output)
    {
        var native = new NativeDouble();
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, Verify("generation"));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => generation.GenerateAsync("prompt", output, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(0, native.Inferences);
    }

    [Fact]
    public async Task EmbedAsync_CompleteNonCausalSequence_ForwardsMeanPoolingAndOneBatch()
    {
        var native = new NativeDouble { Architecture = "nomic-bert-moe" };
        using var embedding = await LlamaEmbedding.LoadAsync("cache", native.Load, Verify("embedding"));
        var vector = await embedding.EmbedAsync("search_document: ação", CancellationToken.None);
        Assert.Equal([1f, 2f], vector);
        Assert.Equal(LLamaPoolingType.Mean, native.Parameters!.PoolingType);
        Assert.True(native.Parameters.Embeddings);
        Assert.Equal(0, native.Parameters.GpuLayerCount);
        Assert.True(native.Parameters.NoKqvOffload);
        Assert.False(native.Parameters.OpOffload);
        Assert.Equal(native.Parameters.BatchSize, native.Parameters.UBatchSize);
        Assert.True(native.Parameters.BatchSize >= native.TokenCount);
        Assert.Equal("search_document: ação", native.Prompt);
        Assert.True(native.AddSpecial && native.ParseSpecial);
        embedding.Dispose();
        Assert.True(native.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EmbedAsync_UnexpectedVectorCount_ReportsFailure(int vectors)
    {
        var native = new NativeDouble { Architecture = "nomic-bert-moe", Vectors = Enumerable.Repeat(new float[] { 1 }, vectors).ToArray() };
        using var embedding = await LlamaEmbedding.LoadAsync("cache", native.Load, Verify("embedding"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => embedding.EmbedAsync("document", CancellationToken.None));
        Assert.Contains("one", error.Message);
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(513, true)]
    public async Task EmbedAsync_NativeContextBoundary_NoSilentTruncation(int tokens, bool rejects)
    {
        var native = new NativeDouble { Architecture = "nomic-bert-moe", TokenCount = tokens };
        using var embedding = await LlamaEmbedding.LoadAsync("cache", native.Load, Verify("embedding"));
        if (rejects)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => embedding.EmbedAsync("source", CancellationToken.None));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.Equal(0, native.Embeddings);
        }
        else
        {
            var vector = await embedding.EmbedAsync("source", CancellationToken.None);
            Assert.Equal([1f, 2f], vector);
        }
    }

    [Fact]
    public async Task EmbedAsync_InitializationFailure_ReleasesModelAndPreservesCause()
    {
        var cause = new InvalidOperationException("Native context allocation unavailable.");
        var native = new NativeDouble { Architecture = "nomic-bert-moe", InitializationFailure = cause };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LlamaEmbedding.LoadAsync("cache", native.Load, Verify("embedding")));
        Assert.Same(cause, error);
        Assert.True(native.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmbedAsync_CancellationBeforeOrDuringNativeCall_DoesNotReturnSuccess(bool before)
    {
        using var cancellation = new CancellationTokenSource();
        var native = new NativeDouble { Architecture = "nomic-bert-moe", OnEmbed = cancellation.Cancel };
        using var embedding = await LlamaEmbedding.LoadAsync("cache", native.Load, Verify("embedding"));
        if (before) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embedding.EmbedAsync("source", cancellation.Token));
        Assert.Equal(before ? 0 : 1, native.Embeddings);
    }

    [Fact]
    public async Task GenerateAsync_CancelledAfterNativeYield_DoesNotReturnPartialText()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new NativeDouble { OnGenerated = cancellation.Cancel };
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, Verify("generation"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.GenerateAsync("source", 8, cancellation.Token));
    }

    private static ModelFileVerifier Verify(string role) => (_, files, _) =>
    {
        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.Equal(role, file.Role));
        return Task.CompletedTask;
    };
}

internal sealed class NativeDouble : INativeModel
{
    public string Architecture { get; init; } = "qwen2";
    public IReadOnlyDictionary<string, string> Metadata => new Dictionary<string, string> { ["general.architecture"] = Architecture };
    public ulong SizeInBytes => 100;
    public int EmbeddingSize { get; init; } = 768;
    public int LastGeneratedTokens => 2;
    public int TokenCount { get; init; } = 3;
    public bool Disposed { get; private set; }
    public int Inferences { get; private set; }
    public NativeContextOptions? Parameters { get; private set; }
    public NativeGenerationOptions? Inference { get; private set; }
    public string? Prompt { get; private set; }
    public bool AddSpecial { get; private set; }
    public bool ParseSpecial { get; private set; }
    public Encoding? Encoding { get; private set; }
    public IReadOnlyList<float[]> Vectors { get; init; } = [new float[] { 1, 2 }];
    public Exception? InitializationFailure { get; init; }
    public Action? OnGenerated { get; init; }
    public Action? OnEmbed { get; init; }
    public int Embeddings { get; private set; }
    public GenerationFinish Finish { get; init; } = GenerationFinish.Completed;
    private bool embeddingInitialized;
    public Task<INativeModel> Load(NativeContextOptions parameters, CancellationToken cancellationToken)
    {
        Parameters = parameters;
        return Task.FromResult<INativeModel>(this);
    }
    public int CountTokens(string text, bool addSpecial, bool parseSpecial, Encoding encoding)
    {
        AddSpecial = addSpecial;
        ParseSpecial = parseSpecial;
        Encoding = encoding;
        return TokenCount;
    }
    public async Task<GenerationResult> InferAsync(string prompt, NativeContextOptions context, NativeGenerationOptions inference,
        CancellationToken cancellationToken)
    {
        Prompt = prompt;
        Parameters = context;
        Inference = inference;
        Inferences++;
        await Task.Yield();
        OnGenerated?.Invoke();
        return new("firstsecond", Finish, 2);
    }
    public void InitializeEmbedding(NativeContextOptions context)
    {
        if (InitializationFailure is not null) throw InitializationFailure;
        Parameters = context;
        embeddingInitialized = true;
    }
    public Task<IReadOnlyList<float[]>> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        if (!embeddingInitialized) throw new InvalidOperationException("Embedding was not initialized.");
        Embeddings++;
        Prompt = input;
        OnEmbed?.Invoke();
        return Task.FromResult(Vectors);
    }
    public void Dispose() => Disposed = true;
}
