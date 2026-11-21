using System.Text.Json;
using Microsoft.Extensions.Logging;
using SoundRecorder.Infrastructure.Diagnostics;

namespace SoundRecorder.Core.Tests;

public sealed class DiagnosticsStorageTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();

    [Fact]
    public void Log_StructuredException_PersistsMetadataButNotOriginalTemplate()
    {
        using var provider = new JsonFileLoggerProvider(_directory.PathFor("logs"));
        Assert.True(provider.IsAvailable);
        var logger = provider.CreateLogger("Storage.Tests");
        Assert.Null(logger.BeginScope("ignored scope"));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        logger.LogDebug("not written");
        Exception exception;
        try
        {
            throw new IOException("private exception message");
        }
        catch (IOException caught)
        {
            exception = caught;
        }

        var before = DateTimeOffset.UtcNow;
        logger.LogWarning(new EventId(234, "StorageEvent"), exception, "Saved {Count} records", 17);
        var after = DateTimeOffset.UtcNow;
        var line = Assert.Single(File.ReadAllLines(provider.FilePath));
        using var json = JsonDocument.Parse(line);
        var entry = json.RootElement;
        Assert.InRange(entry.GetProperty("Timestamp").GetDateTimeOffset(), before, after);
        Assert.Equal(Environment.ProcessId, entry.GetProperty("ProcessId").GetInt32());
        Assert.Equal(Environment.CurrentManagedThreadId, entry.GetProperty("ThreadId").GetInt32());
        Assert.Equal("Storage.Tests", entry.GetProperty("Category").GetString());
        Assert.Equal("Warning", entry.GetProperty("Level").GetString());
        Assert.Equal(234, entry.GetProperty("EventId").GetInt32());
        Assert.Equal("StorageEvent", entry.GetProperty("EventName").GetString());
        Assert.Equal("Saved 17 records", entry.GetProperty("Message").GetString());
        Assert.Equal(typeof(IOException).FullName, entry.GetProperty("ExceptionType").GetString());
        Assert.Contains(nameof(Log_StructuredException_PersistsMetadataButNotOriginalTemplate),
            entry.GetProperty("StackTrace").GetString());
        Assert.Equal(17, entry.GetProperty("Properties").GetProperty("Count").GetInt32());
        Assert.False(entry.GetProperty("Properties").TryGetProperty("{OriginalFormat}", out _));
        Assert.DoesNotContain("private exception message", line);
    }

    [Fact]
    public void Log_UnstructuredStateAndNoException_WritesNullOptionalMetadata()
    {
        using var provider = new JsonFileLoggerProvider(_directory.Root);
        var logger = provider.CreateLogger("plain");
        logger.Log(LogLevel.Information, new EventId(3), "state", null, (state, _) => state);
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(provider.FilePath)));
        Assert.Equal("state", json.RootElement.GetProperty("Message").GetString());
        foreach (var name in new[] { "Properties", "ExceptionType", "StackTrace", "EventName" })
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(name).ValueKind);
        }
    }

    [Fact]
    public void Log_UnavailableDirectory_DoesNotAttemptFormattingOrWriting()
    {
        var path = _directory.PathFor("file");
        File.WriteAllText(path, "keep");
        using var provider = new JsonFileLoggerProvider(path);
        Assert.False(provider.IsAvailable);
        provider.CreateLogger("unavailable").LogInformation("ignored");
        Assert.False(provider.IsAvailable);
        Assert.Equal("keep", File.ReadAllText(path));
        File.Delete(path);
        Directory.CreateDirectory(path);
        provider.CreateLogger("unavailable").LogInformation("not retried after storage returns");
        Assert.False(File.Exists(provider.FilePath));
    }

    [Fact]
    public void Log_WriteFailure_DisablesFurtherWritesWithoutThrowing()
    {
        using var provider = new JsonFileLoggerProvider(_directory.Root);
        Directory.CreateDirectory(provider.FilePath);
        var logger = provider.CreateLogger("broken");
        logger.LogInformation("fails");
        Assert.False(provider.IsAvailable);
        Directory.Delete(provider.FilePath);
        logger.LogInformation("not retried");
        Assert.False(File.Exists(provider.FilePath));
    }

    [Fact]
    public void Log_BelowInformation_DoesNotInvokeFormatter()
    {
        using var provider = new JsonFileLoggerProvider(_directory.Root);
        provider.CreateLogger("filtered").Log(LogLevel.Debug, default, "state", null,
            (_, _) => throw new InvalidOperationException("must not format"));
        Assert.False(File.Exists(provider.FilePath));
    }

    public void Dispose() => _directory.Dispose();
}
