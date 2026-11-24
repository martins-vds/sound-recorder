using System.Diagnostics.Tracing;
using System.Text.Json;
using Reqnroll;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Review recordings and manage their owned artifacts")]
public sealed class RecordingLibrarySteps
{
    private readonly StorageTestDirectory _directory = new();
    private readonly List<SavedRecording> _recordings = [];
    private readonly JsonRecordingHistory _history;
    private readonly JsonTranscriptionJobRepository _jobs;
    private readonly IRecordingLibrary _library;
    private IReadOnlyList<RecordingLibraryEntry> _results = [];
    private Dictionary<string, byte[]> _before = [];
    private int _networkRequests;
    private IReadOnlyList<TranscriptionJob> _originalJobs = [];
    private OwnedTranscriptOutputs? _owned;
    private PendingRecordingDeletion? _pending;

    public RecordingLibrarySteps()
    {
        _history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        _jobs = new JsonTranscriptionJobRepository(_directory.PathFor("jobs.json"));
        _library = new RecordingLibrary(_history, _jobs);
    }

    [Given("the library contains recordings {string} and {string}")]
    public async Task GivenRecordings(string first, string second)
    {
        foreach (var title in new[] { first, second })
        {
            var recording = new SavedRecording(Guid.NewGuid(), _directory.PathFor($"{title}.wav"),
                "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(8)) { Title = title };
            await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3, 4]);
            await _history.AddAsync(recording, default);
            _recordings.Add(recording);
        }
    }

    [Given("only the completed transcript for {string} contains {string}")]
    public async Task GivenCompletedContent(string title, string content)
    {
        var jobs = new List<TranscriptionJob>();
        foreach (var recording in _recordings)
        {
            var outputs = new TranscriptOutputs(Path.ChangeExtension(recording.AudioPath, ".txt"),
                Path.ChangeExtension(recording.AudioPath, ".srt"));
            await File.WriteAllTextAsync(outputs.TextPath, recording.Title == title ? content : "Ordinary transcript");
            await File.WriteAllTextAsync(outputs.SubtitlePath, "Saved subtitles");
            var job = new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UtcNow,
                TranscriptionJobStatus.Succeeded, outputs, null);
            jobs.Add(job);
            await _history.RegisterOutputsAsync(recording.Id, new(job.Id, outputs), default);
        }

        var failedOutputs = new TranscriptOutputs(_directory.PathFor($"{_recordings[0].Title}.transcript-2.txt"),
            _directory.PathFor($"{_recordings[0].Title}.transcript-2.srt"));
        await File.WriteAllTextAsync(failedOutputs.TextPath, content);
        jobs.Add(new(Guid.NewGuid(), _recordings[0], DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Failed, failedOutputs, "Publication failed"));
        await _jobs.SaveAsync(jobs, default);
        _before = await SnapshotAsync();
    }

    [When("the user searches the library for {string}")]
    public async Task WhenSearch(string query)
    {
        using var network = new LibraryNetworkObserver();
        var error = await Record.ExceptionAsync(async () => _results = await _library.SearchAsync(query, default));
        Assert.Null(error);
        _networkRequests = network.Requests;
    }

    [Then("only {string} is returned")]
    public void ThenOnlyRecording(string title) =>
        Assert.Equal(title, Assert.Single(_results).Recording.Title);

    [Then("recording files, transcript files, and job states remain unchanged")]
    public async Task ThenFilesUnchanged()
    {
        var after = await SnapshotAsync();
        Assert.Equal(_before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in _before)
        {
            Assert.Equal(bytes, after[path]);
        }
    }

    [Then("no network request is made")]
    public void ThenNoNetworkRequest() => Assert.Equal(0, _networkRequests);

    [Given("disposable legacy history contains a recording with completed job outputs")]
    public async Task GivenLegacyHistory()
    {
        var recording = new SavedRecording(Guid.NewGuid(), _directory.PathFor("legacy.wav"),
            "Brazilian Portuguese", RecordingOutcome.Recovered, TimeSpan.FromTicks(123456789));
        var legacy = new { recording.Id, recording.AudioPath, recording.Language, recording.Outcome, recording.CapturedDuration };
        await File.WriteAllTextAsync(_directory.PathFor("history.json"), JsonSerializer.Serialize(new[] { legacy }));
        _owned = new(Guid.NewGuid(), new(_directory.PathFor("legacy.txt"), _directory.PathFor("legacy.srt")))
        {
            Language = "Brazilian Portuguese"
        };
        var createdAt = DateTimeOffset.Parse("2026-03-04T05:06:07+02:00");
        var job = new
        {
            Id = _owned.JobId, Recording = legacy, Status = TranscriptionJobStatus.Succeeded,
            CreatedAt = createdAt, Outputs = _owned.Outputs, Error = (string?)null,
        };
        await File.WriteAllTextAsync(_directory.PathFor("jobs.json"),
            JsonSerializer.Serialize(new[] { job }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await File.WriteAllBytesAsync(recording.AudioPath, [5, 4, 3, 2, 1]);
        await File.WriteAllTextAsync(_owned.Outputs.TextPath, "Legacy transcript");
        await File.WriteAllTextAsync(_owned.Outputs.SubtitlePath, "Legacy subtitles");
        _recordings.Add(recording);
        _originalJobs = await _jobs.LoadAsync(default);
        _before = await SnapshotAsync();
    }

    [When("the app loads and saves the migrated library")]
    public async Task WhenMigrate()
    {
        _results = await _library.LoadAsync(default);
        await _history.UpdateAsync(Assert.Single(_results).Recording, default);
        _results = await new RecordingLibrary(new JsonRecordingHistory(_directory.PathFor("history.json")), _jobs)
            .LoadAsync(default);
    }

    [Then("the recording and job IDs, paths, language, captured duration, and statuses are preserved")]
    public async Task ThenLegacyMetadataPreserved()
    {
        var expected = Assert.Single(_recordings);
        var actual = Assert.Single(_results).Recording;
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.AudioPath, actual.AudioPath);
        Assert.Equal(expected.Language, actual.Language);
        Assert.Equal(expected.CapturedDuration, actual.CapturedDuration);
        Assert.Equal(expected.Outcome, actual.Outcome);
        var expectedJob = Assert.Single(_originalJobs);
        var actualJob = Assert.Single(await _jobs.LoadAsync(default));
        Assert.Equal(expectedJob.Id, actualJob.Id);
        Assert.Equal(expectedJob.CreatedAt, actualJob.CreatedAt);
        Assert.Equal(expectedJob.Status, actualJob.Status);
        Assert.Equal(expectedJob.Outputs, actualJob.Outputs);
        Assert.Equal(expectedJob.Error, actualJob.Error);
        Assert.Equal(expected, actualJob.Recording);
        Assert.Equal(_before[_directory.PathFor("jobs.json")], await File.ReadAllBytesAsync(_directory.PathFor("jobs.json")));
    }

    [Then("existing audio and output bytes remain unchanged")]
    public async Task ThenMediaBytesUnchanged()
    {
        foreach (var (path, bytes) in _before.Where(item => Path.GetExtension(item.Key) != ".json"))
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
    }

    [Then("the output pair is associated with its library entry")]
    public void ThenPairOwned() =>
        Assert.Equal(_owned, Assert.Single(Assert.Single(_results).Recording.TranscriptOutputs));

    [Then("no unknown historical model choice is fabricated")]
    public async Task ThenNoFabricatedModel()
    {
        Assert.DoesNotContain("model", await File.ReadAllTextAsync(_directory.PathFor("history.json")), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", await File.ReadAllTextAsync(_directory.PathFor("jobs.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Given("confirmed artifact deletion was interrupted after removing only the WAV")]
    public async Task GivenInterruptedDeletion()
    {
        var recording = new SavedRecording(Guid.NewGuid(), _directory.PathFor("pending.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(8));
        _owned = new(Guid.NewGuid(), new(_directory.PathFor("pending.txt"), _directory.PathFor("pending.srt")));
        _pending = new(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Text, _owned.Outputs.TextPath, _owned.JobId, ArtifactDeletionState.Pending, null),
            new(RecordingArtifactKind.Subtitle, _owned.Outputs.SubtitlePath, _owned.JobId, ArtifactDeletionState.Pending, null),
        ], [_owned.JobId], null);
        recording = recording with { AudioDeleted = true, TranscriptOutputs = [_owned], PendingDeletion = _pending };
        await _history.AddAsync(recording, default);
        await File.WriteAllTextAsync(_owned.Outputs.TextPath, "Pending retained transcript");
        await File.WriteAllTextAsync(_owned.Outputs.SubtitlePath, "Pending retained subtitles");
        _recordings.Add(recording);
        _before = await SnapshotAsync();
    }

    [When("the app reloads the disposable library storage")]
    public async Task WhenReload() =>
        _results = await new RecordingLibrary(new JsonRecordingHistory(_directory.PathFor("history.json")), _jobs).LoadAsync(default);

    [Then("the unfinished deletion and remaining artifacts are visible")]
    public void ThenUnfinishedDeletionVisible()
    {
        var entry = Assert.Single(_results);
        Assert.Equal(AudioAvailability.Deleted, entry.AudioAvailability);
        var pending = entry.Recording.PendingDeletion;
        Assert.NotNull(pending);
        Assert.Equal(_pending!.OperationId, pending.OperationId);
        Assert.Equal(_pending.Scope, pending.Scope);
        Assert.Equal(_pending.Artifacts, pending.Artifacts);
        Assert.Equal(_pending.RemainingJobIds, pending.RemainingJobIds);
        Assert.False(File.Exists(entry.Recording.AudioPath));
    }

    [Then("retained transcripts remain available")]
    public async Task ThenRetainedTranscriptsAvailable()
    {
        var transcript = await _library.ReadTranscriptAsync(Assert.Single(_recordings).Id, _owned!.JobId, default);
        Assert.Equal("Pending retained transcript", transcript.Text);
        Assert.Equal("Pending retained subtitles", transcript.Subtitles);
        Assert.Empty(transcript.Issues);
    }

    [Then("no further artifact is removed until the user explicitly retries")]
    public Task ThenNoAutomaticRemoval() => ThenFilesUnchanged();

    private async Task<Dictionary<string, byte[]>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, byte[]>();
        foreach (var path in Directory.GetFiles(_directory.Root))
        {
            snapshot[path] = await File.ReadAllBytesAsync(path);
        }

        return snapshot;
    }

    [AfterScenario]
    public void Cleanup() => _directory.Dispose();
}

internal sealed class LibraryNetworkObserver : EventListener
{
    private static readonly AsyncLocal<LibraryNetworkObserver?> Scope = new();
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);

    public LibraryNetworkObserver() => Scope.Value = this;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name is "System.Net.Http" or "System.Net.Sockets")
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (Scope.Value == this && eventData.EventName is "RequestStart" or "ConnectStart")
        {
            Interlocked.Increment(ref _requests);
        }
    }

    public override void Dispose()
    {
        if (Scope.Value == this)
        {
            Scope.Value = null;
        }

        base.Dispose();
    }
}
