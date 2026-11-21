using System.Text;
using Microsoft.Extensions.Logging;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class TranscriptWriterStorageTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();

    [Fact]
    public async Task WriteAsync_MultipleSegments_WritesExactUtf8TextAndSubtitles()
    {
        var logger = new StorageTestLogger<AtomicTranscriptWriter>();
        var constructor = typeof(AtomicTranscriptWriter).GetConstructor([typeof(ILogger<AtomicTranscriptWriter>)]);
        Assert.NotNull(constructor);
        var writer = Assert.IsType<AtomicTranscriptWriter>(constructor.Invoke([logger]));
        var document = Document(
            new(TimeSpan.Zero, TimeSpan.FromMilliseconds(1234), "Olá 🌍"),
            new(TimeSpan.FromMilliseconds(1234), TimeSpan.FromHours(27) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3.456), "Second"));
        var outputs = await writer.WriteAsync(document, default);
        var nl = Environment.NewLine;
        Assert.Equal(_directory.PathFor("recording.txt"), outputs.TextPath);
        Assert.Equal(_directory.PathFor("recording.srt"), outputs.SubtitlePath);
        Assert.Equal($"[00:00:00.000] Olá 🌍{nl}[00:00:01.234] Second", await File.ReadAllTextAsync(outputs.TextPath));
        Assert.Equal($"1{nl}00:00:00,000 --> 00:00:01,234{nl}Olá 🌍{nl}{nl}2{nl}00:00:01,234 --> 27:02:03,456{nl}Second{nl}{nl}",
            await File.ReadAllTextAsync(outputs.SubtitlePath));
        Assert.False((await File.ReadAllBytesAsync(outputs.TextPath)).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.False((await File.ReadAllBytesAsync(outputs.SubtitlePath)).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Single(logger.Entries);
        Assert.Empty(Directory.GetFiles(_directory.Root, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_ExistingEitherOutput_ChoosesUniquePairWithoutReplacingFiles()
    {
        await File.WriteAllTextAsync(_directory.PathFor("recording.txt"), "existing text");
        await File.WriteAllTextAsync(_directory.PathFor("recording.transcript-2.srt"), "existing subtitles");
        var result = await new AtomicTranscriptWriter().WriteAsync(Document(), default);
        Assert.Equal(_directory.PathFor("recording.transcript-3.txt"), result.TextPath);
        Assert.Equal(_directory.PathFor("recording.transcript-3.srt"), result.SubtitlePath);
        Assert.Equal("", await File.ReadAllTextAsync(result.TextPath));
        Assert.Equal("", await File.ReadAllTextAsync(result.SubtitlePath));
        Assert.Equal("existing text", await File.ReadAllTextAsync(_directory.PathFor("recording.txt")));
        Assert.Equal("existing subtitles", await File.ReadAllTextAsync(_directory.PathFor("recording.transcript-2.srt")));
    }

    [Theory]
    [InlineData(-1, 1, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2, 1, 0, 0)]
    [InlineData(0, 101000000, 0, 0)]
    [InlineData(0, 2, 1, 3)]
    public async Task WriteAsync_InvalidTimeline_RejectsWithoutProducingFiles(long start, long end, long nextStart, long nextEnd)
    {
        var segments = new List<TranscriptSegment>
        {
            new(TimeSpan.FromTicks(start), TimeSpan.FromTicks(end), "first"),
        };
        if (nextEnd != 0)
        {
            segments.Add(new(TimeSpan.FromTicks(nextStart), TimeSpan.FromTicks(nextEnd), "second"));
        }

        var document = Document(segments.ToArray()) with
        {
            Recording = Document().Recording with { CapturedDuration = TimeSpan.FromSeconds(10) },
        };
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AtomicTranscriptWriter().WriteAsync(document, default));
        Assert.Equal("Transcript segments must be ordered, non-overlapping, and within the saved audio duration.", exception.Message);
        Assert.Empty(Directory.GetFiles(_directory.Root));
    }

    [Fact]
    public async Task WriteAsync_SegmentEndsAtDuration_AllowsExactBoundary()
    {
        var document = Document(new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromHours(28), "complete"));
        var outputs = await new AtomicTranscriptWriter().WriteAsync(document, default);
        Assert.True(File.Exists(outputs.SubtitlePath));
    }

    [Fact]
    public async Task WriteAsync_PreCanceled_CleansTemporaryFilesAndReportsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logger = new StorageTestLogger<AtomicTranscriptWriter>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AtomicTranscriptWriter(logger).WriteAsync(Document(), cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory.Root));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task WriteAsync_CanceledAfterBothTemporaryFilesAreWritten_DoesNotCommitOrChangeEarlierOutputs()
    {
        var earlierText = _directory.PathFor("recording.txt");
        var earlierSubtitle = _directory.PathFor("recording.srt");
        await File.WriteAllTextAsync(earlierText, "earlier text");
        await File.WriteAllTextAsync(earlierSubtitle, "earlier subtitles");
        using var cancellation = new CancellationTokenSource();
        var completedWrites = new List<string>();
        var writer = new AtomicTranscriptWriter(null, null, async (path, text, encoding, token) =>
        {
            await File.WriteAllTextAsync(path, text, encoding, token);
            completedWrites.Add(path);
            if (completedWrites.Count == 2)
            {
                Assert.All(completedWrites, stagedPath => Assert.True(File.Exists(stagedPath)));
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync(Document(), cancellation.Token));

        Assert.Equal(2, completedWrites.Count);
        Assert.Equal("earlier text", await File.ReadAllTextAsync(earlierText));
        Assert.Equal("earlier subtitles", await File.ReadAllTextAsync(earlierSubtitle));
        Assert.Equal(new[] { earlierText, earlierSubtitle }.Order(),
            Directory.GetFiles(_directory.Root).Order());
    }

    [Fact]
    public async Task WriteAsync_SubtitleDestinationIsDirectory_RollsBackTextAndPreservesDirectory()
    {
        Directory.CreateDirectory(_directory.PathFor("recording.srt"));
        await Assert.ThrowsAnyAsync<IOException>(() => new AtomicTranscriptWriter().WriteAsync(Document(), default));
        Assert.False(File.Exists(_directory.PathFor("recording.txt")));
        Assert.True(Directory.Exists(_directory.PathFor("recording.srt")));
        Assert.Empty(Directory.GetFiles(_directory.Root));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WriteAsync_DestinationAppearsDuringCommit_PreservesOtherWritersFile(int failAt)
    {
        var commits = 0;
        string? otherPath = null;
        var writer = new AtomicTranscriptWriter(null, (source, destination) =>
        {
            commits++;
            if (commits == failAt)
            {
                otherPath = destination;
                File.WriteAllText(destination, "another writer owns this");
            }

            File.Move(source, destination);
        }, null);

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(Document(), default));

        Assert.Equal("another writer owns this", await File.ReadAllTextAsync(otherPath!));
        Assert.Equal([otherPath!], Directory.GetFiles(_directory.Root));
    }

    [Fact]
    public async Task WriteAsync_CompletionNotificationThrows_DoesNotLeaveAnUnreportedPair()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            new AtomicTranscriptWriter(new FailingCompletionLogger()).WriteAsync(Document(), default));
        Assert.Empty(Directory.GetFiles(_directory.Root));
    }

    private TranscriptDocument Document(params TranscriptSegment[] segments) => new(
        new SavedRecording(Guid.NewGuid(), _directory.PathFor("recording.wav"), "English",
            RecordingOutcome.Complete, TimeSpan.FromHours(28)), "English", segments);

    public void Dispose() => _directory.Dispose();

    private sealed class FailingCompletionLogger : ILogger<AtomicTranscriptWriter>
    {
        private bool _failed;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("Completion notification failed.");
            }
        }
    }
}
