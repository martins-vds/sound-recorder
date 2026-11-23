using System.Text.Json;
using System.Text;
using System.Diagnostics;
using LLama.Common;
using Models.Harness;
using Xunit;

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
[Trait("purpose", "NativeCrashReplay")]
public sealed class NativeCrashReplayTests
{
    [Fact]
    public async Task GenerateAsync_CapturedPortugueseSixthCall_CompletesExactFrozenRequest()
    {
        using var captured = JsonDocument.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("SR_CRASH_REPLAY_INPUT")
                ?? throw new InvalidOperationException("Select the locally captured exact replay input.")));
        var root = Environment.GetEnvironmentVariable("SR_CRASH_REPLAY_OUTPUT")
            ?? throw new InvalidOperationException("Select the owned local replay report directory.");
        Directory.CreateDirectory(root);
        var inputs = captured.RootElement.ValueKind == JsonValueKind.Array
            ? captured.RootElement.EnumerateArray().ToArray()
            : [captured.RootElement];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var cache = Environment.GetEnvironmentVariable("SR_APPROVED_MODEL_DIRECTORY")
            ?? throw new InvalidOperationException("Use the already approved cache.");
        var files = ModelFiles.PhiMiniFiles();
        await ModelFiles.VerifyAsync(cache, files, deadline.Token);
        using var configuration = JsonDocument.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("SR_CRASH_REPLAY_CONFIG")
                ?? throw new InvalidOperationException("Select the exact frozen native configuration.")));
        var settings = configuration.RootElement;
        var context = new NativeContextOptions(Path.Combine(cache, files.Single().Name))
        {
            ContextSize = settings.GetProperty("ContextSize").GetUInt32(),
            Threads = settings.GetProperty("Threads").GetInt32(),
            BatchThreads = settings.GetProperty("BatchThreads").GetInt32(),
            BatchSize = settings.GetProperty("BatchSize").GetUInt32(),
            UBatchSize = settings.GetProperty("UBatchSize").GetUInt32(),
            GpuLayerCount = 0,
            NoKqvOffload = true,
            OpOffload = false
        };
        using var generation = await LlamaNativeModel.LoadAsync(context, deadline.Token);
        using var observer = Environment.GetEnvironmentVariable("SR_CRASH_REPLAY_OBSERVER") == "1"
            ? new Timer(_ =>
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                File.AppendAllText(Path.Combine(root, "memory.jsonl"),
                    JsonSerializer.Serialize(new
                    {
                        utc = DateTime.UtcNow,
                        process.WorkingSet64,
                        process.PeakWorkingSet64,
                        process.PrivateMemorySize64
                    }) + "\n");
            }, null, 100, 100)
            : null;
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            var prompt = input.GetProperty("prompt").GetString()!;
            var budget = input.GetProperty("outputTokens").GetInt32();
            Assert.Equal(input.GetProperty("promptTokens").GetInt32(), generation.CountTokens(prompt, true, true, Encoding.UTF8));
            File.AppendAllText(Path.Combine(root, "replay.jsonl"),
                JsonSerializer.Serialize(new { phase = "before", index, budget, utc = DateTime.UtcNow }) + "\n");
            var result = await generation.InferAsync(prompt, context,
                new(budget, settings.GetProperty("Grammar").GetString()!, settings.GetProperty("Seed").GetUInt32(),
                    settings.GetProperty("Temperature").GetSingle(), ContextOverflowStrategy.ThrowException), deadline.Token);
            File.AppendAllText(Path.Combine(root, "replay.jsonl"),
                JsonSerializer.Serialize(new { phase = "after", index, result, utc = DateTime.UtcNow }) + "\n");
            Assert.Equal(GenerationFinish.Completed, result.Finish);
            using var output = JsonDocument.Parse(result.Text);
            Assert.NotEmpty(output.RootElement.GetProperty("claims").EnumerateArray());
            Assert.All(output.RootElement.GetProperty("claims").EnumerateArray(),
                claim => Assert.Equal(0, claim.GetProperty("references")[0].GetInt32()));
        }
    }
}
