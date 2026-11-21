using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Desktop;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Recovery;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Ui.TestHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("no model" or "valid package" or "invalid package" or
                "missing recording" or "saved transcript" or "running job"))
        {
            throw new ArgumentException("Expected an isolated state directory and a supported fixture profile.");
        }

        var root = Path.GetFullPath(args[0]);
        var engine = new FixtureEngine();
        using var client = new System.Net.Http.HttpClient(new OfflineHandler());
        var models = new LocalTranscriptionModelManager(Path.Combine(root, "models"), client);
        var selectedPackage = CreatePackage(root, "valid.zip", invalidHash: false);
        if (args[1] == "invalid package")
        {
            models.ImportAsync(selectedPackage, CancellationToken.None).GetAwaiter().GetResult();
            selectedPackage = CreatePackage(root, "invalid.zip", invalidHash: true);
        }
        var history = new JsonRecordingHistory(Path.Combine(root, "history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(root, "jobs.json"));
        var writer = new AtomicTranscriptWriter();
        if (args[1] is "missing recording" or "saved transcript" or "running job")
        {
            var recording = new SavedRecording(Guid.NewGuid(), Path.Combine(root, "fixture.wav"), "English",
                RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
            history.AddAsync(recording, CancellationToken.None).GetAwaiter().GetResult();
            if (args[1] != "missing recording")
            {
                using (var audio = new NAudio.Wave.WaveFileWriter(recording.AudioPath,
                           new NAudio.Wave.WaveFormat(48_000, 16, 1)))
                {
                    audio.Write(new byte[96_000], 0, 96_000);
                }
                models.ImportAsync(selectedPackage, CancellationToken.None).GetAwaiter().GetResult();
                var outputs = args[1] == "saved transcript"
                    ? writer.WriteAsync(FixtureTranscriber.Document(recording), CancellationToken.None)
                        .GetAwaiter().GetResult()
                    : null;
                var job = new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow,
                    outputs is null ? TranscriptionJobStatus.Queued : TranscriptionJobStatus.Succeeded,
                    outputs, null);
                repository.SaveAsync([job], CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        var queue = new TranscriptionQueue(new FixtureTranscriber(args[1] == "running job"), writer, repository);
        var recorder = new RecorderApplication(engine, queue, models, history);
        var viewModel = new MainViewModel(recorder, engine, queue, models, history,
            new RecoveryManager(new LocalRecoveryStorage(Path.Combine(root, "capture"))),
            new FixtureDevices(), NullLogger<MainViewModel>.Instance)
        {
            DestinationFolder = root,
        };
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var dialogs = new WindowsDesktopDialogs(dialog =>
        {
            if (dialog is Microsoft.Win32.OpenFileDialog file)
            {
                file.FileName = selectedPackage;
                return true;
            }
            throw new InvalidOperationException("No folder-dialog selection fixture is configured.");
        }, System.Windows.MessageBox.Show);
        var window = new MainWindow(viewModel, engine, dialogs,
            new FixtureEnvironment(), NullLogger<MainWindow>.Instance);
        application.MainWindow = window;
        application.Run(window);
    }

    private static string CreatePackage(string root, string name, bool invalidHash)
    {
        var path = Path.Combine(root, name);
        var content = Encoding.UTF8.GetBytes("Deterministic UI fixture; not a native model.");
        var package = new TranscriptionModelPackage(1, "ui-fixture", "Whisper.net 1.9", "model.bin",
            invalidHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(content)),
            "Windows11-x64", ["English", "Brazilian Portuguese"]);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var model = archive.CreateEntry(package.ModelFile).Open())
        {
            model.Write(content);
        }
        using (var manifest = archive.CreateEntry("manifest.json").Open())
        {
            JsonSerializer.Serialize(manifest, package);
        }
        return path;
    }
}

internal sealed class FixtureEngine : IRecordingEngine, IRecordingMonitor
{
    private readonly Stopwatch _clock = new();
    private TimeSpan _captured;
    private RecordingRequest? _request;

    public string? LastFailure => null;
    public TimeSpan CapturedDuration => _captured + _clock.Elapsed;
    public float MicrophoneLevel => _clock.IsRunning ? .25f : 0;
    public float SystemAudioLevel => _clock.IsRunning ? .5f : 0;

    public Task StartAsync(RecordingRequest request, CancellationToken cancellationToken)
    {
        _request = request;
        _captured = TimeSpan.Zero;
        _clock.Restart();
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        _captured += _clock.Elapsed;
        _clock.Reset();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        _clock.Start();
        return Task.CompletedTask;
    }

    public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
    {
        _clock.Stop();
        var request = _request ?? throw new InvalidOperationException("No fixture recording is active.");
        var destination = request.DestinationFolder ??
            throw new InvalidOperationException("The fixture recording has no destination.");
        var path = Path.Combine(destination, Guid.NewGuid() + ".wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[96_000], 0, 96_000);
        }
        return Task.FromResult(new SavedRecording(Guid.NewGuid(), path, request.Language,
            RecordingOutcome.Complete, CapturedDuration));
    }
}

internal sealed class OfflineHandler : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Network access is disabled in the UI test host.");
}

internal sealed class FixtureDevices : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceDescriptor> GetMicrophones() => [new("fixture-mic", "Fixture microphone")];
    public IReadOnlyList<AudioDeviceDescriptor> GetPlaybackDevices() => [new("fixture-playback", "Fixture playback")];
}

internal sealed class FixtureTranscriber(bool block) : ILocalTranscriber
{
    public async Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token)
    {
        if (block)
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        return Document(recording);
    }

    public static TranscriptDocument Document(SavedRecording recording) =>
        new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "UI fixture transcript.")]);
}

internal sealed class FixtureEnvironment : IDesktopEnvironment
{
    private readonly Window _notification = new()
    {
        Title = "Fixture notification area",
        Width = 260,
        Height = 120,
    };

    public FixtureEnvironment()
    {
        System.Windows.Automation.AutomationProperties.SetName(_notification, "Fixture notification area");
        var restore = new System.Windows.Controls.Button { Content = "Restore recorder" };
        restore.Click += (_, _) =>
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            _notification.Hide();
        };
        _notification.Content = restore;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested { add { } remove { } }
    public void SetNotificationText(string text) { }
    public void ShowBalloonTip(int milliseconds, string title, string message, System.Windows.Forms.ToolTipIcon icon)
    {
        if (title == "Sound Recorder is still running")
        {
            _notification.Show();
        }
    }
    public void PreventIdleSleep(bool prevent) { }
    public void CopyTranscript(string text) { }
    public void OpenRecordingFolder(string audioPath) { }
    public void Dispose() => _notification.Close();
}
