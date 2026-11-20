using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.ProcessTestHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var transcriber = new WhisperProcessTranscriber(
            args[0],
            new ModelStore(args[1]),
            new ReadyLogger(args[3]));
        var recording = new SavedRecording(
            Guid.NewGuid(), args[2], "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await transcriber.TranscribeAsync(recording, CancellationToken.None);
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
