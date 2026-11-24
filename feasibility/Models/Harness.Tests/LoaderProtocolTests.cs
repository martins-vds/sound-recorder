using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class LoaderProtocolTests
{
    [Theory]
    [InlineData("qwen3", "Qwen3-8B-Q4_K_M.gguf")]
    [InlineData("phi", "Phi-4-mini-instruct-Q4_K_M.gguf")]
    public async Task Load_ExplicitFamilyMissingFiles_FailsForIndependentlyPinnedFileBeforeNative(string family, string expectedFile)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "missing-models", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var error = family == "qwen3"
                ? await Assert.ThrowsAsync<FileNotFoundException>(() => LlamaGeneration.LoadQwen3Async(directory))
                : await Assert.ThrowsAsync<FileNotFoundException>(() => LlamaGeneration.LoadPhiMiniAsync(directory));
            Assert.Equal(expectedFile, error.FileName);
            Assert.Contains("approved", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderPrompt_QwenFamily_TransportsRolesAndOnlySelectedNonThinkingBranch(bool alternate)
    {
        var native = new NativeDouble { Architecture = alternate ? "qwen3" : "qwen2" };
        using var generation = await LlamaGeneration.LoadAsync("cache", native.Load, (_, _, _) => Task.CompletedTask, qwen3: alternate);
        var prompt = generation.RenderPrompt("Use transcript.", "ação");
        Assert.StartsWith("<|im_start|>system\nUse transcript.", prompt);
        Assert.Contains("<|im_end|>\n<|im_start|>user\nação<|im_end|>\n<|im_start|>assistant\n", prompt);
        if (alternate)
        {
            Assert.Contains(" /no_think<|im_end|>", prompt);
            Assert.EndsWith("<think>\n\n</think>\n\n", prompt);
        }
        else
        {
            Assert.DoesNotContain("/no_think", prompt);
            Assert.DoesNotContain("<think>", prompt);
            Assert.EndsWith("<|im_start|>assistant\n", prompt);
            Assert.Equal("<|im_start|>system\nUse transcript.<|im_end|>\n<|im_start|>user\nação<|im_end|>\n<|im_start|>assistant\n",prompt);
        }
    }
}
