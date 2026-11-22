using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reqnroll;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Review recordings and manage their owned artifacts")]
public sealed class RecordingLibraryDeletionSteps
{
    private readonly LibraryDeletionFixture _fixture = new();
    private IRecordingLibrary _library;
    private LibraryDeletionResult? _result;
    private Dictionary<string, byte[]> _outputBytes = [];
    private readonly Dictionary<string, byte[]> _protectedFiles = [];
    private FileStream? _lockedText;
    private FileStream? _playbackHandle;
    private FileStream? _capture;
    private ControlledDeletionTranscriber? _worker;
    private TranscriptionQueue? _queue;
    private HttpClient? _modelClient;
    private bool _playbackReleased;

    public RecordingLibraryDeletionSteps() => _library = _fixture.CreateLibrary();

    [Given("a disposable recording has two completed transcription jobs and two unique TXT and SRT pairs")]
    [Given("a disposable recording has two unique TXT and SRT pairs")]
    public async Task GivenTwoAttempts()
    {
        await _fixture.InitializeAsync(2);
        _outputBytes = await _fixture.OutputBytesAsync();
    }

    [Given("a disposable recording has a completed transcription job with a TXT and SRT pair")]
    [Given("a disposable recording has a WAV and a completed TXT and SRT pair")]
    public async Task GivenOneAttempt()
    {
        await _fixture.InitializeAsync();
        _outputBytes = await _fixture.OutputBytesAsync();
    }

    [Given("another recording and an unrelated file share its output folder")]
    public async Task GivenSharedFolder()
    {
        var neighbor = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "neighbor.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        await _fixture.History.AddAsync(neighbor, default);
        foreach (var (path, text) in new[]
        {
            (neighbor.AudioPath, "Unrelated recording bytes"),
            (Path.Combine(_fixture.Root, "unrelated.txt"), "Unrelated document bytes"),
        })
        {
            await File.WriteAllTextAsync(path, text);
            _protectedFiles[path] = await File.ReadAllBytesAsync(path);
        }
    }

    [Given("a downloaded model is available")]
    public async Task GivenVerifiedLocalModel()
    {
        var bytes = Encoding.UTF8.GetBytes("ggml disposable integrity fixture");
        var manifest = new TranscriptionModelPackage(1, "deletion-fixture-model", "Whisper.net 1.9",
            "model.bin", Convert.ToHexString(SHA256.HashData(bytes)), "Windows11-x64",
            ["English", "Brazilian Portuguese"]);
        var package = Path.Combine(_fixture.Root, "model-package.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write(JsonSerializer.Serialize(manifest));
            using var model = archive.CreateEntry("model.bin").Open();
            model.Write(bytes);
        }
        _modelClient = new(new NoNetworkHandler());
        var manager = new LocalTranscriptionModelManager(Path.Combine(_fixture.Root, "models"), _modelClient);
        await manager.ImportAsync(package, default);
        Assert.NotNull(manager.GetModelPath("English"));
        foreach (var path in Directory.GetFiles(Path.Combine(_fixture.Root, "models"), "*", SearchOption.AllDirectories))
            _protectedFiles[path] = await File.ReadAllBytesAsync(path);
    }

    [When("the user confirms deletion of only its audio")]
    [When("the user confirms deletion of the saved recording's audio")]
    [When("the user confirms deletion of the recording's audio")]
    public Task WhenDeleteAudio() => DeleteAsync(RecordingDeletionScope.AudioOnly);

    [When("the user confirms deletion of the recording and all its artifacts")]
    [When("the user confirms deletion of all its artifacts")]
    [When("deletion of all recording artifacts is requested")]
    public Task WhenDeleteAll() => DeleteAsync(RecordingDeletionScope.AllArtifacts);

    private async Task DeleteAsync(RecordingDeletionScope scope)
    {
        var deletion = _library.DeleteRecordingAsync(_fixture.Recording.Id, scope, default);
        if (_worker is not null)
        {
            await _worker.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(deletion.IsCompleted);
            Assert.True(File.Exists(_fixture.Recording.AudioPath));
            _worker.Release.TrySetResult();
        }
        _result = await deletion;
        if (_queue is not null)
            await _queue.WaitForIdleAsync(default);
    }

    [Then("the WAV is removed")]
    public void ThenAudioRemoved()
    {
        Assert.Equal(LibraryDeletionStatus.Completed, _result!.Status);
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
    }

    [Then("both TXT and SRT pairs and their content remain unchanged")]
    public async Task ThenOutputsUnchanged()
    {
        foreach (var (path, bytes) in _outputBytes)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Then("the library entry and both job-history entries remain")]
    public async Task ThenHistoryRetained()
    {
        var entry = Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.Equal(_fixture.Recording.Id, entry.Id);
        Assert.Equal(2, (await _fixture.Jobs.LoadAsync(default)).Count);
        Assert.Equal(_fixture.Outputs, entry.TranscriptOutputs);
    }

    [Then("playback and requests for new transcription are unavailable for that entry")]
    public async Task ThenAudioUnavailable()
    {
        var entry = Assert.Single(await _library.LoadAsync(default));
        Assert.True(entry.Recording.AudioDeleted);
        Assert.Equal(AudioAvailability.Deleted, entry.AudioAvailability);
        var queue = new TranscriptionQueue(new ControlledDeletionTranscriber(), new AtomicTranscriptWriter(),
            _fixture.Jobs, history: _fixture.History);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(_fixture.Recording, default));
    }

    [Then("only that recording's WAV and both TXT and SRT pairs are removed")]
    [Then("the WAV and that job's TXT and SRT pair are removed")]
    public void ThenOwnedArtifactsRemoved()
    {
        Assert.True(_result!.Status == LibraryDeletionStatus.Completed,
            JsonSerializer.Serialize(_result) + Environment.NewLine + string.Join(Environment.NewLine, _fixture.Logger.Entries.Select(entry => entry.Message)));
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        foreach (var output in _fixture.Outputs)
        {
            Assert.False(File.Exists(output.Outputs.TextPath));
            Assert.False(File.Exists(output.Outputs.SubtitlePath));
        }
    }

    [Then("its library and job-history entries are removed")]
    public async Task ThenTargetHistoryRemoved()
    {
        Assert.DoesNotContain(await _fixture.History.LoadAsync(default), recording => recording.Id == _fixture.Recording.Id);
        Assert.DoesNotContain(await _fixture.Jobs.LoadAsync(default), job => job.Recording.Id == _fixture.Recording.Id);
    }

    [Then("the output folder, unrelated file, other recording, and downloaded model remain unchanged")]
    public async Task ThenProtectedArtifactsUnchanged()
    {
        Assert.True(Directory.Exists(_fixture.Root));
        Assert.NotEmpty(_protectedFiles);
        foreach (var (path, bytes) in _protectedFiles)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(await _fixture.History.LoadAsync(default));
    }

    [When("the user deletes only that job's history")]
    public async Task WhenDeleteJob()
    {
        _result = await _library.DeleteJobHistoryAsync(_fixture.Outputs[0].JobId, default);
        Assert.Equal(LibraryDeletionStatus.Completed, _result.Status);
        Assert.Empty(await _fixture.Jobs.LoadAsync(default));
    }

    [Then("its completed outputs remain associated with the library entry")]
    public async Task ThenOwnershipRetained()
    {
        Assert.Equal(_fixture.Outputs, Assert.Single(await _fixture.History.LoadAsync(default)).TranscriptOutputs);
        await ThenOutputsUnchanged();
        var transcript = await _library.ReadTranscriptAsync(_fixture.Recording.Id, _fixture.Outputs[0].JobId, default);
        Assert.Equal("Transcript attempt 1", transcript.Text);
    }

    [Then("no orphan output from the deleted job history remains")]
    public async Task ThenNoOrphan()
    {
        ThenOwnedArtifactsRemoved();
        await ThenTargetHistoryRemoved();
    }

    [Given("a recording is being played and transcribed")]
    public async Task GivenRelatedWork()
    {
        await GivenOneAttempt();
        _worker = new ControlledDeletionTranscriber();
        _queue = new(_worker, new AtomicTranscriptWriter(), _fixture.Jobs, history: _fixture.History);
        await _queue.RestoreAsync(default);
        await _queue.EnqueueAsync(_fixture.Recording, default);
        await _worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _queue.EnqueueAsync(_fixture.Recording, default);
        _playbackHandle = File.Open(_fixture.Recording.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _library = _fixture.CreateLibrary(cancel: async (id, token) =>
        {
            await _queue.CancelRecordingAsync(id, token);
            Assert.True(_worker.FileReleased);
            Assert.True(File.Exists(_fixture.Recording.AudioPath));
        }, release: (id, _) =>
        {
            Assert.Equal(_fixture.Recording.Id, id);
            Assert.True(_worker.FileReleased);
            Assert.True(File.Exists(_fixture.Recording.AudioPath));
            _playbackHandle.Dispose();
            _playbackReleased = true;
            return Task.CompletedTask;
        }, deleteJob: _queue.DeleteJobAsync);
    }

    [Given("another recording session is capturing audio")]
    public void GivenUnrelatedCapture()
    {
        _capture = File.Open(Path.Combine(_fixture.Root, "unrelated-capture.wav"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _capture.Write([1, 2, 3]);
    }

    [Then("related transcription is cancelled and its worker is released before file removal")]
    public void ThenWorkerReleased()
    {
        ThenAudioRemoved();
        Assert.True(_worker!.FileReleased);
        Assert.Equal(TranscriptionJobStatus.Canceled, _queue!.Jobs[1].Status);
        Assert.Equal(TranscriptionJobStatus.Canceled, _queue.Jobs[2].Status);
    }

    [Then("the playback file handle is closed before file removal")]
    public void ThenPlaybackReleased() => Assert.True(_playbackReleased);

    [Then("the unrelated recording session keeps capturing")]
    public void ThenUnrelatedCaptureContinues()
    {
        _capture!.Write([4, 5, 6]);
        _capture.Flush();
        Assert.Equal(6, _capture.Length);
    }

    [Then("late progress or results from the cancelled attempt do not recreate artifacts")]
    public void ThenNoLateOutputs()
    {
        Assert.False(File.Exists(_fixture.Recording.AudioPath));
        Assert.False(File.Exists(Path.Combine(_fixture.Root, "meeting.transcript-2.txt")));
        Assert.False(File.Exists(Path.Combine(_fixture.Root, "meeting.transcript-2.srt")));
    }

    [Given("a related transcription worker cannot be released within the existing worker-release deadline")]
    public async Task GivenWorkerReleaseFailure()
    {
        await GivenOneAttempt();
        _library = _fixture.CreateLibrary(cancel: (_, _) =>
            Task.FromException(new TimeoutException("Controlled worker-release deadline failure")));
    }

    [Then("deletion reports an actionable worker-release failure")]
    public void ThenWorkerFailure()
    {
        var issue = Assert.Single(_result!.Issues);
        Assert.Equal(LibraryFailureCode.WorkerReleaseFailed, issue.Code);
        Assert.Contains("retry", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Then("no recording artifact or library entry is removed")]
    public async Task ThenNothingRemoved()
    {
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
        Assert.Single(await _fixture.History.LoadAsync(default));
        await ThenOutputsUnchanged();
    }

    [Then("the operation is not reported as successful")]
    [Then("complete deletion is not reported")]
    [Then("a successful complete deletion is not reported")]
    public void ThenNotSuccessful() => Assert.NotEqual(LibraryDeletionStatus.Completed, _result!.Status);

    [Given("deleting the WAV succeeds but the TXT cannot be deleted")]
    public void GivenRestrictedText() =>
        _lockedText = File.Open(_fixture.Outputs[0].Outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Then("the library records the audio removal and the unfinished requested deletion")]
    public async Task ThenPartialStateStored()
    {
        var saved = Assert.Single(await _fixture.History.LoadAsync(default));
        Assert.True(saved.AudioDeleted);
        Assert.NotNull(saved.PendingDeletion);
        Assert.Equal(RecordingDeletionScope.AllArtifacts, saved.PendingDeletion.Scope);
        Assert.Equal(ArtifactDeletionState.Removed, saved.PendingDeletion.Artifacts[0].State);
        Assert.Equal(ArtifactDeletionState.Failed, saved.PendingDeletion.Artifacts[1].State);
        Assert.Single(await _fixture.Jobs.LoadAsync(default));
    }

    [Then("the remaining failed artifact is identified with an actionable error")]
    public void ThenFailedArtifactIdentified()
    {
        var issue = Assert.Single(_result!.Issues);
        Assert.Equal(_fixture.Outputs[0].Outputs.TextPath, issue.Path);
        Assert.Equal(LibraryFailureCode.ArtifactDeleteFailed, issue.Code);
        Assert.Contains("retry", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [When("the file restriction is removed and the user retries that deletion")]
    public async Task WhenRetry()
    {
        _lockedText!.Dispose();
        _lockedText = null;
        _library = _fixture.CreateLibrary(history: new JsonRecordingHistory(_fixture.HistoryPath));
        await DeleteAsync(RecordingDeletionScope.AllArtifacts);
    }

    [Then("the remaining owned artifacts and associated history are removed")]
    public async Task ThenRemainingRemoved()
    {
        ThenOwnedArtifactsRemoved();
        await ThenTargetHistoryRemoved();
    }

    [Then("already removed audio is not recreated")]
    public void ThenNoAudioRecreation() => Assert.False(File.Exists(_fixture.Recording.AudioPath));

    [Given("a recording's artifact metadata includes an unrelated file reference")]
    public async Task GivenForeignReference()
    {
        await GivenOneAttempt();
        var pair = new TranscriptOutputs(Path.Combine(_fixture.Root, "unrelated.txt"), Path.Combine(_fixture.Root, "unrelated.srt"));
        await File.WriteAllTextAsync(pair.TextPath, "Unrelated protected content");
        await File.WriteAllTextAsync(pair.SubtitlePath, "Unrelated protected subtitles");
        await _fixture.History.RegisterOutputsAsync(_fixture.Recording.Id, new(Guid.NewGuid(), pair), default);
        _protectedFiles[pair.TextPath] = await File.ReadAllBytesAsync(pair.TextPath);
    }

    [Then("ownership validation reports an actionable failure")]
    public void ThenOwnershipFailure()
    {
        Assert.Equal(LibraryDeletionStatus.Rejected, _result!.Status);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(_result.Issues).Code);
        Assert.Empty(_fixture.CancelledRecordings);
        Assert.Empty(_fixture.DeletedJobs);
    }

    [Then("the unrelated file is not removed")]
    public async Task ThenForeignFilePreserved()
    {
        foreach (var (path, bytes) in _protectedFiles)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    [AfterScenario]
    public async Task Cleanup()
    {
        _worker?.Release.TrySetResult();
        if (_queue is not null)
            await _queue.WaitForIdleAsync(default);
        _lockedText?.Dispose();
        _playbackHandle?.Dispose();
        _capture?.Dispose();
        _modelClient?.Dispose();
        _fixture.Dispose();
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No network request is permitted.");
    }
}
