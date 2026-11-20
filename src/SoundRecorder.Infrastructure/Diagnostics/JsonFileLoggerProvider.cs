using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SoundRecorder.Infrastructure.Diagnostics;

public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _filePath;

    public JsonFileLoggerProvider(string logDirectory)
    {
        _filePath = Path.Combine(
            logDirectory,
            $"sound-recorder-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
        try
        {
            Directory.CreateDirectory(logDirectory);
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public string FilePath => _filePath;

    public bool IsAvailable { get; private set; }

    public ILogger CreateLogger(string categoryName)
    {
        return new JsonFileLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    private void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (!IsAvailable)
        {
            return;
        }

        var entry = new LogEntry(
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            category,
            level.ToString(),
            eventId.Id,
            eventId.Name,
            message,
            exception?.GetType().FullName,
            exception?.StackTrace,
            properties);
        try
        {
            lock (_gate)
            {
                File.AppendAllText(
                    _filePath,
                    JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }

    private sealed class JsonFileLogger(
        JsonFileLoggerProvider provider,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                ExtractProperties(state));
        }

        private static IReadOnlyDictionary<string, object?>? ExtractProperties<TState>(
            TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                return null;
            }

            return values
                .Where(pair => pair.Key != "{OriginalFormat}")
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }

    private sealed record LogEntry(
        DateTimeOffset Timestamp,
        int ProcessId,
        int ThreadId,
        string Category,
        string Level,
        int EventId,
        string? EventName,
        string Message,
        string? ExceptionType,
        string? StackTrace,
        IReadOnlyDictionary<string, object?>? Properties);
}
