using SoundRecorder.Analysis;
using Xunit;

namespace Models.Harness.Tests;

public sealed class ModelFilesTests
{
    [Fact]
    public void CandidateFiles_MissingResource_ReportsSetupFailureInsteadOfEmptyInventory()
    {
        var error = Assert.Throws<InvalidDataException>(() => ModelFiles.CandidateFiles(() => null));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Contains("profile", error.Message);
    }
    [Fact]
    public async Task VerifyAsync_MissingLocalModel_ReportsMissingFileWithoutNativeLoad()
    {
        var file = new PinnedModelFile("generation", "not-downloaded.gguf", 100, new string('a', 64), "unused");
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => ModelFiles.VerifyAsync(AppContext.BaseDirectory, [file]));
        Assert.Equal(file.Name, error.FileName);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_WrongSizeOrSameSizeCorruption_RejectsLocalFile(bool sameSize)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Evaluation", "pt-BR.seed.json");
        var directory = Path.Combine(AppContext.BaseDirectory, "scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = await File.ReadAllBytesAsync(source);
            var expected = new PinnedModelFile("fixture", "corrupt.json", bytes.Length + (sameSize ? 0 : 1),
                "26914d9e2017f2cdf92ab9fea50b747752c8d941b1a7e031ac2fec0236e7edd2", "unused");
            if (sameSize)
            {
                bytes[10] ^= 1;
            }
            await File.WriteAllBytesAsync(Path.Combine(directory, expected.Name), bytes);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelFiles.VerifyAsync(directory, [expected]));
            Assert.Contains(expected.Name, error.Message);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_ExactEvaluationSeed_AcceptsFrozenBytesReadOnly()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Evaluation");
        var length = new FileInfo(Path.Combine(directory, "pt-BR.seed.json")).Length;
        await ModelFiles.VerifyAsync(directory, [new("fixture", "pt-BR.seed.json", length,
            "26914d9e2017f2cdf92ab9fea50b747752c8d941b1a7e031ac2fec0236e7edd2", "unused")]);
    }

    [Fact]
    public async Task VerifyAsync_AlreadyCancelled_DoesNotStartFileInspection()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelFiles.VerifyAsync(AppContext.BaseDirectory,
            [new("generation", "not-downloaded.gguf", 100, new string('a', 64), "unused")], cancelled.Token));
    }
}
