using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
[Trait("purpose", "PhiMiniNative")]
public sealed class PhiMiniNativeTests
{
    [Fact]
    public async Task Tokenize_ApprovedPhiMini_RecognizesExactRoleTokensWithoutAddingBosOrEos()
    {
        var directory = ApprovedCache();
        var files = ModelFiles.PhiMiniFiles();
        await ModelFiles.VerifyAsync(directory, files);
        NativeCpu.Probe();
        using var weights = await LLamaWeights.LoadFromFileAsync(new ModelParams(Path.Combine(directory, files.Single().Name))
        {
            ContextSize = 128, GpuLayerCount = 0, NoKqvOffload = true, OpOffload = false,
            Threads = 4, BatchThreads = 4, BatchSize = 32, UBatchSize = 32
        });
        const string roleSequence = "<|system|><|end|><|user|><|end|><|assistant|>";
        var tokens = weights.Tokenize(roleSequence, true, true, Encoding.UTF8);
        Assert.Equal([200022, 200020, 200021, 200020, 200019], tokens.Select(token => (int)token));
        Assert.Equal(tokens, weights.Tokenize(roleSequence, false, true, Encoding.UTF8));
        Assert.True(weights.Tokenize(roleSequence, true, false, Encoding.UTF8).Length > tokens.Length);
        Assert.False(weights.NativeHandle.Vocab.ShouldAddBOS);
        Assert.False(weights.NativeHandle.Vocab.ShouldAddEOS);
        Assert.Equal(200020, (int)weights.NativeHandle.Vocab.EOS!.Value);
        Assert.NotEqual(weights.NativeHandle.Vocab.EOS, weights.NativeHandle.Vocab.Pad);
        Assert.Equal(200064, weights.NativeHandle.Vocab.Count);
        Assert.True(tokens[1].IsEndOfGeneration(weights.Vocab));
        Assert.False(tokens[0].IsEndOfGeneration(weights.Vocab));
        Assert.False(weights.NativeHandle.Vocab.Pad!.Value.IsEndOfGeneration(weights.Vocab));
    }

    [Fact]
    public async Task GenerateAsync_ApprovedPhiMini_ExecutesOwnTemplateAndBoundedJsonOnNativeCpu()
    {
        var generation = await LlamaGeneration.LoadPhiMiniAsync(ApprovedCache());
        try
        {
            var prompt = generation.RenderPrompt(
                """Return only JSON {"claims":[],"limitation":"OK"}.""", "Confirm.");
            var text = await generation.GenerateAsync(prompt, 64, CancellationToken.None);
            var output = JsonSerializer.Deserialize<CandidateOutput>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(output);
            Assert.Empty(output.Claims);
            Assert.Equal("OK", output.Limitation);
            Assert.InRange(generation.LastGeneratedTokens, 1, 63);
            Assert.True(generation.NativeModelBytes > 0);
            Assert.True(generation.CountTokens(prompt) < 4096 - 64);
        }
        finally
        {
            generation.Dispose();
        }
        Assert.ThrowsAny<Exception>(() => generation.CountTokens("source"));
    }

    private static string ApprovedCache() => Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY")
        ?? throw new InvalidOperationException("Configure the already approved external model cache.");
}
