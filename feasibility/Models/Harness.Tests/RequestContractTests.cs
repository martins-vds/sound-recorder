using System.Text.Json;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

public sealed class RequestContractTests
{
    [Theory]
    [InlineData(false, "en", "extract")]
    [InlineData(true, "pt-BR", "answer")]
    public async Task GenerateAsync_SelectedTask_TransportsLanguageQuestionAndMachineReadableResponseSchema(
        bool question, string language, string task)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new ContractBackend(task, language, question ? "Quem verifica?" : null);
            var probe = new GenerationProbe(backend);
            var identity = new TranscriptIdentity("attempt", new string('a', 64));
            var path = Path.Combine(directory, "claims.sqlite");
            var result = question
                ? await probe.AnswerAsync(identity, Source(), path, "Quem verifica?", language, TimeSpan.FromSeconds(5))
                : await probe.ExtractAsync(identity, Source(), path, language, TimeSpan.FromSeconds(5));
            Assert.Equal(AnalysisStatus.Completed, result.Status);
            Assert.Equal(1, backend.Calls);
            static async IAsyncEnumerable<ExcerptInput> Source()
            {
                await Task.Yield();
                yield return new("s000001", null, null, new StringReader("Ana verifies access."));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ContractBackend(string task, string language, string? question) : IGenerationBackend
    {
        public int Calls { get; private set; }
        public string RenderPrompt(string instruction, string input) => input;
        public int CountTokens(string prompt) => 100;
        public Task<GenerationResult> GenerateAsync(string prompt, int outputTokens, CancellationToken cancellationToken)
        {
            using var request = JsonDocument.Parse(prompt);
            var root = request.RootElement;
            Assert.Equal(task, root.GetProperty("task").GetString());
            Assert.Equal(language, root.GetProperty("language").GetString());
            Assert.Equal(question, root.GetProperty("question").GetString());
            var schema = root.GetProperty("outputSchema");
            var claim = schema.GetProperty("properties").GetProperty("claims").GetProperty("items");
            Assert.Contains("owner", claim.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
            Assert.Contains("deadline", claim.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
            Assert.Equal(512, claim.GetProperty("properties").GetProperty("statement").GetProperty("maxLength").GetInt32());
            Assert.Equal("Ana verifies access.", root.GetProperty("excerpts")[0].GetProperty("Quote").GetString());
            Calls++;
            return Task.FromResult(new GenerationResult(
                """{"claims":[{"kind":"Report","statement":"Ana verifies access.","owner":{"state":"Absent","value":null},"deadline":{"state":"Absent","value":null},"qualifiers":[],"references":[0]}],"limitation":null}""",
                GenerationFinish.Completed, 50));
        }
    }
}
