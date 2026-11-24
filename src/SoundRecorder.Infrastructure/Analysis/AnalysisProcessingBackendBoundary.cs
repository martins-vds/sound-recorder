using SoundRecorder.Analysis;

namespace SoundRecorder.Infrastructure.Analysis;

public sealed partial class AnalysisProcessingService
{
    private sealed class GenerationBoundary(IGenerationBackend backend,
        Func<CancellationToken, Task<IDisposable>>? enter) : IGenerationBackend
    {
        public string RenderPrompt(string instruction, string input) => backend.RenderPrompt(instruction, input);
        public int CountTokens(string prompt) => backend.CountTokens(prompt);

        public async Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            using var lease = enter is null ? null : await enter(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await backend.GenerateAsync(prompt, outputTokens, cancellationToken);
        }
    }
}
