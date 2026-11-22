using System.Buffers.Binary;
using NAudio.Wave;
using Reqnroll;
using SoundRecorder.Core;
using SoundRecorder.Desktop;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Review recordings and manage their owned artifacts")]
public sealed class RecordingLibraryPlaybackSteps
{
    private readonly LibraryDeletionFixture _fixture = new();
    private readonly List<ControlledPlaybackOutput> _outputs = [];
    private readonly IRecordingPlayback _playback;
    private IRecordingLibrary _library = null!;
    private Dictionary<string, byte[]> _before = [];
    private short _sampleAfterSeek;
    private Exception? _playbackError;

    public RecordingLibraryPlaybackSteps()
    {
        _playback = new WindowsRecordingPlayback(() =>
        {
            var output = new ControlledPlaybackOutput();
            _outputs.Add(output);
            return output;
        });
    }

    [Given("a disposable valid WAV recording with a completed transcript")]
    public async Task GivenSavedWaveAndTranscript()
    {
        await _fixture.InitializeAsync();
        using (var writer = new WaveFileWriter(_fixture.Recording.AudioPath, new WaveFormat(16000, 16, 1)))
        {
            WriteCapturedInterval(writer, 0x1111);
            WriteCapturedInterval(writer, 0x3333);
        }
        _library = _fixture.CreateLibrary();
        _before = await SnapshotAsync();
    }

    private static void WriteCapturedInterval(WaveFileWriter writer, short sample)
    {
        var bytes = new byte[16000 * 2 * 4];
        for (var offset = 0; offset < bytes.Length; offset += 2)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset, 2), sample);
        writer.Write(bytes, 0, bytes.Length);
    }

    [Given("the WAV includes no audio from paused capture intervals")]
    public void GivenPausedSamplesAreExcluded()
    {
        using var reader = new WaveFileReader(_fixture.Recording.AudioPath);
        Assert.Equal(TimeSpan.FromSeconds(8), reader.TotalTime);
        var bytes = new byte[checked((int)reader.Length)];
        Assert.Equal(bytes.Length, reader.Read(bytes, 0, bytes.Length));
        for (var offset = 0; offset < bytes.Length; offset += 2)
        {
            var expected = offset < 128000 ? (short)0x1111 : (short)0x3333;
            Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2)));
        }
    }

    [When("the user plays that recording and seeks to {int} seconds")]
    public void WhenPlayAndSeek(int seconds)
    {
        _playback.Open(_fixture.Recording);
        _playback.Play();
        var initial = Assert.Single(_outputs);
        initial.ReadAhead(6);
        initial.PlayedBytes = 32000;
        Assert.Equal(TimeSpan.FromSeconds(1), _playback.Position);
        _playback.Position = TimeSpan.FromSeconds(seconds);
        Assert.True(initial.Disposed);
        var sample = new byte[2];
        Assert.Equal(2, _outputs[^1].Provider!.Read(sample.AsSpan()));
        _sampleAfterSeek = BinaryPrimitives.ReadInt16LittleEndian(sample);
    }

    [Then("playback reads that recording's WAV")]
    public void ThenPlaybackReadsSelectedWave()
    {
        Assert.Equal(_fixture.Recording.Id, _playback.RecordingId);
        Assert.Equal(_fixture.Recording.AudioPath, _playback.SourcePath);
        Assert.IsType<WaveFileReader>(_outputs[^1].Provider);
        Assert.Equal((short)0x3333, _sampleAfterSeek);
        Assert.True(_playback.IsPlaying);
    }

    [Then("its position is {int} seconds on the saved-audio timeline")]
    public void ThenSavedAudioPosition(int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), _playback.Position);
        Assert.Equal(TimeSpan.FromSeconds(8), _playback.Duration);
    }

    [Then("the completed transcript remains available for review")]
    public async Task ThenCompletedTranscriptReadable()
    {
        await AssertRetainedTranscriptAsync();
        foreach (var (path, bytes) in _before)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Then("no transcription job is started by playback")]
    public async Task ThenPlaybackDoesNotStartTranscription()
    {
        Assert.Equal(_before[_fixture.JobsPath], await File.ReadAllBytesAsync(_fixture.JobsPath));
        var job = Assert.Single(await _fixture.Jobs.LoadAsync(default));
        Assert.Equal(_fixture.Outputs[0].JobId, job.Id);
        Assert.Equal(TranscriptionJobStatus.Succeeded, job.Status);
    }

    [Given("a library entry has an audio file and a completed TXT and SRT pair")]
    public Task GivenLibraryEntryWithOutputs() => GivenSavedWaveAndTranscript();

    [Given("an external operation removes only its audio file")]
    public void GivenAudioRemovedExternally() => File.Delete(_fixture.Recording.AudioPath);

    [When("the user requests playback")]
    public void WhenPlaybackRequested() =>
        _playbackError = Record.Exception(() =>
        {
            _playback.Open(_fixture.Recording);
            _playback.Play();
        });

    [Then("the missing audio is reported explicitly")]
    public async Task ThenMissingAudioReported()
    {
        var error = Assert.IsType<FileNotFoundException>(_playbackError);
        Assert.Equal(_fixture.Recording.AudioPath, error.FileName);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(_outputs);
        Assert.False(_playback.IsPlaying);
        var entry = Assert.Single(await _library.LoadAsync(default));
        Assert.Equal(AudioAvailability.Missing, entry.AudioAvailability);
        Assert.False(entry.Recording.AudioDeleted);
        Assert.Equal(LibraryFailureCode.ArtifactMissing, Assert.Single(entry.Issues).Code);
    }

    [Then("the completed transcript and subtitles remain available")]
    public async Task ThenTranscriptAndSubtitlesAvailable()
    {
        await AssertRetainedTranscriptAsync();
        foreach (var (path, bytes) in _before.Where(file => file.Key != _fixture.Recording.AudioPath))
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Then("the audio is not recreated or replaced")]
    public async Task ThenAudioNotReplaced()
    {
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        Assert.Null(_playback.SourcePath);
        Assert.Equal(_before.Keys.Where(path => path != _fixture.Recording.AudioPath).Order(),
            (await SnapshotAsync()).Keys.Order());
    }

    private async Task AssertRetainedTranscriptAsync()
    {
        var transcript = await _library.ReadTranscriptAsync(_fixture.Recording.Id, _fixture.Outputs[0].JobId, default);
        Assert.Equal("Transcript attempt 1", transcript.Text);
        Assert.Equal("1\r\n00:00:00,000 --> 00:00:04,000\r\nSubtitle attempt 1\r\n", transcript.Subtitles);
        Assert.Empty(transcript.Issues);
    }

    private async Task<Dictionary<string, byte[]>> SnapshotAsync()
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var path in Directory.GetFiles(_fixture.Root))
            files[path] = await File.ReadAllBytesAsync(path);
        return files;
    }

    [AfterScenario]
    public void Cleanup()
    {
        _playback.Dispose();
        _fixture.Dispose();
    }

    private sealed class ControlledPlaybackOutput : IWavePlayer, IWavePosition
    {
        public IWaveProvider? Provider { get; private set; }
        public long PlayedBytes { get; set; }
        public bool Disposed { get; private set; }
        public float Volume { get; set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Provider!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider provider) => Provider = provider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new(null));
        }
        public long GetPosition() => PlayedBytes;
        public void ReadAhead(int seconds)
        {
            var bytes = new byte[seconds * OutputWaveFormat.AverageBytesPerSecond];
            Assert.Equal(bytes.Length, Provider!.Read(bytes.AsSpan()));
        }
        public void Dispose()
        {
            Stop();
            Disposed = true;
        }
    }
}
