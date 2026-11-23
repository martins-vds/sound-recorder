using System.Text;
using LLama.Common;
using LLama.Native;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
[Trait("purpose", "NativeAdapterSmoke")]
public sealed class NativeAdapterSmokeTests
{
    [Fact]
    public async Task NativeAdapter_TinyGeneration_UsesProvidedTokenizerGrammarAndReleasesModel()
    {
        var file = ModelFiles.CandidateFiles().First(f => f.Role == "generation");
        var context = CpuContext(file.Name, 128, false);
        var model = await LlamaNativeModel.LoadAsync(context, CancellationToken.None);
        try
        {
            Assert.Equal("qwen2", model.Metadata["general.architecture"]);
            Assert.True(model.SizeInBytes > 0);
            Assert.True(model.CountTokens("ação", true, true, Encoding.UTF8) > 0);
            var text = new StringBuilder();
            var prompt = "<|im_start|>system\nReply OK.<|im_end|>\n<|im_start|>user\nConfirm.";
            const string suffix = "<|im_end|>\n<|im_start|>assistant\n";
            while (model.CountTokens(prompt + suffix, true, true, Encoding.UTF8) < context.ContextSize - 8)
            {
                prompt += " word";
            }
            await foreach (var token in model.InferAsync(prompt + suffix,
                context, new(4, """root ::= "OK" """, 42, 0, ContextOverflowStrategy.ThrowException), CancellationToken.None))
            {
                text.Append(token);
            }
            Assert.Equal("OK", text.ToString());
        }
        finally
        {
            model.Dispose();
        }
        Assert.ThrowsAny<Exception>(() => model.CountTokens("source", true, true, Encoding.UTF8));
    }

    [Fact]
    public async Task NativeAdapter_EmbeddingInitialization_ReturnsOneFiniteSequenceVectorAndReleasesModel()
    {
        var file = ModelFiles.CandidateFiles().Single(f => f.Role == "embedding");
        var context = CpuContext(file.Name, 512, true);
        var model = await LlamaNativeModel.LoadAsync(context, CancellationToken.None);
        try
        {
            Assert.Equal(768, model.EmbeddingSize);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => model.EmbedAsync("source", CancellationToken.None));
            Assert.False(string.IsNullOrWhiteSpace(missing.Message));
            model.InitializeEmbedding(context);
            var vectors = await model.EmbedAsync("search_document: Ana verifica os registros.", CancellationToken.None);
            var vector = Assert.Single(vectors);
            Assert.Equal(model.EmbeddingSize, vector.Length);
            Assert.All(vector, value => Assert.True(float.IsFinite(value)));
        }
        finally
        {
            model.Dispose();
        }
        Assert.ThrowsAny<Exception>(() => model.CountTokens("source", true, true, Encoding.UTF8));
    }

    private static NativeContextOptions CpuContext(string file, uint context, bool embedding)
    {
        var cache = Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY")
            ?? throw new InvalidOperationException("Configure the already approved external model cache.");
        return new(Path.Combine(cache, file))
        {
            ContextSize = context, GpuLayerCount = 0, NoKqvOffload = true, OpOffload = false,
            Threads = 4, BatchThreads = 4, BatchSize = embedding ? 512u : 32u, UBatchSize = embedding ? 512u : 32u,
            Embeddings = embedding, PoolingType = embedding ? LLamaPoolingType.Mean : LLamaPoolingType.Unspecified
        };
    }
}
