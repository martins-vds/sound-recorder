using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.ProcessTestHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["--hold"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        if (args is ["--transcribe-worker"])
        {
            await RunProtocolFixtureAsync();
            return;
        }

        var transcriber = new WhisperProcessTranscriber(
            args[0],
            new ModelStore(args[1]),
            new ReadyLogger(args[3]));
        var recording = new SavedRecording(
            Guid.NewGuid(), args[2], "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await transcriber.TranscribeAsync(recording, CancellationToken.None);
    }

    private static async Task RunProtocolFixtureAsync()
    {
        using var request = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
        var mode = request.RootElement.GetProperty("ModelPath").GetString();
        Console.WriteLine("{\"Started\":true}");
        switch (mode)
        {
            case "error":
                Console.WriteLine("{\"Error\":\"fixture inference failed\"}");
                Environment.ExitCode = 7;
                break;
            case "exit":
                Environment.ExitCode = 9;
                break;
            case "null":
                Console.WriteLine("null");
                break;
            case "missing":
                Console.WriteLine("{\"Error\":null}");
                break;
            case "malformed":
                Console.WriteLine("not-json");
                break;
            case "hang":
                await Task.Delay(Timeout.InfiniteTimeSpan);
                break;
            case "tree":
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                       {
                           UseShellExecute = false,
                           CreateNoWindow = true,
                           ArgumentList = { "--hold" },
                       })!)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { Grandchild = child.Id }));
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                break;
            case "slow-exit":
                Console.WriteLine("{\"Segments\":[{\"Start\":\"00:00:00\",\"End\":\"00:00:00.5000000\",\"Text\":\"hello\"}]}");
                await Task.Delay(200);
                break;
            default:
                Console.WriteLine("{\"Segments\":[{\"Start\":\"00:00:00\",\"End\":\"00:00:00.5000000\",\"Text\":\"hello\"}]}");
                break;
        }
    }

    public sealed record ReadyState(int ParentProcessId, int WorkerProcessId);

    private sealed class ModelStore(string path) : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => File.Exists(path);

        public string? GetModelPath(string language) => path;
    }

    private sealed class ReadyLogger(string markerPath) : ILogger<WhisperProcessTranscriber>
    {
        private int _workerProcessId;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 702 &&
                state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                _workerProcessId = (int)properties.Single(item => item.Key == "WorkerProcessId").Value!;
            }

            if (eventId.Id == 700)
            {
                var ready = new ReadyState(Environment.ProcessId, _workerProcessId);
                File.WriteAllText($"{markerPath}.tmp", JsonSerializer.Serialize(ready));
                File.Move($"{markerPath}.tmp", markerPath);
            }
        }
    }
}
