using SoundRecorder.Core;
using NAudio.Wave;

namespace SoundRecorder.Desktop;

public sealed class WindowsRecordingPlayback : IRecordingPlayback
{
    private readonly Func<IWavePlayer> _createOutput;
    private WaveFileReader? _reader;
    private IWavePlayer? _output;
    private TimeSpan _offset;
    private double _volume = 1;

    public WindowsRecordingPlayback(Func<IWavePlayer>? createOutput = null) =>
        _createOutput = createOutput ?? (() => new WaveOut());

    public Guid? RecordingId { get; private set; }
    public string? SourcePath { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position
    {
        get
        {
            if (_reader is null)
            {
                return TimeSpan.Zero;
            }
            if (_output is null)
            {
                return _reader.CurrentTime;
            }
            if (_output is not IWavePosition position)
            {
                throw new NotSupportedException("The playback device cannot report its audio position.");
            }
            return TimeSpan.FromSeconds(Math.Min(Duration.TotalSeconds,
                _offset.TotalSeconds + (double)position.GetPosition() / position.OutputWaveFormat.AverageBytesPerSecond));
        }
        set
        {
            var reader = _reader ?? throw new InvalidOperationException("Open a recording before seeking.");
            if (value < TimeSpan.Zero || value > Duration)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Seek must remain within the saved audio.");
            }
            var playing = IsPlaying;
            ReleaseOutput();
            reader.CurrentTime = value;
            _offset = value;
            if (playing)
            {
                Play();
            }
        }
    }
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Playback volume must be between zero and one.");
            }
            _volume = value;
            if (_output is not null)
            {
                _output.Volume = (float)value;
            }
        }
    }
    public string? LastError { get; private set; }

    public void Open(SavedRecording recording)
    {
        Close();
        if (recording.AudioDeleted)
        {
            throw new InvalidOperationException("The recording's audio has been deleted.");
        }
        _reader = new WaveFileReader(recording.AudioPath);
        RecordingId = recording.Id;
        SourcePath = recording.AudioPath;
        LastError = null;
    }

    public void Play()
    {
        var reader = _reader ?? throw new InvalidOperationException("Open a recording before playback.");
        if (_output is null)
        {
            var output = _createOutput();
            try
            {
                output.Init(reader);
                output.Volume = (float)_volume;
                output.PlaybackStopped += OutputStopped;
                _offset = reader.CurrentTime;
                _output = output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        LastError = null;
        _output.Play();
    }

    public void Pause() => _output?.Pause();

    private void OutputStopped(object? sender, StoppedEventArgs args)
    {
        if (ReferenceEquals(sender, _output) && args.Exception is not null)
        {
            LastError = "The audio device could not continue playback: " + args.Exception.Message;
        }
    }

    private void ReleaseOutput()
    {
        if (_output is { } output)
        {
            _output = null;
            output.PlaybackStopped -= OutputStopped;
            output.Dispose();
        }
    }

    public void Close()
    {
        try
        {
            ReleaseOutput();
        }
        finally
        {
            _reader?.Dispose();
            _reader = null;
            RecordingId = null;
            SourcePath = null;
            _offset = TimeSpan.Zero;
        }
    }

    public void Dispose() => Close();
}
