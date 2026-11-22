using Reqnroll;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;
using NAudio.Wave;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Choose verified downloaded models and explicitly download other models")]
public sealed class ModelCatalogSteps : IDisposable
{
    private readonly ControlledModelDownload _handler = new();
    private readonly ModelTestWorkspace _workspace;
    private IReadOnlyList<ModelCatalogEntry> _catalog = [];
    private readonly CancellationTokenSource _downloadCancellation = new();
    private readonly TaskCompletionSource _transferred = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _continueTransfer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _verifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _continueVerification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ModelDownloadProgress> _progress = [];
    private Task<DownloadedTranscriptionModel>? _download;
    private ModelDownloadProgress? _observation;
    private Exception? _failure;
    private string? _removedPath;
    private string? _independentDirectory;
    private string? _selectedId;
    private RecorderApplication? _application;
    private CatalogRecordingEngine? _engine;
    private readonly CatalogQueue _queue = new();
    private SavedRecording? _recording;
    private int _networkBaseline;

    public ModelCatalogSteps() => _workspace = new ModelTestWorkspace(_handler);

    [When("the user browses the built-in model download catalog")]
    public void BrowseCatalog() => _catalog = TranscriptionModelCatalog.Entries;

    [Then("it contains exactly these model families and language capabilities")]
    public void AssertFamilies(Table table)
    {
        Assert.Equal(table.Rows.Select(row => row["model"]).Order(),
            _catalog.Select(entry => entry.Family).Distinct().Order());
        foreach (var row in table.Rows)
        {
            var expected = row["language capability"] == "English only"
                ? new[] { "English" } : ["English", "Brazilian Portuguese"];
            Assert.All(_catalog.Where(entry => entry.Family == row["model"]),
                entry => Assert.Equal(expected, entry.Languages));
        }
    }

    [Then("each family offers exactly these separate downloadable encodings")]
    public void AssertEncodings(Table table)
    {
        var expected = table.Rows.Select(row => row["encoding"]).Order();
        foreach (var family in _catalog.GroupBy(entry => entry.Family))
            Assert.Equal(expected, family.Select(entry => entry.Encoding.ToString()).Order());
    }

    [Then("the catalog offers 72 distinct model artifacts")]
    public void AssertArtifactCount()
    {
        Assert.Equal(72, _catalog.Count);
        Assert.Equal(72, _catalog.Select(entry => entry.Key).Distinct().Count());
        Assert.Equal(72, _catalog.Select(entry => entry.DownloadUri).Distinct().Count());
        var entry = TranscriptionModelCatalog.Find("whisper.net:base:Q5_1");
        Assert.NotNull(entry);
        Assert.Equal("https://huggingface.co/sandrohanea/whisper.net/resolve/v4/q5_1/ggml-base.bin",
            entry.DownloadUri.AbsoluteUri);
        var encodings = new Dictionary<string, (string Directory, string Label)>
        {
            ["NoQuantization"] = ("classic", "original"),
            ["Q4_0"] = ("q4_0", "Q4_0"), ["Q4_1"] = ("q4_1", "Q4_1"),
            ["Q5_0"] = ("q5_0", "Q5_0"), ["Q5_1"] = ("q5_1", "Q5_1"),
            ["Q8_0"] = ("q8_0", "Q8_0")
        };
        foreach (var artifact in _catalog)
        {
            var expected = encodings[artifact.Encoding.ToString()];
            Assert.Equal($"whisper.net:{artifact.Family}:{artifact.Encoding}", artifact.Key);
            Assert.Equal($"{artifact.Family} {expected.Label}", artifact.DisplayName);
            Assert.Equal(
                $"https://huggingface.co/sandrohanea/whisper.net/resolve/v4/{expected.Directory}/ggml-{artifact.Family}.bin",
                artifact.DownloadUri.AbsoluteUri);
        }
    }

    [Then("VAD models and platform encoder bundles are not transcription-model choices")]
    public void AssertNoAuxiliaries() =>
        Assert.All(_catalog, entry =>
        {
            Assert.DoesNotContain("vad", entry.DownloadUri.AbsoluteUri);
            Assert.DoesNotContain("encoder", entry.DownloadUri.AbsoluteUri);
            Assert.EndsWith(".bin", entry.DownloadUri.AbsoluteUri);
        });

    [Then("browsing the catalog makes no network request")]
    public void AssertOfflineCatalog() => Assert.Empty(_handler.Requests);

    [Given("a verified downloaded Base original model exists")]
    public async Task GivenBase() => await Register("base");

    [Given("a Small model is still downloading")]
    public async Task GivenSmallDownloading()
    {
        _handler.Entry = Entry("small");
        await BeginDownload();
    }

    [Given("a Medium model failed integrity verification")]
    public async Task GivenMediumFailed()
    {
        _failure = await Record.ExceptionAsync(() => _workspace.Manager.RegisterAsync(
            _workspace.Package("medium-failed", invalidHash: true), default));
        Assert.IsType<InvalidDataException>(_failure);
        Assert.Contains("SHA-256", _failure.Message);
    }

    [When("the user opens downloaded model selection")]
    public void OpenSelection() => Assert.NotNull(_workspace.Manager.DownloadedModels);

    [Then("only the Base original model is selectable")]
    public void AssertOnlyBase() =>
        Assert.Equal(Entry("base").Key, Assert.Single(_workspace.Manager.DownloadedModels).Id);

    [Then("incomplete or unverified artifacts are not offered as ready models")]
    public void AssertIncompleteExcluded()
    {
        Assert.DoesNotContain(_workspace.Manager.DownloadedModels, model => model.Id == Entry("small").Key);
        Assert.DoesNotContain(_workspace.Manager.DownloadedModels, model => model.Id == Entry("medium").Key);
    }

    [Given("Base original and Base Q5_1 are both verified and downloaded")]
    public async Task GivenTwoBaseEncodings()
    {
        await Register("base");
        await Register("base", "Q5_1");
    }

    [When("the user selects Base Q5_1")]
    public async Task SelectBaseQuantized() =>
        await _workspace.Manager.SelectAsync(Entry("base", "Q5_1").Key, default);

    [Then("new transcription requests use the Base Q5_1 artifact identity")]
    public void AssertPinnedIdentity()
    {
        Assert.Equal(Entry("base", "Q5_1").Key, _workspace.Manager.SelectedModelId);
        var choice = Assert.Single(_workspace.Manager.DownloadedModels,
            model => model.Id == _workspace.Manager.SelectedModelId);
        Assert.Equal(choice.ModelPath,
            _workspace.Manager.GetModelPath("English", _workspace.Manager.SelectedModelId));
    }

    [Then("the Base original artifact remains available")]
    public void AssertBaseRetained() =>
        Assert.NotNull(_workspace.Manager.GetModelPath("English", Entry("base").Key));

    [Given("the selected model is a verified Tiny model")]
    public async Task GivenTinySelected() => await RegisterAndSelect("tiny");

    [Given("an explicitly requested model download has a total length of 4096 bytes")]
    public void GivenKnownLength() => _handler.KnownLength = true;

    [When("1024 bytes have been transferred")]
    [When("bytes are transferred")]
    public async Task TransferBytes()
    {
        await BeginDownload();
        _observation = _progress.Last(value => value.Phase == ModelDownloadPhase.Downloading);
        Assert.Equal(1024, _observation.BytesTransferred);
    }

    [Then("download progress reports 25 percent")]
    public void AssertQuarter() => Assert.Equal(25d, _observation!.Percent);

    [Then("the downloading model is not selectable")]
    [Then("the model is still not selectable")]
    [Then("its incomplete artifact is not selectable")]
    public void AssertDownloadNotSelectable() =>
        Assert.DoesNotContain(_workspace.Manager.DownloadedModels, model => model.Id == _handler.Entry.Key);

    [When("all bytes have arrived but verification is unfinished")]
    public async Task ReachVerification()
    {
        _continueTransfer.TrySetResult();
        await _verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_download!.IsCompleted);
        Assert.Equal(4096, _progress[^1].BytesTransferred);
        Assert.Equal(ModelDownloadPhase.Verifying, _progress[^1].Phase);
    }

    [When("compatibility and expected SHA-256 verification succeed")]
    public async Task CompleteVerification()
    {
        _continueVerification.TrySetResult();
        await _download!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Then("the new model becomes a downloaded choice")]
    public void AssertRegistered() =>
        Assert.Contains(_workspace.Manager.DownloadedModels, model => model.Id == _handler.Entry.Key);

    [Then("Tiny remains the selected model until the user explicitly changes selection")]
    public void AssertTinyStillSelected() =>
        Assert.Equal(Entry("tiny").Key, _workspace.Manager.SelectedModelId);

    [Given("an explicitly requested model download has no known total byte count")]
    public void GivenUnknownLength() => _handler.KnownLength = false;

    [Then("transferred bytes are observable")]
    public void AssertTransferredBytes() => Assert.True(_observation!.BytesTransferred > 0);

    [Then("a total percentage is unavailable rather than fabricated")]
    public void AssertIndeterminate()
    {
        Assert.Null(_observation!.TotalBytes);
        Assert.Null(_observation.Percent);
    }

    [Then("completion still requires compatibility and integrity verification")]
    public async Task AssertVerificationRequired()
    {
        await ReachVerification();
        AssertDownloadNotSelectable();
        await CompleteVerification();
        AssertRegistered();
        Assert.All(_progress, value => Assert.Null(value.Percent));
    }

    [Given("a verified downloaded Base model is selected")]
    public async Task GivenBaseSelected() => await RegisterAndSelect("base");

    [When("an explicitly requested download encounters {string}")]
    public async Task FailDownload(string failure)
    {
        _handler.Entry = Entry("small");
        _handler.Interrupted = failure == "interrupted transfer";
        _handler.HashMismatch = failure == "SHA-256 mismatch";
        if (failure == "incompatible model")
            _handler.Bytes[0] = 0;
        _independentDirectory = Path.Combine(_workspace.ModelsDirectory, ".staging-independent");
        Directory.CreateDirectory(_independentDirectory);
        await File.WriteAllTextAsync(Path.Combine(_independentDirectory, "owned-by-someone-else.txt"), "keep");
        _failure = await Record.ExceptionAsync(() => _workspace.Manager.DownloadAsync(
            _handler.Entry, new InlineModelProgress(_ => { }), default));
    }

    [Then("the failure is reported with a recovery action")]
    public void AssertRecoveryAction()
    {
        Assert.NotNull(_failure);
        Assert.Contains("retry", _failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the incomplete artifact is not registered as a downloaded model")]
    public void AssertFailureExcluded() => AssertDownloadNotSelectable();

    [Then("Base remains selectable and selected")]
    public void AssertBaseSelected()
    {
        Assert.Equal(Entry("base").Key, _workspace.Manager.SelectedModelId);
        Assert.NotNull(_workspace.Manager.GetModelPath("English"));
    }

    [Then("only owned download staging files are cleaned")]
    public void AssertOwnedCleanup()
    {
        Assert.Equal(new[] { _independentDirectory! }, Directory.GetDirectories(_workspace.ModelsDirectory, ".staging-*"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_independentDirectory!, "owned-by-someone-else.txt")));
    }

    [Given("a verified downloaded model is selected")]
    [Given("a verified downloaded model was selected")]
    public async Task GivenSelectedModel()
    {
        await RegisterAndSelect("base");
        await Register("small", "Q5_1");
    }

    [Given("capture and a model download are active")]
    public async Task GivenCaptureAndDownload()
    {
        await StartCapture("English");
        _handler.Entry = Entry("medium");
        await BeginDownload();
        Assert.Equal(RecordingStatus.Recording, _application!.Status);
    }

    [When("the user cancels the model download")]
    public async Task CancelDownload()
    {
        _downloadCancellation.Cancel();
        _failure = await Record.ExceptionAsync(() => _download!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(_failure);
    }

    [Then("only that download is cancelled")]
    [Then("capture and the selected model remain unchanged")]
    public void AssertCaptureUnaffected()
    {
        Assert.Equal(RecordingStatus.Recording, _application!.Status);
        Assert.Equal(_selectedId, _workspace.Manager.SelectedModelId);
        Assert.False(_engine!.CaptureCancellation.IsCancellationRequested);
        Assert.Empty(Directory.GetDirectories(_workspace.ModelsDirectory, ".staging-*"));
    }

    [Given("Base original and Small Q5_1 have been verified and downloaded")]
    public async Task GivenBaseAndSmall()
    {
        await Register("base");
        await Register("small", "Q5_1");
    }

    [Given("the network is disconnected")]
    public void DisconnectNetwork()
    {
        _networkBaseline = _handler.Requests.Count;
        _handler.RejectRequests = true;
    }

    [When("the user selects Small Q5_1 and restarts the app")]
    public async Task SelectSmallAndRestart()
    {
        await _workspace.Manager.SelectAsync(Entry("small", "Q5_1").Key, default);
        _workspace.Reload();
    }

    [Then("Small Q5_1 remains the selected model")]
    public void AssertSmallSelected() =>
        Assert.Equal(Entry("small", "Q5_1").Key, _workspace.Manager.SelectedModelId);

    [Then("both downloaded artifacts remain available")]
    public void AssertTwoChoices() => Assert.Equal(2, _workspace.Manager.DownloadedModels.Count);

    [Then("no network request is required")]
    public void AssertNoNetwork() => Assert.Equal(_networkBaseline, _handler.Requests.Count);

    [Given("a downloaded Tiny.en model is selected")]
    public async Task GivenEnglishOnly() => await RegisterAndSelect("tiny.en");

    [When("a Brazilian Portuguese recording is saved")]
    public async Task SavePortuguese()
    {
        await StartCapture("Brazilian Portuguese");
        _recording = await _application!.StopAsync();
        Assert.NotNull(_recording);
    }

    [Then("its audio is retained without a new transcription job")]
    public void AssertAudioRetained()
    {
        Assert.True(File.Exists(_recording!.AudioPath));
        using var reader = new WaveFileReader(_recording.AudioPath);
        Assert.True(reader.Length > 0);
        Assert.Empty(_queue.Recordings);
    }

    [Then("the user is told to choose a compatible downloaded model")]
    public void AssertCompatibilityNotice() =>
        Assert.Contains("compatible transcription model", _application!.TranscriptionNotice);

    [Then("Tiny.en remains a valid English-only downloaded choice")]
    public void AssertEnglishOnly()
    {
        var choice = Assert.Single(_workspace.Manager.DownloadedModels);
        Assert.Equal(Entry("tiny.en").Key, choice.Id);
        Assert.Equal(["English"], choice.Languages);
        Assert.NotNull(_workspace.Manager.GetModelPath("English"));
        Assert.Null(_workspace.Manager.GetModelPath("Brazilian Portuguese"));
    }

    [Then("no multilingual model is selected silently")]
    [Then("another downloaded model is not selected silently")]
    public void AssertNoSilentSelection() => Assert.Equal(_selectedId, _workspace.Manager.SelectedModelId);

    [Given("its model file is externally removed")]
    public void RemoveSelectedFile()
    {
        _removedPath = _workspace.Manager.GetModelPath("English");
        File.Delete(_removedPath!);
    }

    [When("the app reloads the model inventory")]
    public async Task ReloadInventory()
    {
        _failure = await Record.ExceptionAsync(() => _workspace.Manager.RefreshInventoryAsync(default));
        _workspace.Reload();
    }

    [Then("that artifact is reported unavailable for use")]
    public void AssertUnavailable()
    {
        Assert.IsType<InvalidDataException>(_failure);
        Assert.Null(_workspace.Manager.GetModelPath("English"));
        Assert.Null(_workspace.Manager.GetModelPath("English", _selectedId));
        Assert.DoesNotContain(_workspace.Manager.DownloadedModels, model => model.ModelPath == _removedPath);
    }

    [Then("recording remains available")]
    public async Task AssertRecordingAvailable()
    {
        await StartCapture("English");
        Assert.Equal(RecordingStatus.Recording, _application!.Status);
        _recording = await _application.StopAsync();
        Assert.True(File.Exists(_recording!.AudioPath));
        Assert.Empty(_queue.Recordings);
    }

    private static ModelCatalogEntry Entry(string family, string encoding = "NoQuantization") =>
        TranscriptionModelCatalog.Find($"whisper.net:{family}:{encoding}")!;

    private async Task Register(string family, string encoding = "NoQuantization")
    {
        var entry = Entry(family, encoding);
        var previous = _handler.Entry;
        _handler.Entry = entry;
        try
        {
            await _workspace.Manager.DownloadAsync(entry, new InlineModelProgress(_ => { }), default);
        }
        finally
        {
            _handler.Entry = previous;
        }
    }

    private async Task RegisterAndSelect(string family)
    {
        await Register(family);
        _selectedId = Entry(family).Key;
        await _workspace.Manager.SelectAsync(_selectedId, default);
    }

    private async Task BeginDownload()
    {
        if (_download is null)
        {
            _handler.BeforeSecondChunk = () => _continueTransfer.Task;
            _download = Task.Run(() => _workspace.Manager.DownloadAsync(
                _handler.Entry, new InlineModelProgress(value =>
                {
                    _progress.Add(value);
                    if (value.Phase == ModelDownloadPhase.Downloading && value.BytesTransferred == 1024)
                        _transferred.TrySetResult();
                    if (value.Phase == ModelDownloadPhase.Verifying)
                    {
                        _verifying.TrySetResult();
                        _continueVerification.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    }
                }), _downloadCancellation.Token));
        }
        await _transferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task StartCapture(string language)
    {
        _engine = new CatalogRecordingEngine(
            Path.Combine(_workspace.ModelsDirectory, $"{Guid.NewGuid():N}.wav"), language);
        _application = new RecorderApplication(_engine, _queue, _workspace.Manager);
        await _application.StartAsync(new RecordingRequest(
            AudioSource.MicrophoneOnly, "controlled-microphone", null, _workspace.ModelsDirectory, null, language), true);
    }

    [AfterScenario]
    public async Task Cleanup()
    {
        _downloadCancellation.Cancel();
        _continueTransfer.TrySetResult();
        _continueVerification.TrySetResult();
        if (_download is not null)
        {
            try { await _download.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
        _engine?.Dispose();
    }

    public void Dispose()
    {
        _downloadCancellation.Dispose();
        _workspace.Dispose();
    }
}

internal sealed class CatalogQueue : ITranscriptionQueue
{
    public List<SavedRecording> Recordings { get; } = [];
    public Task EnqueueAsync(SavedRecording recording, CancellationToken cancellationToken)
    {
        Recordings.Add(recording);
        return Task.CompletedTask;
    }
}

internal sealed class CatalogRecordingEngine(string path, string language) : IRecordingEngine, IDisposable
{
    private WaveFileWriter? _writer;
    public CancellationTokenSource CaptureCancellation { get; } = new();
    public string? LastFailure => null;
    public Task StartAsync(RecordingRequest request, CancellationToken cancellationToken)
    {
        _writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1));
        _writer.Write(new byte[16000], 0, 16000);
        return Task.CompletedTask;
    }
    public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
    {
        _writer?.Dispose();
        _writer = null;
        return Task.FromResult(new SavedRecording(
            Guid.NewGuid(), path, language, RecordingOutcome.Complete, TimeSpan.FromSeconds(1)));
    }
    public void Dispose()
    {
        _writer?.Dispose();
        CaptureCancellation.Dispose();
    }
}
