using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class WhisperProcessQualityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("recorder-process-tests-").FullName;

    [Fact]
    public async Task TranscribeAsync_WorkerProtocol_ReturnsSegmentsAndCleansOwnedDirectory()
    {
        var worker = new FakeWorker(
            "{\"Started\":true}\n{\"Started\":true}\n" +
            "{\"Segments\":[{\"Start\":\"00:00:00\",\"End\":\"00:00:00.5000000\",\"Text\":\"hello\"}]}\n");
        var logger = new RecordingLogger<WhisperProcessTranscriber>();
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), executable =>
        {
            Assert.Equal("worker.exe", executable);
            return worker;
        }, logger);
        var recording = Recording();

        var document = await transcriber.TranscribeAsync(recording, CancellationToken.None);

        Assert.Equal(recording, document.Recording);
        Assert.Equal(recording.Language, document.Language);
        Assert.Equal(
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), "hello"),
            Assert.Single(document.Segments));
        Assert.True(worker.Disposed);
        Assert.Equal(1, worker.WaitCount);
        Assert.Equal(2, logger.EventIds.Count(id => id == 700));
        Assert.Contains(702, logger.EventIds);
        Assert.Contains(701, logger.EventIds);
        using var request = JsonDocument.Parse(worker.Request.ToString());
        Assert.Equal("success", request.RootElement.GetProperty("ModelPath").GetString());
        Assert.Equal(recording.Id, request.RootElement.GetProperty("Recording").GetProperty("Id").GetGuid());
        var temporary = request.RootElement.GetProperty("ProcessingAudioPath").GetString()!;
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporary)));
    }

    [Theory]
    [InlineData(0, "native failed")]
    [InlineData(7, "native failed")]
    [InlineData(7, null)]
    public async Task TranscribeAsync_WorkerFailure_DoesNotReturnSuccessfulDocument(int exitCode, string? error)
    {
        var worker = new FakeWorker(JsonSerializer.Serialize(new { Error = error, Segments = Array.Empty<TranscriptSegment>() }))
        {
            ExitCode = exitCode,
        };
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transcriber.TranscribeAsync(Recording(), CancellationToken.None));

        Assert.Contains(error ?? "7", failure.Message);
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData("null", "invalid response")]
    [InlineData("{}", "no result")]
    public async Task TranscribeAsync_IncompleteResponse_ReportsInvalidData(string response, string message)
    {
        var worker = new FakeWorker(response);
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            transcriber.TranscribeAsync(Recording(), CancellationToken.None));

        Assert.Contains(message, error.Message);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task TranscribeAsync_NoResponse_ReportsExitCodeAndDisposesWorker()
    {
        var worker = new FakeWorker(string.Empty) { ExitCode = 13 };
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            transcriber.TranscribeAsync(Recording(), CancellationToken.None));

        Assert.Contains("13", error.Message);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task TranscribeAsync_CancellationBreaksPipe_StillReportsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var worker = new FakeWorker(string.Empty);
        worker.Reader = new FailingReader(() =>
        {
            cancellation.Cancel();
            throw new IOException("closed pipe");
        });
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(Recording(), cancellation.Token));

        Assert.Equal("Local transcription was canceled.", error.Message);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, worker.AbortCount);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task TranscribeAsync_BrokenPipeWithoutCancellation_PreservesFailure()
    {
        var failure = new IOException("broken worker pipe");
        var worker = new FakeWorker(string.Empty) { Reader = new FailingReader(() => throw failure) };
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            transcriber.TranscribeAsync(Recording(), CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task RunWorkerAsync_ValidRequest_UsesLocalModelAndEmitsOnlyProtocolMessages()
    {
        var recording = Recording();
        var model = Path.Combine(_directory, "model.bin");
        await File.WriteAllBytesAsync(model, [1]);
        var processing = Path.Combine(_directory, "processing.wav");
        using var input = new StringReader(JsonSerializer.Serialize(
            new { ModelPath = model, Recording = recording, ProcessingAudioPath = processing }));
        using var output = new BufferedOutput();
        var exit = await WhisperProcessTranscriber.RunWorkerAsync(input, output, (models, logger) =>
        {
            Assert.Equal(model, models.GetModelPath("English"));
            Assert.True(models.HasCompatibleModel("English"));
            File.Delete(model);
            Assert.False(models.HasCompatibleModel("English"));
            Assert.True(logger.IsEnabled(LogLevel.Information));
            Assert.Null(logger.BeginScope("inference"));
            logger.LogInformation(new EventId(800), "unrelated");
            return new WhisperLocalTranscriber(models, logger, (_, language, _, _) =>
            {
                Assert.Equal("en", language);
                using var ready = JsonDocument.Parse(output.ToString());
                Assert.True(ready.RootElement.GetProperty("Started").GetBoolean());
                return Task.FromResult<IReadOnlyList<TranscriptSegment>>(
                    [new(TimeSpan.Zero, TimeSpan.FromMilliseconds(500), " hello ")]);
            });
        });

        Assert.Equal(0, exit);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var started = JsonDocument.Parse(lines[0]);
        Assert.True(started.RootElement.GetProperty("Started").GetBoolean());
        using var result = JsonDocument.Parse(lines[1]);
        Assert.Equal("hello", result.RootElement.GetProperty("Segments")[0].GetProperty("Text").GetString());
        Assert.False(File.Exists(processing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task RunWorkerAsync_EmptyRequest_ReturnsAnExplicitFailure(string request)
    {
        using var input = new StringReader(request);
        using var output = new BufferedOutput();

        Assert.Equal(1, await WhisperProcessTranscriber.RunWorkerAsync(input, output));
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Contains("request", response.RootElement.GetProperty("Error").GetString());
    }

    [Fact]
    public async Task TranscribeAsync_CancellationArrivesAtWorkerExit_DoesNotPublishAResult()
    {
        using var cancellation = new CancellationTokenSource();
        var worker = new FakeWorker("{\"Segments\":[]}") { OnWait = cancellation.Cancel };
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(Recording(), cancellation.Token));

        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task TranscribeAsync_CancellationArrivesAtEmptyExit_PreservesCancellationInsteadOfIoFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var worker = new FakeWorker(string.Empty) { OnWait = cancellation.Cancel };
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(Recording(), cancellation.Token));

        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task TranscribeAsync_WorkerLeavesTemporaryAudio_RemovesItsOwnedFiles()
    {
        var worker = new FakeWorker(string.Empty);
        string? temporary = null;
        worker.Reader = new FailingReader(() =>
        {
            using var request = JsonDocument.Parse(worker.Request.ToString());
            temporary = request.RootElement.GetProperty("ProcessingAudioPath").GetString()!;
            File.WriteAllText(temporary, "processing copy");
            return "{\"Segments\":[]}";
        });
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        await transcriber.TranscribeAsync(Recording(), CancellationToken.None);

        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporary)));
    }

    [Fact]
    public async Task TranscribeAsync_NoCompatibleModel_DoesNotStartAProcess()
    {
        var transcriber = new WhisperProcessTranscriber("worker.exe", new MissingModel(), _ =>
            throw new InvalidOperationException("No model must not start a process"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transcriber.TranscribeAsync(Recording(), CancellationToken.None));

        Assert.Contains("English", error.Message);
    }

    [Theory]
    [Trait("type", "IntegrationTests")]
    [InlineData("success", typeof(TranscriptDocument))]
    [InlineData("slow-exit", typeof(TranscriptDocument))]
    [InlineData("error", typeof(InvalidOperationException))]
    [InlineData("exit", typeof(IOException))]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("missing", typeof(InvalidDataException))]
    [InlineData("malformed", typeof(JsonException))]
    public async Task TranscribeAsync_RealProcess_EnforcesProtocolAndExitStatus(string mode, Type expectedType)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe");
        var transcriber = new WhisperProcessTranscriber(executable, new ModelStore(mode));
        if (mode is "success" or "slow-exit")
        {
            var document = await transcriber.TranscribeAsync(Recording(), CancellationToken.None);
            Assert.Equal("hello", Assert.Single(document.Segments).Text);
        }
        else
        {
            var error = await Record.ExceptionAsync(() =>
                transcriber.TranscribeAsync(Recording(), CancellationToken.None));
            Assert.NotNull(error);
            Assert.IsType(expectedType, error);
            if (mode == "error")
            {
                Assert.Equal("fixture inference failed", error.Message);
            }
            else if (mode == "exit")
            {
                Assert.Contains("9", error.Message);
            }
        }
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task TranscribeAsync_CanceledRealProcess_ReleasesItsNativeLifetime()
    {
        var logger = new StartingLogger();
        var executable = Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe");
        var transcriber = new WhisperProcessTranscriber(executable, new ModelStore("hang"), logger);
        using var cancellation = new CancellationTokenSource();
        var inference = transcriber.TranscribeAsync(Recording(), cancellation.Token);
        await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inference);
    }

    [Fact]
    [Trait("type", "IntegrationTests")]
    public async Task DisposeAsync_RunningWorker_StopsItWithoutNeedingCancellation()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe");
        var worker = WindowsTranscriptionWorker.Start(executable);
        var disposed = false;
        try
        {
            await worker.Input.WriteLineAsync("{\"ModelPath\":\"hang\"}");
            worker.Input.Close();
            Assert.Equal("{\"Started\":true}", await worker.Output.ReadLineAsync());
            using var process = Process.GetProcessById(worker.Id);
            Assert.False(process.HasExited);

            await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            disposed = true;

            Assert.True(process.HasExited);
        }
        finally
        {
            if (!disposed)
            {
                await worker.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TranscribeAsync_MissingExecutable_PreservesNativeStartupError()
    {
        var transcriber = new WhisperProcessTranscriber(
            Path.Combine(_directory, "absent.exe"), new ModelStore());

        await Assert.ThrowsAsync<Win32Exception>(() =>
            transcriber.TranscribeAsync(Recording(), CancellationToken.None));
    }

    private SavedRecording Recording()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[96_000], 0, 96_000);
        }

        return new(Guid.NewGuid(), path, "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ModelStore(string path = "success") : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => true;
        public string? GetModelPath(string language) => path;
    }

    private sealed class MissingModel : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => false;
        public string? GetModelPath(string language) => null;
    }

    private sealed class FakeWorker(string output) : ITranscriptionWorker
    {
        public StringWriter Request { get; } = new();
        public TextReader Reader { get; set; } = new StringReader(output);
        public int Id => 47;
        public TextWriter Input => Request;
        public TextReader Output => Reader;
        public int ExitCode { get; set; }
        public int WaitCount { get; private set; }
        public int AbortCount { get; private set; }
        public bool Disposed { get; private set; }
        public Action? OnWait { get; set; }
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            OnWait?.Invoke();
            return Task.FromResult(ExitCode);
        }

        public void Abort() => AbortCount++;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingReader(Func<string?> read) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new(read());
    }

    private sealed class BufferedOutput : TextWriter
    {
        private readonly StringBuilder _pending = new();
        private readonly StringBuilder _visible = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => _pending.AppendLine(value);
        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override void Flush()
        {
            _visible.Append(_pending);
            _pending.Clear();
        }

        public override Task FlushAsync()
        {
            Flush();
            return Task.CompletedTask;
        }

        public override string ToString() => _visible.ToString();
    }

    private sealed class StartingLogger : ILogger<WhisperProcessTranscriber>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 700)
            {
                Started.TrySetResult();
            }
        }
    }
}
