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
using SoundRecorder.Infrastructure.Analysis;

namespace SoundRecorder.Ui.TestHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var realAnalysis = args.Length == 2 && args[1] == "analysis real model";
        if (realAnalysis) args[1] = "saved transcript";
        var searchable = args.Length == 2 && args[1] == "searchable meetings";
        var workspace = args.Length == 2 && args[1] == "meeting workspace";
        var mixedHistory = args.Length == 2 && args[1] == "workspace with mixed job history";
        var longTranscript = args.Length == 2 && args[1] == "long transcript";
        if (searchable || workspace || mixedHistory)
        {
            args[1] = "two playable transcripts";
        }
        else if (longTranscript)
        {
            args[1] = "playable transcript";
        }
        var playbackAndWorker = args.Length == 2 && args[1] == "playable transcript and running job";
        var twoPlayable = args.Length == 2 && args[1] == "two playable transcripts";
        if (playbackAndWorker || twoPlayable)
        {
            args[1] = "playable transcript";
        }
        var retainedAudioDeleted = args.Length == 2 && args[1] == "audio deleted but transcripts retained";
        var unusableAudio = args.Length == 2 && args[1] == "unusable audio and retained transcript";
        var deletionFailure = args.Length == 2 && args[1] == "artifact deletion failure";
        if (retainedAudioDeleted || unusableAudio || deletionFailure)
        {
            args[1] = "saved transcript";
        }
        var englishOnlyModel = args.Length == 2 && args[1] == "English-only downloaded model";
        if (englishOnlyModel)
        {
            args[1] = "valid package";
        }
        var unknownModelLength = args.Length == 2 && args[1] == "unknown download length";
        var pinnedJobs = args.Length == 2 && args[1] == "queued model-pinned job";
        var modelDownloads = args.Length == 2 && (args[1] == "controlled model download" || unknownModelLength || pinnedJobs);
        if (modelDownloads)
        {
            args[1] = pinnedJobs ? "running job" : "valid package";
        }
        var controlledWorker = playbackAndWorker || mixedHistory || (args.Length == 2 && args[1] is "controlled running job" or "worker without progress");
        if (controlledWorker && !playbackAndWorker && !mixedHistory)
        {
            args[1] = "running job";
        }
        var catalog = args.Length == 2 && args[1] == "model catalog";
        if (catalog)
        {
            args[1] = "valid package";
        }
        if (args.Length == 2 && args[1] == "meeting workspace")
        {
            args[1] = "saved transcript";
        }
        var multipleAttempts = twoPlayable || (args.Length == 2 && args[1] == "multiple transcript attempts");
        var playable = args.Length == 2 && (args[1] == "playable transcript" || multipleAttempts);
        if (playable)
        {
            args[1] = "saved transcript";
        }
        if (args.Length != 2 || args[1] is not ("no model" or "valid package" or "invalid package" or
                "missing recording" or "saved transcript" or "running job"))
        {
            throw new ArgumentException("Expected an isolated state directory and a supported fixture profile.");
        }

        var root = Path.GetFullPath(args[0]);
        var engine = new FixtureEngine();
        using var downloadHandler = modelDownloads ? new FixtureModelDownloads(root, unknownModelLength) : null;
        using var client = new System.Net.Http.HttpClient(downloadHandler is null ? new OfflineHandler(root) : downloadHandler);
        var localModels = new LocalTranscriptionModelManager(Path.Combine(root, "models"), client);
        ILocalTranscriptionModelManager models = localModels;
        if (downloadHandler is not null)
        {
            var baseEntry = TranscriptionModelCatalog.Entries.Single(entry => entry.Key == "whisper.net:base:NoQuantization");
            localModels.DownloadAsync(baseEntry, new Progress<ModelDownloadProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            if (pinnedJobs)
            {
                var smallEntry = TranscriptionModelCatalog.Entries.Single(entry => entry.Key == "whisper.net:small:Q5_1");
                localModels.DownloadAsync(smallEntry, new Progress<ModelDownloadProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            }
            localModels.SelectAsync(baseEntry.Key, CancellationToken.None).GetAwaiter().GetResult();
            downloadHandler.Controlled = !pinnedJobs;
            models = new FixtureModelManager(localModels, root, unknownModelLength);
        }
        var selectedPackage = CreatePackage(root, "valid.zip", invalidHash: false, englishOnly: englishOnlyModel);
        if (catalog || englishOnlyModel || args[1] == "invalid package")
        {
            models.ImportAsync(selectedPackage, CancellationToken.None).GetAwaiter().GetResult();
            if (args[1] == "invalid package")
            {
                selectedPackage = CreatePackage(root, "invalid.zip", invalidHash: true);
            }
        }
        var history = new JsonRecordingHistory(Path.Combine(root, "history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(root, "jobs.json"));
        var writer = new AtomicTranscriptWriter();
        FileStream? heldArtifact = null;
        if (args[1] is "missing recording" or "saved transcript" or "running job")
        {
            var recording = new SavedRecording(multipleAttempts
                    ? Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") : Guid.NewGuid(),
                Path.Combine(root, "fixture.wav"), "English",
                RecordingOutcome.Complete, TimeSpan.FromSeconds(playable ? 6 : 1))
            {
                Title = searchable ? "Design handover" : null,
            };
            history.AddAsync(recording, CancellationToken.None).GetAwaiter().GetResult();
            if (args[1] != "missing recording")
            {
                using (var audio = new NAudio.Wave.WaveFileWriter(recording.AudioPath,
                           new NAudio.Wave.WaveFormat(48_000, 16, 1)))
                {
                    var bytes = new byte[(playable ? 6 : 1) * 96_000];
                    audio.Write(bytes, 0, bytes.Length);
                }
                if (!pinnedJobs)
                {
                    models.ImportAsync(selectedPackage, CancellationToken.None).GetAwaiter().GetResult();
                }
                var document = FixtureTranscriber.Document(recording, playable);
                if (realAnalysis)
                {
                    document = new TranscriptDocument(recording, "English",
                        [new(TimeSpan.Zero, recording.CapturedDuration,
                            "Ana will send the draft on Friday. The launch date has not been decided.")]);
                }
                if (longTranscript)
                {
                    document = new TranscriptDocument(recording, recording.Language,
                        [new(TimeSpan.Zero, recording.CapturedDuration,
                            string.Join("\n\n", Enumerable.Repeat(
                                "UI fixture transcript. This long retained text can be scrolled independently of the recording controls.",
                                40)))]);
                }
                var outputs = args[1] == "saved transcript"
                    ? writer.WriteAsync(document, CancellationToken.None)
                        .GetAwaiter().GetResult()
                    : null;
                var job = new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow,
                    outputs is null ? TranscriptionJobStatus.Queued : TranscriptionJobStatus.Succeeded,
                    outputs, null, modelId: models.SelectedModelId);
                var seededJobs = new List<TranscriptionJob> { job };
                if (pinnedJobs)
                {
                    seededJobs.Add(new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow.AddSeconds(1),
                        TranscriptionJobStatus.Queued, null, null, modelId: models.SelectedModelId));
                }
                if (controlledWorker)
                {
                    if (playbackAndWorker || mixedHistory)
                    {
                        seededJobs.Add(new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow.AddSeconds(1),
                            TranscriptionJobStatus.Queued, null, null, modelId: models.SelectedModelId));
                    }
                    else
                    {
                        var earlierOutputs = writer.WriteAsync(FixtureTranscriber.Document(recording), CancellationToken.None)
                            .GetAwaiter().GetResult();
                        seededJobs.Insert(0, new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow.AddMinutes(-1),
                            TranscriptionJobStatus.Succeeded, earlierOutputs, null, modelId: models.SelectedModelId));
                    }
                }
                if (multipleAttempts)
                {
                    var secondOutputs = writer.WriteAsync(FixtureTranscriber.Document(recording, true), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    seededJobs.Add(new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow.AddSeconds(1),
                        TranscriptionJobStatus.Succeeded, secondOutputs, null, modelId: models.SelectedModelId));
                    var other = new SavedRecording(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Path.Combine(root, "other.wav"), "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1))
                    {
                        Title = "Other recording",
                    };
                    using (var audio = new NAudio.Wave.WaveFileWriter(other.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
                    {
                        audio.Write(new byte[96_000], 0, 96_000);
                    }
                    history.AddAsync(other, CancellationToken.None).GetAwaiter().GetResult();
                    var otherDocument = searchable
                        ? new TranscriptDocument(other, other.Language,
                            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Notes from handover discussion.")])
                        : FixtureTranscriber.Document(other, false);
                    var otherOutputs = writer.WriteAsync(otherDocument, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    seededJobs.Add(new TranscriptionJob(Guid.NewGuid(), other, DateTimeOffset.UtcNow,
                        TranscriptionJobStatus.Succeeded, otherOutputs, null, modelId: models.SelectedModelId));
                    if (searchable)
                    {
                        var unmatched = new SavedRecording(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                            Path.Combine(root, "unmatched.wav"), "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1))
                        {
                            Title = "Budget discussion",
                        };
                        using (var audio = new NAudio.Wave.WaveFileWriter(unmatched.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
                        {
                            audio.Write(new byte[96_000], 0, 96_000);
                        }
                        history.AddAsync(unmatched, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                repository.SaveAsync(seededJobs, CancellationToken.None).GetAwaiter().GetResult();
                if (deletionFailure && outputs is not null)
                {
                    heldArtifact = File.Open(outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                if (unusableAudio)
                {
                    File.WriteAllBytes(recording.AudioPath, Encoding.UTF8.GetBytes("Not a WAV file"));
                }
                if (retainedAudioDeleted)
                {
                    var initialLibrary = new RecordingLibrary(history, repository,
                        cancelRecordingAsync: (_, _) => Task.CompletedTask,
                        releasePlaybackAsync: (_, _) => Task.CompletedTask);
                    var result = initialLibrary.DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AudioOnly,
                        CancellationToken.None).GetAwaiter().GetResult();
                    if (result.Status != LibraryDeletionStatus.Completed)
                    {
                        throw new InvalidOperationException("The audio-deleted fixture could not be prepared.");
                    }
                }
            }
        }
        var queue = new TranscriptionQueue(new FixtureTranscriber(args[1] == "running job", controlledWorker ? root : null),
            controlledWorker ? new FixtureTranscriptWriter(root, writer) : writer, repository,
            models: models, history: history);
        var recorder = new RecorderApplication(engine, queue, models, history);
        var playback = new WindowsRecordingPlayback(() => new FixturePlaybackOutput());
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
            deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (id, token) =>
                dispatcher.InvokeAsync(() =>
                {
                    if (playback.RecordingId == id)
                    {
                        playback.Close();
                    }
                }, System.Windows.Threading.DispatcherPriority.Normal, token).Task);
        var viewModel = new MainViewModel(recorder, engine, queue, models, history,
            new RecoveryManager(new LocalRecoveryStorage(Path.Combine(root, "capture"))),
            new FixtureDevices(), NullLogger<MainViewModel>.Instance,
            library,
            playback,
            realAnalysis ? new LocalTranscriptAnalysis(
                Environment.GetEnvironmentVariable("SOUNDRECORDER_ANALYSIS_MODELS") ?? "",
                Path.Combine(root, "experimental-analysis")) : new FixtureTranscriptAnalysis())
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
            new FixtureEnvironment(root), NullLogger<MainWindow>.Instance);
        application.MainWindow = window;
        try
        {
            application.Run(window);
        }
        finally
        {
            heldArtifact?.Dispose();
        }
    }

    private static string CreatePackage(string root, string name, bool invalidHash, bool englishOnly = false)
    {
        var path = Path.Combine(root, name);
        var content = Encoding.UTF8.GetBytes("Deterministic UI fixture; not a native model.");
        var package = new TranscriptionModelPackage(1, "ui-fixture", "Whisper.net 1.9", "model.bin",
            invalidHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(content)),
            "Windows11-x64", englishOnly ? ["English"] : ["English", "Brazilian Portuguese"]);
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

internal sealed class FixtureTranscriptAnalysis : ILocalTranscriptAnalysis
{
    public async Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken token)
    {
        progress.Report("Controlled UI fixture analysis — no real inference");
        if (request.Question == "UI fixture wait") await Task.Delay(Timeout.Infinite, token);
        if (request.Question == "UI fixture failure") throw new IOException("Controlled unavailable cache fixture.");
        await Task.Delay(150, token);
        var original = await File.ReadAllTextAsync(request.Outputs.TextPath, token);
        return new("Completed", "UI fixture only — not model evidence.", "",
            [new("UI fixture candidate — no real inference.",
                [new($"{request.AttemptId:N} · original fixture", 0, original)])]);
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
            RecordingOutcome.Complete, CapturedDuration) { Title = request.Title });
    }
}

internal sealed class OfflineHandler(string root) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        File.WriteAllText(Path.Combine(root, "network-requested.txt"), "requested");
        throw new InvalidOperationException("Network access is disabled in the UI test host.");
    }
}

internal sealed class FixturePlaybackOutput : NAudio.Wave.IWavePlayer, NAudio.Wave.IWavePosition
{
    private readonly Stopwatch _clock = new();
    private double _seconds;
    public float Volume { get; set; }
    public NAudio.Wave.PlaybackState PlaybackState { get; private set; }
    public NAudio.Wave.WaveFormat OutputWaveFormat { get; private set; } = new(48_000, 16, 1);
    public event EventHandler<NAudio.Wave.StoppedEventArgs>? PlaybackStopped;
    public void Init(NAudio.Wave.IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
    public void Play() { PlaybackState = NAudio.Wave.PlaybackState.Playing; _clock.Start(); }
    public void Pause()
    {
        _seconds += _clock.Elapsed.TotalSeconds;
        _clock.Reset();
        PlaybackState = NAudio.Wave.PlaybackState.Paused;
    }
    public void Stop()
    {
        Pause();
        PlaybackState = NAudio.Wave.PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new());
    }
    public long GetPosition() => (long)((_seconds + _clock.Elapsed.TotalSeconds) * OutputWaveFormat.AverageBytesPerSecond);
    public void Dispose() => Stop();
}

internal sealed class FixtureDevices : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceDescriptor> GetMicrophones() => [new("fixture-mic", "Fixture microphone")];
    public IReadOnlyList<AudioDeviceDescriptor> GetPlaybackDevices() => [new("fixture-playback", "Fixture playback")];
}

internal sealed class FixtureTranscriber(bool block, string? controlRoot = null) : IJobTranscriber
{
    public async Task<TranscriptDocument> TranscribeAsync(
        TranscriptionRequest request, IProgress<TranscriptionProgress>? progress, CancellationToken token)
    {
        if (controlRoot is null)
        {
            return await TranscribeAsync(request.Recording, token);
        }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var commandPath = Path.Combine(controlRoot, "worker-command.json");
                if (File.Exists(commandPath))
                {
                    var command = JsonSerializer.Deserialize<FixtureWorkerCommand>(await File.ReadAllTextAsync(commandPath, token)) ??
                        throw new InvalidOperationException("The fixture worker command is empty.");
                    File.Delete(commandPath);
                    if (command.Action == "Complete")
                    {
                        return Document(request.Recording);
                    }
                    if (!Enum.TryParse<TranscriptionPhase>(command.Action, out var phase))
                    {
                        throw new InvalidOperationException("Unknown fixture worker command.");
                    }
                    progress?.Report(new(phase, command.Percent));
                }
                await Task.Delay(25, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            File.WriteAllText(Path.Combine(controlRoot, "worker-cancel-pending.txt"), "pending");
            while (!File.Exists(Path.Combine(controlRoot, "worker-release.txt")))
            {
                await Task.Delay(25);
            }
            throw;
        }
    }

    public async Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token)
    {
        if (block)
        {
            await Task.Delay(Timeout.Infinite, token);
        }

        return Document(recording);
    }

    public static TranscriptDocument Document(SavedRecording recording, bool extended = false) =>
        new(recording, recording.Language,
            extended
                ? [new(TimeSpan.Zero, TimeSpan.FromSeconds(3),
                       "UI fixture transcript. This saved transcript stays readable while its recording is played. " +
                       "The recording timeline excludes paused capture intervals and playback does not start new transcription work."),
                   new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6),
                       "The next section remains available while the player stays visible above the independently scrolling reader.")]
                : [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "UI fixture transcript.")]);
}

internal sealed record FixtureWorkerCommand(string Action, int? Percent = null);

internal sealed class FixtureTranscriptWriter(string root, ITranscriptWriter writer) : ITranscriptWriter
{
    public async Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken token)
    {
        while (!File.Exists(Path.Combine(root, "worker-publish.txt")))
        {
            await Task.Delay(25, token);
        }
        return await writer.WriteAsync(document, token);
    }
}

internal sealed class FixtureEnvironment : IDesktopEnvironment
{
    private readonly string _root;
    private readonly Window _notification = new()
    {
        Title = "Fixture notification area",
        Width = 260,
        Height = 120,
    };

    public FixtureEnvironment(string root)
    {
        _root = root;
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
    public void CopyTranscript(string text) =>
        File.WriteAllText(Path.Combine(_root, "clipboard-transcript.txt"), text);
    public void OpenRecordingFolder(string audioPath) =>
        File.WriteAllText(Path.Combine(_root, "revealed-recording-path.txt"), audioPath);
    public void Dispose() => _notification.Close();
}
