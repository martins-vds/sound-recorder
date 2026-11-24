using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class NativeModelBoundaryTests
{
    [Theory]
    [InlineData("generation")]
    [InlineData("embedding")]
    public async Task LoadAsync_UninstalledModel_ReportsExplicitFailure(string role)
    {
        if (role == "generation")
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => LlamaGeneration.LoadAsync(AppContext.BaseDirectory));
        }
        else
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => LlamaEmbedding.LoadAsync(AppContext.BaseDirectory));
        }
    }
}
