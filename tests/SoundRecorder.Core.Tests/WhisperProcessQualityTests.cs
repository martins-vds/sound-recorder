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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscribeAsync_PreCanceledRequests_DoNotResolveModelsOrStartWorkers(bool jobEntrypoint)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var models = new RoutingModels();
        var starts = 0;
        var transcriber = new WhisperProcessTranscriber("worker.exe", models, _ =>
        {
            starts++;
            return new FakeWorker("{\"Segments\":[]}");
        });
        var recording = Recording();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobEntrypoint
            ? transcriber.TranscribeAsync(new(Guid.NewGuid(), recording, "base-original", null), null, cancellation.Token)
            : transcriber.TranscribeAsync(recording, cancellation.Token));

        Assert.Equal(0, starts);
        Assert.Equal(0, models.PinnedLookups);
        Assert.Equal(0, models.SelectedLookups);
    }

    [Fact]
    public async Task TranscribeAsync_TypedCurrentWorkerError_IsActionableAndDoesNotExposeForeignAttemptErrors()
    {
        var id = Guid.NewGuid();
        var worker = new FakeWorker(
            JsonSerializer.Serialize(new { Kind = "Error", JobId = Guid.NewGuid(), Error = "Foreign attempt failed." }) + "\n" +
            JsonSerializer.Serialize(new { Kind = "Error", JobId = id, Error = "Current model inference failed." }));
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transcriber.TranscribeAsync(new(id, Recording(), null, "pinned.bin"), null, default));

        Assert.Equal("Current model inference failed.", error.Message);
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunWorkerAsync_AwaitingPhaseDelivery_PreventsStartAndFinalFramesFromOvertakingProgress(bool fail)
    {
        var id = Guid.NewGuid();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inferenceFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new BufferedOutput
        {
            BeforeWrite = async line =>
            {
                if (line?.Contains("\"Kind\":\"Progress\"", StringComparison.Ordinal) != true) return;
                held.TrySetResult();
                await release.Task;
            },
        };
        using var input = new StringReader(JsonSerializer.Serialize(new
        {
            JobId = id, ModelPath = "pinned.bin", Recording = Recording(),
            ProcessingAudioPath = Path.Combine(_directory, "delayed-phase.wav"),
        }));
        var run = WhisperProcessTranscriber.RunWorkerAsync(input, output, (models, logger) =>
            new WhisperLocalTranscriber(models, logger, (_, _, _, _) =>
            {
                inferenceFinished.TrySetResult();
                return fail
                    ? Task.FromException<IReadOnlyList<TranscriptSegment>>(new IOException("Controlled inference failed."))
                    : Task.FromResult<IReadOnlyList<TranscriptSegment>>([]);
            }));
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await inferenceFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(run, await Task.WhenAny(run, Task.Delay(100)));
            Assert.Equal(string.Empty, output.ToString());
            release.TrySetResult();
            Assert.Equal(fail ? 1 : 0, await run);
            var frames = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            using var first = JsonDocument.Parse(frames[0]);
            using var final = JsonDocument.Parse(frames[^1]);
            Assert.Equal("Progress", first.RootElement.GetProperty("Kind").GetString());
            Assert.Equal(fail ? "Error" : "Result", final.RootElement.GetProperty("Kind").GetString());
        }
        finally { release.TrySetResult(); await run; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TranscribeAsync_CancellationArrivesWithAWorkerFrame_DiscardsEvenMalformedFrames(bool malformed)
    {
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var reads = 0;
        var worker = new FakeWorker(string.Empty)
        {
            Reader = new FailingReader(() =>
            {
                if (reads++ != 0) return null;
                cancellation.Cancel();
                return malformed ? "{\"Kind\":\"Progress\"" : JsonSerializer.Serialize(new
                {
                    Kind = "Progress", JobId = id, Progress = new TranscriptionProgress(TranscriptionPhase.Recognizing, 37),
                });
            }),
        };
        var reported = new List<TranscriptionProgress>();
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(new(id, Recording(), null, "pinned.bin"),
                new TranscriptionProgressProtocolTests.Reporter(value => reported.Add(value)), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(reported);
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunWorkerAsync_TypedJob_StreamsFlushedCorrelatedPhasesBeforeItsResultOrError(bool fail)
    {
        var id = Guid.NewGuid();
        var recording = Recording();
        var processing = Path.Combine(_directory, "typed-processing.wav");
        var model = Path.Combine(_directory, "pinned-model.bin");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var input = new StringReader(JsonSerializer.Serialize(new
        {
            JobId = id, ModelPath = model, Recording = recording, ProcessingAudioPath = processing,
        }));
        using var output = new BufferedOutput();
        var run = WhisperProcessTranscriber.RunWorkerAsync(input, output, (models, logger) =>
            new WhisperLocalTranscriber(models, logger, async (path, _, _, _) =>
            {
                Assert.Equal(model, path);
                entered.TrySetResult();
                await finish.Task;
                if (fail) throw new IOException("Controlled inference failed.");
                return [new(TimeSpan.Zero, TimeSpan.FromSeconds(.5), "typed result")];
            }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await TranscriptionJobManagementTests.WaitUntilAsync(() =>
                output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Count(line => line.Contains("\"Kind\":\"Progress\"", StringComparison.Ordinal)) == 2);
            Assert.False(run.IsCompleted);
            Assert.DoesNotContain("\"Kind\":\"Result\"", output.ToString());
            finish.TrySetResult();
            Assert.Equal(fail ? 1 : 0, await run);

            var frames = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.All(frames, frame => Assert.Equal(id, frame.RootElement.GetProperty("JobId").GetGuid()));
                Assert.Single(frames, frame => frame.RootElement.GetProperty("Kind").GetString() == "Started");
                var phases = frames.Where(frame => frame.RootElement.GetProperty("Kind").GetString() == "Progress")
                    .Select(frame => frame.RootElement.GetProperty("Progress")).ToArray();
                Assert.Equal(new[] { (int)TranscriptionPhase.Preparing, (int)TranscriptionPhase.Recognizing },
                    phases.Select(phase => phase.GetProperty("Phase").GetInt32()));
                Assert.All(phases, phase => Assert.Equal(JsonValueKind.Null, phase.GetProperty("Percent").ValueKind));
                var final = frames[^1].RootElement;
                Assert.Equal(fail ? "Error" : "Result", final.GetProperty("Kind").GetString());
                if (fail) Assert.Contains("Controlled inference failed", final.GetProperty("Error").GetString());
                else Assert.Equal("typed result", final.GetProperty("Segments")[0].GetProperty("Text").GetString());
                Assert.False(File.Exists(processing));
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        finally { finish.TrySetResult(); await run; }
    }

    [Theory]
    [InlineData("Progress", false, null, null, "Progress frame has no progress.")]
    [InlineData("Progress", false, -1, 1, "Invalid worker progress.")]
    [InlineData("Progress", false, 101, 1, "Invalid worker progress.")]
    [InlineData("Progress", false, 37, 99, "Invalid worker progress.")]
    [InlineData("Unknown", false, null, null, "Unknown local transcription worker frame.")]
    [InlineData("Result", true, null, null, "A typed worker frame must identify its job.")]
    public async Task TranscribeAsync_InvalidTypedFrame_RejectsMalformedWorkerState(
        string kind, bool missingIdentity, int? percent, int? phase, string expectedError)
    {
        var id = Guid.NewGuid();
        var frame = JsonSerializer.Serialize(new
        {
            Kind = kind,
            JobId = missingIdentity ? Guid.Empty : id,
            Progress = phase is null ? null : new { Phase = phase.Value, Percent = percent },
            Segments = Array.Empty<TranscriptSegment>(),
        });
        var worker = new FakeWorker(frame + "\n");
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            transcriber.TranscribeAsync(new(id, Recording(), null, "pinned.bin"), null, default));

        Assert.Equal(expectedError, error.Message);
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData(true, "explicit.bin", "explicit.bin")]
    [InlineData(true, null, "base-original.bin")]
    [InlineData(false, null, "selected.bin")]
    public async Task TranscribeAsync_PinnedAndLegacyEntrypoints_ResolveOnlyTheirIntendedModel(
        bool jobEntrypoint, string? explicitPath, string expectedPath)
    {
        var worker = new FakeWorker("{\"Segments\":[]}");
        var models = new RoutingModels();
        var transcriber = new WhisperProcessTranscriber("worker.exe", models, _ => worker);
        var recording = Recording();
        if (jobEntrypoint)
            await transcriber.TranscribeAsync(new(Guid.NewGuid(), recording, "base-original", explicitPath), null, default);
        else
            await transcriber.TranscribeAsync(recording, default);

        using var request = JsonDocument.Parse(worker.Request.ToString());
        Assert.Equal(expectedPath, request.RootElement.GetProperty("ModelPath").GetString());
        Assert.Equal(explicitPath is null && jobEntrypoint ? 1 : 0, models.PinnedLookups);
        Assert.Equal(jobEntrypoint ? 0 : 1, models.SelectedLookups);
    }

    [Fact]
    public async Task TranscribeAsync_CorrelatedIndeterminateAndFinalRecognitionEstimates_AreNotResults()
    {
        var id = Guid.NewGuid();
        var frames = string.Join("\n", new[]
        {
            JsonSerializer.Serialize(new { Kind = "Started", JobId = id }),
            JsonSerializer.Serialize(new { Kind = "Progress", JobId = id, Progress = new TranscriptionProgress(TranscriptionPhase.Preparing) }),
            JsonSerializer.Serialize(new { Kind = "Progress", JobId = id, Progress = new TranscriptionProgress(TranscriptionPhase.Recognizing) }),
            JsonSerializer.Serialize(new { Kind = "Progress", JobId = id, Progress = new TranscriptionProgress(TranscriptionPhase.Recognizing, 100) }),
            JsonSerializer.Serialize(new { Kind = "Result", JobId = Guid.NewGuid(), Segments = new[] { new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "stale") } }),
            JsonSerializer.Serialize(new { Kind = "Result", JobId = id, Segments = new[] { new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "current") } }),
        });
        var worker = new FakeWorker(frames);
        var seen = new List<TranscriptionProgress>();
        var transcriber = new WhisperProcessTranscriber("worker.exe", new ModelStore(), _ => worker);

        var document = await transcriber.TranscribeAsync(new(id, Recording(), null, "pinned.bin"),
            new TranscriptionProgressProtocolTests.Reporter(value => seen.Add(value)), default);

        Assert.Equal(new TranscriptionProgress[]
        {
            new(TranscriptionPhase.Preparing),
            new(TranscriptionPhase.Recognizing),
            new(TranscriptionPhase.Recognizing, 100),
        }, seen);
        Assert.Equal("current", Assert.Single(document.Segments).Text);
    }

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

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(Recording(), cancellation.Token));

        Assert.Null(error.InnerException);
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

    private sealed class RoutingModels : ITranscriptionModelStore
    {
        public int SelectedLookups { get; private set; }
        public int PinnedLookups { get; private set; }
        public bool HasCompatibleModel(string language) => true;
        public string? GetModelPath(string language) { SelectedLookups++; return "selected.bin"; }
        public string? GetModelPath(string language, string? modelId)
        {
            PinnedLookups++;
            Assert.Equal("base-original", modelId);
            return "base-original.bin";
        }
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
        private readonly object _gate = new();
        private readonly StringBuilder _pending = new();
        private readonly StringBuilder _visible = new();
        public Func<string?, Task>? BeforeWrite { get; init; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) { lock (_gate) _pending.AppendLine(value); }
        public override async Task WriteLineAsync(string? value)
        {
            if (BeforeWrite is not null) await BeforeWrite(value);
            WriteLine(value);
        }

        public override void Flush()
        {
            lock (_gate)
            {
                _visible.Append(_pending);
                _pending.Clear();
            }
        }

        public override Task FlushAsync()
        {
            Flush();
            return Task.CompletedTask;
        }

        public override string ToString() { lock (_gate) return _visible.ToString(); }
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
