using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundRecorder.Infrastructure.Diagnostics;

namespace SoundRecorder.Core.Tests;

public sealed class LocalAppDiagnosticsTests
{
    [Fact]
    public void DiagnosticsWriteStructuredStartupAndFailureEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var provider = new JsonFileLoggerProvider(directory);
        var logger = provider.CreateLogger("SoundRecorder.Tests");

        logger.LogInformation(
            new EventId(100, "MainWindowReady"),
            "main-window-ready {Component}",
            "Desktop");
        logger.LogError(
            new EventId(101, "MainWindowLoadFailed"),
            new InvalidOperationException("Initialization failed."),
            "main-window-load-failed");

        var entries = File.ReadAllLines(provider.FilePath)
            .Select(line => JsonSerializer.Deserialize<DiagnosticEntry>(line)!)
            .ToArray();
        Assert.Equal(
            ["main-window-ready Desktop", "main-window-load-failed"],
            entries.Select(entry => entry.Message));
        Assert.Equal("SoundRecorder.Tests", entries[0].Category);
        Assert.Equal("Information", entries[0].Level);
        Assert.Equal(
            "Desktop",
            entries[0].Properties!["Component"]?.ToString());
        Assert.Equal(typeof(InvalidOperationException).FullName, entries[1].ExceptionType);
        Assert.DoesNotContain("Initialization failed.", File.ReadAllText(provider.FilePath));
    }

    [Fact]
    public void DiagnosticsDoNotFailWhenTheLogDirectoryIsUnavailable()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllText(filePath, "not-a-directory");
        var provider = new JsonFileLoggerProvider(Path.Combine(filePath, "logs"));
        var logger = provider.CreateLogger("SoundRecorder.Tests");

        logger.LogCritical(
            new EventId(900, "StartupFailure"),
            new IOException("The output path is unavailable."),
            "startup-failed");

        Assert.False(provider.IsAvailable);
    }

    private sealed record DiagnosticEntry(
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
