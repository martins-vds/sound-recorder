using NAudio.Wave;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

public sealed class WhisperNativeQualityTests
{
    [LocalNativeWhisperFact]
    [Trait("type", "IntegrationTests")]
    public async Task TranscribeAsync_LocalTinyModel_ReturnsTrimmedSpeechWithSavedAudioTimestamps()
    {
        var directory = Directory.CreateTempSubdirectory("recorder-native-whisper-");
        try
        {
            var path = Path.Combine(directory.FullName, "speech.wav");
            await using (var fixture = typeof(WhisperNativeQualityTests).Assembly.GetManifestResourceStream(
                             "SoundRecorder.Core.Tests.Fixtures.native-speech.wav")!)
            await using (var file = File.Create(path))
            {
                await fixture.CopyToAsync(file);
            }

            var original = await File.ReadAllBytesAsync(path);
            TimeSpan duration;
            using (var audio = new WaveFileReader(path))
            {
                duration = audio.TotalTime;
            }

            var recording = new SavedRecording(
                Guid.NewGuid(), path, "English", RecordingOutcome.Complete, duration);
            var transcriber = new WhisperLocalTranscriber(new LocalNativeModel());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var document = await transcriber.TranscribeAsync(recording, timeout.Token);

            Assert.NotEmpty(document.Segments);
            Assert.Equal("English", document.Language);
            Assert.Equal(recording, document.Recording);
            Assert.All(document.Segments, segment =>
            {
                Assert.False(string.IsNullOrWhiteSpace(segment.Text));
                Assert.Equal(segment.Text.Trim(), segment.Text);
                Assert.InRange(segment.Start, TimeSpan.Zero, duration);
                Assert.True(segment.End > segment.Start);
                Assert.True(segment.End <= duration);
            });
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class LocalNativeModel : ITranscriptionModelStore
    {
        public bool HasCompatibleModel(string language) => true;
        public string? GetModelPath(string language) =>
            Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_NATIVE_MODEL");
    }
}

public sealed class LocalNativeWhisperFactAttribute : FactAttribute
{
    public LocalNativeWhisperFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOUND_RECORDER_TEST_NATIVE_MODEL")))
        {
            Skip = "Set SOUND_RECORDER_TEST_NATIVE_MODEL to an existing local tiny GGML model for native adapter coverage.";
        }
    }
}
