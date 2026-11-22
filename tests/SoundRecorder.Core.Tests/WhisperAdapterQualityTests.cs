using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class WhisperAdapterQualityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("recorder-whisper-tests-").FullName;

    [Fact]
    public async Task TranscribeAsync_ExplicitPinnedPathBypassesSelectionAndReportsRealPreparationAndRecognition()
    {
        var recording = Recording();
        var original = await File.ReadAllBytesAsync(recording.AudioPath);
        var seen = new List<TranscriptionProgress>();
        var logger = new RecordingLogger<WhisperLocalTranscriber>();
        string? processing = null;
        var transcriber = new WhisperLocalTranscriber(new RejectModelLookup(), logger, (model, language, audio, _) =>
        {
            Assert.Equal("explicit-pinned.bin", model);
            Assert.Equal("en", language);
            processing = Assert.IsType<FileStream>(audio).Name;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(TimeSpan.Zero, TimeSpan.FromSeconds(.5), "speech")]);
        });

        var document = await transcriber.TranscribeAsync(new(Guid.NewGuid(), recording, "base-original", "explicit-pinned.bin"),
            new TranscriptionProgressProtocolTests.Reporter(value => seen.Add(value)), default);

        Assert.Equal(new TranscriptionProgress[] { new(TranscriptionPhase.Preparing), new(TranscriptionPhase.Recognizing) }, seen);
        Assert.Equal("speech", Assert.Single(document.Segments).Text);
        Assert.Contains(700, logger.EventIds);
        Assert.Contains(701, logger.EventIds);
        Assert.NotNull(processing);
        Assert.False(File.Exists(processing));
        Assert.Equal(original, await File.ReadAllBytesAsync(recording.AudioPath));
    }

    private sealed class RejectModelLookup : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => throw new InvalidOperationException("An explicit path must not consult selection.");
        public string? GetModelPath(string language) => throw new InvalidOperationException("An explicit path must not consult selection.");
        public string? GetModelPath(string language, string? modelId) => throw new InvalidOperationException("An explicit path must not be substituted.");
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData("Brazilian Portuguese", "pt")]
    public async Task TranscribeAsync_SelectedLanguage_PassesNativeLanguageAndNormalizesSegments(
        string language, string code)
    {
        var recording = Recording(language);
        string? temporary = null;
        var transcriber = new WhisperLocalTranscriber(new ModelStore(), null, (model, selected, audio, _) =>
        {
            Assert.Equal("model.bin", model);
            Assert.Equal(code, selected);
            temporary = Assert.IsType<FileStream>(audio).Name;
            using var wave = new WaveFileReader(audio);
            Assert.Equal(16_000, wave.WaveFormat.SampleRate);
            Assert.Equal(1, wave.WaveFormat.Channels);
            Assert.InRange(wave.TotalTime.TotalMilliseconds, 990, 1010);
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(.5), "  hello  "),
                new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1.1), "tail"),
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.05), "outside start"),
                new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(.5), "zero duration"),
                new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(.4), "reversed"),
                new(TimeSpan.Zero, TimeSpan.FromMilliseconds(1100) + TimeSpan.FromTicks(1), "outside tolerance"),
                new(TimeSpan.Zero, TimeSpan.FromSeconds(.5), " \t "),
            ]);
        });

        var document = await transcriber.TranscribeAsync(recording, CancellationToken.None);

        Assert.Equal(recording, document.Recording);
        Assert.Equal(language, document.Language);
        Assert.Equal(
            new[]
            {
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(.5), "hello"),
                new TranscriptSegment(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(1), "tail"),
            },
            document.Segments);
        Assert.NotNull(temporary);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task TranscribeAsync_UnsupportedLanguage_DoesNotEnterInferenceAndCleansProcessingCopy()
    {
        var called = false;
        var transcriber = new WhisperLocalTranscriber(new ModelStore(), null, (_, _, _, _) =>
        {
            called = true;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>([]);
        });
        var temporary = Path.Combine(_directory, "processing.wav");

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            transcriber.TranscribeAsync(Recording("French"), temporary, CancellationToken.None));

        Assert.Contains("French", error.Message);
        Assert.False(called);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task TranscribeAsync_InferenceFails_DisposesAndRemovesProcessingCopy()
    {
        var failure = new IOException("inference failed");
        Stream? opened = null;
        var transcriber = new WhisperLocalTranscriber(new ModelStore(), null, (_, _, audio, _) =>
        {
            opened = audio;
            return Task.FromException<IReadOnlyList<TranscriptSegment>>(failure);
        });
        var temporary = Path.Combine(_directory, "processing.wav");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            transcriber.TranscribeAsync(Recording(), temporary, CancellationToken.None));

        Assert.Same(failure, error);
        Assert.NotNull(opened);
        Assert.False(opened.CanRead);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task TranscribeAsync_CanceledResampling_RemovesProcessingCopyWithoutInference()
    {
        var transcriber = new WhisperLocalTranscriber(new ModelStore(), null, (_, _, _, _) =>
            throw new InvalidOperationException("Canceled audio must not enter inference"));
        var temporary = Path.Combine(_directory, "processing.wav");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transcriber.TranscribeAsync(Recording(), temporary, canceled.Token));

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task TranscribeAsync_ModelUnavailable_DoesNotCreateProcessingCopy()
    {
        var transcriber = new WhisperLocalTranscriber(new ModelStore(null));
        var temporary = Path.Combine(_directory, "processing.wav");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transcriber.TranscribeAsync(Recording(), temporary, CancellationToken.None));

        Assert.Contains("English", error.Message);
        Assert.False(File.Exists(temporary));
    }

    private SavedRecording Recording(string language = "English")
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 2)))
        {
            var audio = new byte[48_000 * 4];
            writer.Write(audio, 0, audio.Length);
        }

        return new SavedRecording(Guid.NewGuid(), path, language, RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ModelStore(string? path = "model.bin") : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => path is not null;
        public string? GetModelPath(string language) => path;
    }
}
