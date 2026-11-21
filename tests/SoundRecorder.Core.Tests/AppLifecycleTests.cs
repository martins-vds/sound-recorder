using System.Windows;
using Microsoft.Extensions.Logging;
using SoundRecorder.Desktop;

namespace SoundRecorder.Core.Tests;

public sealed class AppLifecycleTests
{
    [Theory]
    [InlineData(true, new[] { "--transcribe-worker" })]
    [InlineData(false, new string[0])]
    [InlineData(false, new[] { "--TRANSCRIBE-WORKER" })]
    [InlineData(false, new[] { "--transcribe-worker", "extra" })]
    [InlineData(false, new[] { "other" })]
    public void IsWorkerInvocation_Arguments_RequiresExactWorkerInvocation(bool expected, string[] arguments)
    {
        Assert.Equal(expected, ApplicationLifecycle.IsWorkerInvocation(arguments));
    }

    [Fact]
    public async Task RunWorkerAsync_ExplicitWorker_UsesStandardStreamsAndPropagatesExitCodeOnlyAfterCompletion()
    {
        var exits = new List<int>();
        var configured = false;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        TextReader? input = null;
        TextWriter? output = null;
        var lifecycle = new ApplicationLifecycle(
            new DesktopDialogs(), exits.Add,
            (reader, writer) => { input = reader; output = writer; return gate.Task; },
            () => { configured = true; throw new InvalidOperationException(); });
        var running = lifecycle.RunWorkerAsync();
        Assert.Same(Console.In, input);
        Assert.Same(Console.Out, output);
        Assert.Empty(exits);
        Assert.False(configured);
        Assert.Null(lifecycle.LoggerFactory);
        Assert.Null(lifecycle.LogFilePath);
        gate.SetResult(17);
        await running;
        Assert.Equal(17, Assert.Single(exits));
    }

    [Fact]
    public async Task RunWorkerAsync_Failure_DoesNotReportSuccessOrInitializeDesktop()
    {
        var exits = new List<int>();
        var error = new IOException("worker failed");
        var lifecycle = new ApplicationLifecycle(new DesktopDialogs(), exits.Add,
            (_, _) => Task.FromException<int>(error), () => throw new InvalidOperationException());
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(lifecycle.RunWorkerAsync));
        Assert.Empty(exits);
    }

    [Fact]
    public void InitializeDesktop_Diagnostics_RecordsStartupFailuresAndExitWithoutConversationContent()
    {
        var factory = new AppLoggerFactory();
        var dialogs = new DesktopDialogs();
        var exits = new List<int>();
        var lifecycle = new ApplicationLifecycle(dialogs, exits.Add,
            (_, _) => throw new InvalidOperationException("not a worker"), () => (factory, "local-diagnostics.jsonl"));
        lifecycle.InitializeDesktop();
        Assert.Same(factory, lifecycle.LoggerFactory);
        Assert.Equal("local-diagnostics.jsonl", lifecycle.LogFilePath);
        Assert.Equal(typeof(App).FullName, factory.Category);
        Assert.Equal((LogLevel.Information, 100, "Application starting"), Assert.Single(factory.Logger.Entries));
        var dispatcherError = new IOException("dispatcher failed");
        lifecycle.LogDispatcherException(dispatcherError);
        Assert.Same(dispatcherError, factory.Logger.Exceptions.Last());
        Assert.Equal((LogLevel.Critical, 900, "Unhandled dispatcher exception"),
            factory.Logger.Entries.Last());
        lifecycle.ShutdownAfterDispatcherException();
        Assert.Equal(("Sound Recorder encountered an unexpected error.\n\nDiagnostics: local-diagnostics.jsonl",
            "Sound Recorder", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None),
            Assert.Single(dialogs.Prompts));
        Assert.Equal(1, Assert.Single(exits));
        var domainError = new ArgumentException("process failed");
        lifecycle.ReportDomainException(domainError);
        Assert.Same(domainError, factory.Logger.Exceptions.Last());
        Assert.Equal((LogLevel.Critical, 901, "Unhandled process exception"), factory.Logger.Entries.Last());
        var count = factory.Logger.Entries.Count;
        lifecycle.ReportDomainException("non-exception payload");
        Assert.Equal(count, factory.Logger.Entries.Count);
        var aggregate = new AggregateException("background failed", new IOException());
        var args = new UnobservedTaskExceptionEventArgs(aggregate);
        lifecycle.ObserveTaskException(args);
        Assert.True(args.Observed);
        Assert.Same(aggregate, factory.Logger.Exceptions.Last());
        Assert.Equal((LogLevel.Error, 902, "Unobserved task exception"), factory.Logger.Entries.Last());
        lifecycle.CompleteExit(23);
        Assert.Equal((LogLevel.Information, 101, "Application exited with code 23"),
            factory.Logger.Entries.Last());
        Assert.Equal(1, factory.Disposals);
    }

    [Fact]
    public void ExceptionHandling_BeforeLoggingExists_StillObservesTasksAndOffersDiagnostics()
    {
        var dialogs = new DesktopDialogs();
        var exits = new List<int>();
        var lifecycle = new ApplicationLifecycle(dialogs, exits.Add,
            (_, _) => Task.FromResult(0), () => throw new InvalidOperationException());
        lifecycle.LogDispatcherException(new Exception());
        lifecycle.ReportDomainException(new Exception());
        lifecycle.ReportDomainException(new object());
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException());
        lifecycle.ObserveTaskException(args);
        Assert.True(args.Observed);
        lifecycle.CompleteExit(7);
        lifecycle.ShutdownAfterDispatcherException();
        Assert.Equal("Sound Recorder encountered an unexpected error.\n\nDiagnostics: ",
            Assert.Single(dialogs.Prompts).Message);
        Assert.Equal(1, Assert.Single(exits));
    }
}

internal sealed class AppLoggerFactory : ILoggerFactory
{
    public AppLogger Logger { get; } = new();
    public string? Category { get; private set; }
    public int Disposals { get; private set; }
    public ILogger CreateLogger(string categoryName) { Category = categoryName; return Logger; }
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() => Disposals++;
}

internal sealed class AppLogger : ILogger
{
    public List<(LogLevel Level, int Id, string Message)> Entries { get; } = [];
    public List<Exception?> Exceptions { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
        Exceptions.Add(exception);
    }
}
