using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class WhisperCancellationTests
{
    [LocalWhisperFact]
    [Trait("type", "IntegrationTests")]
    public async Task TranscribeAsync_CompletedWorker_ReturnsDocumentWithoutChangingAudio()
    {
        var directory = Directory.CreateTempSubdirectory("sound-recorder-worker-test-");
        try
        {
            var recording = CreateSilentRecording(directory.FullName);
            var original = await File.ReadAllBytesAsync(recording.AudioPath);
            var transcriber = new WhisperProcessTranscriber(
                Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_WORKER")!,
                new LocalModelStore());
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var document = await transcriber.TranscribeAsync(recording, timeout.Token);

            Assert.Equal(recording, document.Recording);
            Assert.Equal("English", document.Language);
            Assert.Equal(original, await File.ReadAllBytesAsync(recording.AudioPath));
            Assert.Equal(new[] { recording.AudioPath }, Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [LocalWhisperFact]
    [Trait("type", "IntegrationTests")]
    public async Task TranscribeAsync_WorkerCannotLoadModel_ReportsFailure()
    {
        var directory = Directory.CreateTempSubdirectory("sound-recorder-worker-test-");
        try
        {
            var recording = CreateSilentRecording(directory.FullName);
            var transcriber = new WhisperProcessTranscriber(
                Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_WORKER")!,
                new LocalModelStore(Path.Combine(directory.FullName, "missing-model.bin")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transcriber.TranscribeAsync(recording, timeout.Token));

            Assert.Contains("model", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { recording.AudioPath }, Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [LocalWhisperFact]
    [Trait("type", "IntegrationTests")]
    public async Task CancelAsync_ActiveWhisperInference_CancelsSafelyAndStartsNextJob()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sound-recorder-cancellation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "recording.wav");
        using (var audio = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1)))
        {
            var samples = Enumerable.Range(0, 48_000 * 30)
                .Select(index => .1f * MathF.Sin(2 * MathF.PI * 440 * index / 48_000))
                .ToArray();
            audio.WriteSamples(samples, 0, samples.Length);
        }

        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var recording = new SavedRecording(
            Guid.NewGuid(), path, "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(30));
        var starts = Channel.CreateUnbounded<bool>();
        var transcriber = new WhisperProcessTranscriber(
            Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_WORKER")!,
            new LocalModelStore(),
            new ProcessingLogger(starts.Writer));
        var queue = new TranscriptionQueue(
            transcriber,
            new AtomicTranscriptWriter(),
            new MemoryRepository());
        try
        {
            await queue.EnqueueAsync(recording, CancellationToken.None);
            await WaitForStartAsync(starts.Reader, queue, TimeSpan.FromSeconds(30));
            await queue.EnqueueAsync(recording, CancellationToken.None);
            // The large-model fixture must reach native inference, not just model loading.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var first = queue.Jobs[0];
            Assert.Equal(TranscriptionJobStatus.Running, first.Status);

            var timer = Stopwatch.StartNew();
            await queue.CancelAsync(first.Id, CancellationToken.None);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Cancellation took {timer.Elapsed}.");
            await WaitForStartAsync(starts.Reader, queue, TimeSpan.FromSeconds(5));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Worker release took {timer.Elapsed}.");
            Assert.Equal(TranscriptionJobStatus.Canceled, first.Status);
            Assert.Null(first.Error);
            Assert.Null(first.Outputs);

            var second = queue.Jobs[1];
            await queue.CancelAsync(second.Id, CancellationToken.None);
            await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TranscriptionJobStatus.Canceled, second.Status);
            Assert.Null(second.Error);
            Assert.Null(second.Outputs);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            foreach (var job in queue.Jobs)
            {
                await queue.CancelAsync(job.Id, CancellationToken.None);
            }

            await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForStartAsync(
        ChannelReader<bool> starts,
        TranscriptionQueue queue,
        TimeSpan timeout)
    {
        var started = starts.ReadAsync().AsTask();
        await Task.WhenAny(started, queue.WaitForIdleAsync(CancellationToken.None)).WaitAsync(timeout);
        Assert.True(started.IsCompletedSuccessfully, queue.Jobs.Last().Error ?? "The worker did not start.");
        await started;
    }

    private static SavedRecording CreateSilentRecording(string directory)
    {
        var path = Path.Combine(directory, "recording.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1)))
        {
            var silence = new byte[96_000];
            writer.Write(silence, 0, silence.Length);
        }

        return new SavedRecording(
            Guid.NewGuid(), path, "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
    }

    private sealed class LocalModelStore(string? modelPath = null) : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => true;

        public string? GetModelPath(string language) =>
            modelPath ?? Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_MODEL");
    }

    private sealed class MemoryRepository : ITranscriptionJobRepository
    {
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranscriptionJob>>([]);

        public Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ProcessingLogger(ChannelWriter<bool> starts) : ILogger<WhisperProcessTranscriber>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 700)
            {
                starts.TryWrite(true);
            }
        }
    }
}

public sealed class LocalWhisperFactAttribute : FactAttribute
{
    public LocalWhisperFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_MODEL")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_WORKER")))
        {
            Skip = "Set SOUND_RECORDER_TEST_MODEL and SOUND_RECORDER_TEST_WORKER to run native cancellation.";
        }
    }
}
