using System.Text.Json;
using System.Diagnostics;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingLibraryStorageTests : IDisposable
{
    private readonly StorageTestDirectory _directory = new();

    [Fact]
    public async Task LoadAsync_NullHistory_ReportsMalformedStorageWithoutReplacingIt()
    {
        var path = _directory.PathFor("history.json");
        await File.WriteAllTextAsync(path, "null");
        var history = new JsonRecordingHistory(path);

        await Assert.ThrowsAsync<JsonException>(() => history.LoadAsync(default));
        Assert.Equal("null", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UpdateAsync_LibraryMetadata_RoundTripsWithoutChangingRecordingIdentityOrOutcome()
    {
        var path = _directory.PathFor("history.json");
        IRecordingHistory history = new JsonRecordingHistory(path);
        var original = Recording("meeting.wav");
        await history.AddAsync(original, default);
        var updated = original with
        {
            Title = "Planning",
            AudioDeleted = true,
            TranscriptOutputs = [new(Guid.NewGuid(), new("meeting.txt", "meeting.srt"))],
        };

        var error = await Record.ExceptionAsync(() => history.UpdateAsync(updated, default));
        Assert.Null(error);

        var restored = Assert.Single(await new JsonRecordingHistory(path).LoadAsync(default));
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.AudioPath, restored.AudioPath);
        Assert.Equal(original.Language, restored.Language);
        Assert.Equal(original.Outcome, restored.Outcome);
        Assert.Equal(original.CapturedDuration, restored.CapturedDuration);
        Assert.Equal("Planning", restored.Title);
        Assert.True(restored.AudioDeleted);
        Assert.Equal(updated.TranscriptOutputs, restored.TranscriptOutputs);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("{\"Version\":\"future\",\"Recordings\":[]}")]
    [InlineData("{\"Version\":2,\"Recordings\":[]}")]
    [InlineData("{\"Version\":1,\"Recordings\":null}")]
    [InlineData("{}")]
    public async Task LoadAsync_InvalidStorage_LogsFailureAndNeverOverwritesIt(string json)
    {
        var path = _directory.PathFor("invalid.json");
        await File.WriteAllTextAsync(path, json);
        var logger = new StorageTestLogger<JsonRecordingHistory>();
        var history = new JsonRecordingHistory(path, logger);

        await Assert.ThrowsAnyAsync<JsonException>(() => history.LoadAsync(default));
        await Assert.ThrowsAnyAsync<JsonException>(() => history.AddAsync(Recording("new.wav"), default));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Contains("failed", entry.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(_directory.Root, "*.tmp"));
    }

    [Fact]
    public async Task RegisterOutputsAsync_AllAttempts_RemainOwnedAfterJobHistoryIsRemoved()
    {
        var path = _directory.PathFor("history.json");
        IRecordingHistory history = new JsonRecordingHistory(path);
        var recording = Recording("meeting.wav");
        await history.AddAsync(recording, default);
        var first = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        var second = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting.transcript-2"));
        await File.WriteAllTextAsync(first.Outputs.TextPath, "First attempt");
        await File.WriteAllTextAsync(first.Outputs.SubtitlePath, "First subtitles");
        await File.WriteAllTextAsync(second.Outputs.TextPath, "Second attempt");
        await File.WriteAllTextAsync(second.Outputs.SubtitlePath, "Second subtitles");
        var jobs = new JsonTranscriptionJobRepository(_directory.PathFor("jobs.json"));
        await jobs.SaveAsync(
        [
            new(first.JobId, recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded, first.Outputs, null),
            new(second.JobId, recording, DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded, second.Outputs, null),
        ], default);

        var error = await Record.ExceptionAsync(() => history.RegisterOutputsAsync(recording.Id, first, default));
        Assert.Null(error);
        await history.RegisterOutputsAsync(recording.Id, second, default);
        var beforeDuplicate = await File.ReadAllBytesAsync(path);
        await history.RegisterOutputsAsync(recording.Id, first, default);
        Assert.Equal(beforeDuplicate, await File.ReadAllBytesAsync(path));
        await jobs.SaveAsync([], default);

        var restored = Assert.Single(await new JsonRecordingHistory(path).LoadAsync(default));
        Assert.Equal(new[] { first, second }, restored.TranscriptOutputs);
        Assert.Empty(await jobs.LoadAsync(default));
        Assert.Equal("First attempt", await File.ReadAllTextAsync(first.Outputs.TextPath));
        Assert.Equal("Second subtitles", await File.ReadAllTextAsync(second.Outputs.SubtitlePath));
    }

    private TranscriptOutputs Outputs(string stem) =>
        new(_directory.PathFor($"{stem}.txt"), _directory.PathFor($"{stem}.srt"));

    [Fact]
    public async Task RegisterOutputsAsync_ConflictingJobOrFileOwnership_RejectsWithoutChangingHistory()
    {
        var path = _directory.PathFor("history.json");
        IRecordingHistory history = new JsonRecordingHistory(path);
        var first = Recording("first.wav");
        var second = Recording("second.wav");
        await history.AddAsync(first, default);
        await history.AddAsync(second, default);
        var owned = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("first"));
        await history.RegisterOutputsAsync(first.Id, owned, default);
        var before = await File.ReadAllBytesAsync(path);

        var conflict = await Assert.ThrowsAsync<InvalidDataException>(() =>
            history.RegisterOutputsAsync(first.Id, owned with { Outputs = Outputs("different") }, default));
        Assert.Contains("different transcript output pair", conflict.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            history.RegisterOutputsAsync(second.Id, owned, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            history.RegisterOutputsAsync(second.Id, owned with { JobId = Guid.NewGuid() }, default));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Assert.Single(await history.LoadAsync(default), item => item.Id == second.Id).TranscriptOutputs);
    }

    [Fact]
    public async Task RemoveAsync_MetadataOnly_LeavesFilesAndOtherEntriesUntouched()
    {
        var path = _directory.PathFor("history.json");
        IRecordingHistory history = new JsonRecordingHistory(path);
        var recording = Recording("first.wav");
        var neighbor = Recording("second.wav");
        await File.WriteAllTextAsync(recording.AudioPath, "fixture audio");
        await history.AddAsync(recording, default);
        await history.AddAsync(neighbor, default);

        var error = await Record.ExceptionAsync(() => history.RemoveAsync(recording.Id, default));
        Assert.Null(error);
        await history.RemoveAsync(recording.Id, default);

        Assert.Equal(neighbor, Assert.Single(await new JsonRecordingHistory(path).LoadAsync(default)));
        Assert.Equal("fixture audio", await File.ReadAllTextAsync(recording.AudioPath));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => history.UpdateAsync(recording, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            history.RegisterOutputsAsync(recording.Id, new(Guid.NewGuid(), Outputs("first")), default));
    }

    [Fact]
    public async Task LoadAsync_LegacySuccessfulJobs_ImportsEveryPairWithoutRewritingJobsOrFiles()
    {
        var path = _directory.PathFor("legacy.json");
        var original = Recording("meeting.wav");
        var legacy = new
        {
            original.Id, original.AudioPath, original.Language, original.Outcome, original.CapturedDuration,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { legacy }));
        await File.WriteAllTextAsync(original.AudioPath, "legacy audio bytes");
        var first = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        var second = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting.transcript-2"));
        await File.WriteAllTextAsync(first.Outputs.TextPath, "First successful transcript");
        await File.WriteAllTextAsync(first.Outputs.SubtitlePath, "First successful subtitles");
        await File.WriteAllTextAsync(second.Outputs.TextPath, "Second successful transcript");
        await File.WriteAllTextAsync(second.Outputs.SubtitlePath, "Second successful subtitles");
        var jobsPath = _directory.PathFor("jobs.json");
        var repository = new JsonTranscriptionJobRepository(jobsPath);
        var createdAt = DateTimeOffset.Parse("2026-03-04T05:06:07+02:00");
        await repository.SaveAsync(
        [
            new(first.JobId, original, createdAt, TranscriptionJobStatus.Succeeded, first.Outputs, null),
            new(second.JobId, original, createdAt.AddMinutes(1), TranscriptionJobStatus.Succeeded, second.Outputs, null),
            new(Guid.NewGuid(), original, createdAt.AddMinutes(2), TranscriptionJobStatus.Failed, Outputs("failed"), "Failure"),
        ], default);
        var jobBytes = await File.ReadAllBytesAsync(jobsPath);
        IRecordingLibrary library = new RecordingLibrary(new JsonRecordingHistory(path), repository);

        var restored = Assert.Single(await library.LoadAsync(default)).Recording;

        Assert.Equal(new[] { first, second }, restored.TranscriptOutputs);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.AudioPath, restored.AudioPath);
        Assert.Equal(original.Language, restored.Language);
        Assert.Equal(original.Outcome, restored.Outcome);
        Assert.Equal(original.CapturedDuration, restored.CapturedDuration);
        Assert.Equal(jobBytes, await File.ReadAllBytesAsync(jobsPath));
        Assert.Equal("legacy audio bytes", await File.ReadAllTextAsync(original.AudioPath));
        Assert.Equal("First successful transcript", await File.ReadAllTextAsync(first.Outputs.TextPath));
        Assert.Equal("Second successful subtitles", await File.ReadAllTextAsync(second.Outputs.SubtitlePath));
        var migrated = await File.ReadAllBytesAsync(path);
        await library.LoadAsync(default);
        Assert.Equal(migrated, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain("model", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadTranscriptAsync_ExactRetainedAttempt_DoesNotNeedAudioOrJobHistory()
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording("meeting.wav");
        var first = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        var second = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting.transcript-2"));
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, first, default);
        await history.RegisterOutputsAsync(recording.Id, second, default);
        await history.UpdateAsync(recording with { AudioDeleted = true }, default);
        await File.WriteAllTextAsync(first.Outputs.TextPath, "Older exact transcript");
        await File.WriteAllTextAsync(first.Outputs.SubtitlePath, "Older exact subtitles");
        await File.WriteAllTextAsync(second.Outputs.TextPath, "Newer different transcript");
        await File.WriteAllTextAsync(second.Outputs.SubtitlePath, "Newer different subtitles");
        IRecordingLibrary library = new RecordingLibrary(history);
        TranscriptReadResult? result = null;

        var error = await Record.ExceptionAsync(async () =>
            result = await library.ReadTranscriptAsync(recording.Id, first.JobId, default));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal("Older exact transcript", result.Text);
        Assert.Equal("Older exact subtitles", result.Subtitles);
        Assert.Empty(result.Issues);
        Assert.False(File.Exists(recording.AudioPath));
        var different = await library.ReadTranscriptAsync(recording.Id, second.JobId, default);
        Assert.Equal("Newer different transcript", different.Text);
        var foreign = await library.ReadTranscriptAsync(recording.Id, Guid.NewGuid(), default);
        Assert.Null(foreign.Text);
        Assert.Null(foreign.Subtitles);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(foreign.Issues).Code);
    }

    [Theory]
    [InlineData(false, LibraryFailureCode.ArtifactMissing)]
    [InlineData(true, LibraryFailureCode.ArtifactReadFailed)]
    public async Task ReadTranscriptAsync_MissingOrLockedText_ReportsFailureAndRetainsSubtitles(
        bool locked, LibraryFailureCode expectedCode)
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording("meeting.wav");
        var output = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, output, default);
        await File.WriteAllTextAsync(output.Outputs.SubtitlePath, "Private subtitle content");
        if (locked)
        {
            await File.WriteAllTextAsync(output.Outputs.TextPath, "Private transcript content");
        }
        using var held = locked ? File.Open(output.Outputs.TextPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var logger = new StorageTestLogger<RecordingLibrary>();
        IRecordingLibrary library = new RecordingLibrary(history, logger: logger);
        TranscriptReadResult? result = null;

        var error = await Record.ExceptionAsync(async () =>
            result = await library.ReadTranscriptAsync(recording.Id, output.JobId, default));
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Null(result.Text);
        Assert.Equal("Private subtitle content", result.Subtitles);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(expectedCode, issue.Code);
        Assert.Equal(output.Outputs.TextPath, issue.Path);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("Private", entry.Message);
            Assert.DoesNotContain(_directory.Root, entry.Message);
        });
    }

    [Fact]
    public async Task LoadAsync_AudioAvailability_DistinguishesMissingFromIntentionalDeletionWithoutMutatingMetadata()
    {
        var path = _directory.PathFor("history.json");
        IRecordingHistory history = new JsonRecordingHistory(path);
        var available = Recording("available.wav");
        var missing = Recording("missing.wav");
        var deleted = Recording("deleted.wav") with { AudioDeleted = true };
        await File.WriteAllTextAsync(available.AudioPath, "audio fixture");
        await history.AddAsync(available, default);
        await history.AddAsync(missing, default);
        await history.AddAsync(deleted, default);
        var before = await File.ReadAllBytesAsync(path);
        IRecordingLibrary library = new RecordingLibrary(history);

        var entries = await library.LoadAsync(default);

        Assert.Equal(AudioAvailability.Available, Assert.Single(entries, entry => entry.Recording.Id == available.Id).AudioAvailability);
        var absent = Assert.Single(entries, entry => entry.Recording.Id == missing.Id);
        Assert.Equal(AudioAvailability.Missing, absent.AudioAvailability);
        Assert.Equal(LibraryFailureCode.ArtifactMissing, Assert.Single(absent.Issues).Code);
        Assert.False(absent.Recording.AudioDeleted);
        var intentional = Assert.Single(entries, entry => entry.Recording.Id == deleted.Id);
        Assert.Equal(AudioAvailability.Deleted, intentional.AudioAvailability);
        Assert.Empty(intentional.Issues);
        Assert.Equal(RecordingOutcome.Recovered, intentional.Recording.Outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(missing.AudioPath));
        Assert.False(File.Exists(deleted.AudioPath));
    }

    [Fact]
    public async Task ReadTranscriptAsync_ForeignReference_FailsClosedBeforeReadingEitherFile()
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording("meeting.wav");
        var outputs = Outputs("unrelated");
        var owned = new OwnedTranscriptOutputs(Guid.NewGuid(), outputs);
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, owned, default);
        await File.WriteAllTextAsync(outputs.TextPath, "Unrelated private content");
        await File.WriteAllTextAsync(outputs.SubtitlePath, "Unrelated private subtitles");
        IRecordingLibrary library = new RecordingLibrary(history);

        var result = await library.ReadTranscriptAsync(recording.Id, owned.JobId, default);

        Assert.Null(result.Text);
        Assert.Null(result.Subtitles);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Equal("Unrelated private content", await File.ReadAllTextAsync(outputs.TextPath));
    }

    [Fact]
    public async Task LoadAsync_PendingDeletion_RetainsPreciseOutcomesAndNeverResumesRemoval()
    {
        var path = _directory.PathFor("history.json");
        var recording = Recording("meeting.wav");
        var owned = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
        [
            new(RecordingArtifactKind.Audio, recording.AudioPath, null, ArtifactDeletionState.Removed, null),
            new(RecordingArtifactKind.Text, owned.Outputs.TextPath, owned.JobId, ArtifactDeletionState.Failed,
                new(LibraryFailureCode.ArtifactDeleteFailed, "Close the file and explicitly retry.", owned.Outputs.TextPath)),
            new(RecordingArtifactKind.Subtitle, owned.Outputs.SubtitlePath, owned.JobId, ArtifactDeletionState.Pending, null),
        ], [owned.JobId], null);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[]
        {
            new
            {
                recording.Id, recording.AudioPath, recording.Language, recording.Outcome, recording.CapturedDuration,
                AudioDeleted = true, TranscriptOutputs = new[] { owned }, PendingDeletion = pending,
            },
        }));
        await File.WriteAllTextAsync(owned.Outputs.TextPath, "Retained transcript");
        await File.WriteAllTextAsync(owned.Outputs.SubtitlePath, "Retained subtitles");
        IRecordingHistory history = new JsonRecordingHistory(path);
        IRecordingLibrary library = new RecordingLibrary(history);
        IReadOnlyList<RecordingLibraryEntry>? entries = null;

        var error = await Record.ExceptionAsync(async () => entries = await library.LoadAsync(default));
        Assert.Null(error);
        var entry = Assert.Single(entries!);
        await history.UpdateAsync(entry.Recording with { Title = "Pending explicit retry" }, default);
        var reloaded = Assert.Single(await new RecordingLibrary(new JsonRecordingHistory(path)).LoadAsync(default));
        Assert.Equal(AudioAvailability.Deleted, reloaded.AudioAvailability);
        var transcript = await library.ReadTranscriptAsync(recording.Id, owned.JobId, default);
        Assert.Equal("Retained transcript", transcript.Text);
        Assert.Equal("Retained subtitles", transcript.Subtitles);
        Assert.False(File.Exists(recording.AudioPath));
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var state = persisted.RootElement.GetProperty("Recordings")[0].GetProperty("PendingDeletion");
        Assert.Equal(pending.OperationId, state.GetProperty("OperationId").GetGuid());
        Assert.Equal((int)ArtifactDeletionState.Removed, state.GetProperty("Artifacts")[0].GetProperty("State").GetInt32());
        Assert.Equal((int)ArtifactDeletionState.Failed, state.GetProperty("Artifacts")[1].GetProperty("State").GetInt32());
        Assert.Equal(owned.JobId, state.GetProperty("RemainingJobIds")[0].GetGuid());
    }

    [Fact]
    public async Task UpdateAsync_StaleMetadataSnapshot_DoesNotDropRegisteredOwnership()
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var original = Recording("meeting.wav");
        await history.AddAsync(original, default);
        var owned = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        await history.RegisterOutputsAsync(original.Id, owned, default);

        await history.UpdateAsync(original with { Title = "New title" }, default);

        var restored = Assert.Single(await history.LoadAsync(default));
        Assert.Equal(owned, Assert.Single(restored.TranscriptOutputs));
        Assert.Equal("New title", restored.Title);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("kind")]
    [InlineData("state")]
    public async Task LoadAsync_UnknownDeletionMetadata_RejectsWithoutDiscardingPendingIntent(string invalidField)
    {
        var path = _directory.PathFor("history.json");
        var recording = Recording("meeting.wav") with
        {
            PendingDeletion = new(Guid.NewGuid(),
                invalidField == "scope" ? (RecordingDeletionScope)999 : RecordingDeletionScope.AudioOnly,
                [new(invalidField == "kind" ? (RecordingArtifactKind)999 : RecordingArtifactKind.Audio,
                    _directory.PathFor("meeting.wav"), null,
                    invalidField == "state" ? (ArtifactDeletionState)999 : ArtifactDeletionState.Pending, null)],
                [], null),
        };
        var json = JsonSerializer.Serialize(new[] { recording });
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<JsonException>(() => new JsonRecordingHistory(path).LoadAsync(default));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LoadAsync_ForeignSuccessfulJob_ReportsOwnershipFailureWithoutImportingIt()
    {
        var historyPath = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(historyPath);
        var recording = Recording("meeting.wav");
        await history.AddAsync(recording, default);
        var jobsPath = _directory.PathFor("jobs.json");
        var repository = new JsonTranscriptionJobRepository(jobsPath);
        await repository.SaveAsync(
            [new(Guid.NewGuid(), recording with { AudioPath = _directory.PathFor("unrelated.wav") },
                DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded, Outputs("unrelated"), null)], default);
        var historyBefore = await File.ReadAllBytesAsync(historyPath);
        var jobsBefore = await File.ReadAllBytesAsync(jobsPath);
        var logger = new StorageTestLogger<RecordingLibrary>();
        IRecordingLibrary library = new RecordingLibrary(history, repository, logger);

        var entry = Assert.Single(await library.LoadAsync(default));

        Assert.Contains(entry.Issues, issue => issue.Code == LibraryFailureCode.OwnershipInvalid);
        Assert.Empty(entry.Recording.TranscriptOutputs);
        Assert.Equal(historyBefore, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(jobsBefore, await File.ReadAllBytesAsync(jobsPath));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("ownership", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadTranscriptAsync_JunctionRedirectsArtifactDirectory_RejectsForeignContent()
    {
        var target = _directory.PathFor("target");
        var link = _directory.PathFor("linked");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link}' -Target '{target}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var recording = new SavedRecording(Guid.NewGuid(), Path.Combine(link, "meeting.wav"),
                "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(8));
            var output = new OwnedTranscriptOutputs(Guid.NewGuid(),
                new(Path.Combine(link, "meeting.txt"), Path.Combine(link, "meeting.srt")));
            await File.WriteAllTextAsync(Path.Combine(target, "meeting.txt"), "Foreign junction content");
            await File.WriteAllTextAsync(Path.Combine(target, "meeting.srt"), "Foreign junction subtitles");
            IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
            await history.AddAsync(recording, default);
            await history.RegisterOutputsAsync(recording.Id, output, default);

            var transcript = await new RecordingLibrary(history).ReadTranscriptAsync(recording.Id, output.JobId, default);

            Assert.Null(transcript.Text);
            Assert.Null(transcript.Subtitles);
            Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(transcript.Issues).Code);
            Assert.Equal("Foreign junction content", await File.ReadAllTextAsync(Path.Combine(target, "meeting.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task LoadAsync_StoragePathIsDirectory_ReportsUnavailableStorageInsteadOfEmptyLibrary()
    {
        var history = new JsonRecordingHistory(_directory.Root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.LoadAsync(default));
        Assert.True(Directory.Exists(_directory.Root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task SearchAsync_UntitledRecording_UsesFilenameAndHandlesEmptyAndNoMatchQueries(string? title)
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording("meeting.wav") with { Title = title };
        await history.AddAsync(recording, default);
        IRecordingLibrary library = new RecordingLibrary(history);

        Assert.Equal(recording.Id, Assert.Single(await library.SearchAsync(" MEETING ", default)).Recording.Id);
        Assert.Single(await library.SearchAsync("  ", default));
        Assert.Empty(await library.SearchAsync("not present", default));
    }

    [Fact]
    public async Task SearchAsync_EarlierRetainedAttempt_RemainsSearchableWithoutJobHistory()
    {
        var path = _directory.PathFor("history.json");
        IRecordingHistory history = new JsonRecordingHistory(path);
        var recording = Recording("meeting.wav") with { Title = "Review" };
        var first = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        var second = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting.transcript-2"));
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, first, default);
        await history.RegisterOutputsAsync(recording.Id, second, default);
        await File.WriteAllTextAsync(first.Outputs.TextPath, "Earlier HANDOVER discussion");
        await File.WriteAllTextAsync(second.Outputs.TextPath, "Later unrelated discussion");
        var before = await File.ReadAllBytesAsync(path);
        IRecordingLibrary library = new RecordingLibrary(new JsonRecordingHistory(path));

        Assert.Equal(recording.Id, Assert.Single(await library.SearchAsync("handover", default)).Recording.Id);
        Assert.Equal(recording.Id, Assert.Single(await library.SearchAsync("later", default)).Recording.Id);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AddAsync_LockedHistoryTarget_PreservesOriginalAndCleansOnlyOwnedStaging()
    {
        var path = _directory.PathFor("history.json");
        await File.WriteAllTextAsync(path, "[]");
        var neighbor = _directory.PathFor("unrelated.tmp");
        await File.WriteAllTextAsync(neighbor, "Unrelated staging file");
        var logger = new StorageTestLogger<JsonRecordingHistory>();
        var history = new JsonRecordingHistory(path, logger);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => history.AddAsync(Recording("meeting.wav"), default));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Equal("[]", await File.ReadAllTextAsync(path));
            Assert.Equal(neighbor, Assert.Single(Directory.GetFiles(_directory.Root, "*.tmp")));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
        }

        await history.AddAsync(Recording("retry.wav"), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(await history.LoadAsync(default));
        Assert.Equal("Unrelated staging file", await File.ReadAllTextAsync(neighbor));
    }

    [Fact]
    public async Task DeleteAsync_MetadataSlice_ExplicitlyRejectsWithoutChangingArtifactsOrHistory()
    {
        var path = _directory.PathFor("history.json");
        var history = new JsonRecordingHistory(path);
        var recording = Recording("meeting.wav");
        await history.AddAsync(recording, default);
        await File.WriteAllTextAsync(recording.AudioPath, "Unchanged audio");
        var repository = new JsonTranscriptionJobRepository(_directory.PathFor("jobs.json"));
        var job = new TranscriptionJob(Guid.NewGuid(), recording);
        await repository.SaveAsync([job], default);
        var historyBefore = await File.ReadAllBytesAsync(path);
        var jobsBefore = await File.ReadAllBytesAsync(_directory.PathFor("jobs.json"));
        IRecordingLibrary library = new RecordingLibrary(history, repository);

        var audioOnly = await library.DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AudioOnly, default);
        var all = await library.DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, default);
        var jobOnly = await library.DeleteJobHistoryAsync(job.Id, default);

        Assert.All(new[] { audioOnly, all, jobOnly }, result =>
        {
            Assert.Equal(LibraryDeletionStatus.Rejected, result.Status);
            Assert.Equal(LibraryFailureCode.OrchestrationUnavailable, Assert.Single(result.Issues).Code);
        });
        Assert.Equal(historyBefore, await File.ReadAllBytesAsync(path));
        Assert.Equal(jobsBefore, await File.ReadAllBytesAsync(_directory.PathFor("jobs.json")));
        Assert.Equal("Unchanged audio", await File.ReadAllTextAsync(recording.AudioPath));
    }

    [Fact]
    public async Task UpdateAsync_StaleTitleSnapshot_PreservesAudioRemovalAndUnfinishedDeletion()
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var original = Recording("meeting.wav");
        await history.AddAsync(original, default);
        var pending = new PendingRecordingDeletion(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
            [new(RecordingArtifactKind.Audio, original.AudioPath, null, ArtifactDeletionState.Removed, null)],
            [Guid.NewGuid()], null);
        await history.UpdateAsync(original with { AudioDeleted = true, PendingDeletion = pending }, default);

        await history.UpdateAsync(original with { Title = "Updated title" }, default);

        var restored = Assert.Single(await history.LoadAsync(default));
        Assert.True(restored.AudioDeleted);
        Assert.NotNull(restored.PendingDeletion);
        Assert.Equal(pending.OperationId, restored.PendingDeletion.OperationId);
        Assert.Equal(pending.RemainingJobIds, restored.PendingDeletion.RemainingJobIds);
        Assert.Equal("Updated title", restored.Title);
    }

    [Theory]
    [InlineData("+2")]
    [InlineData("02")]
    [InlineData(" 2")]
    public async Task ReadTranscriptAsync_NonPublishedAttemptFilename_RejectsForeignContent(string suffix)
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording("meeting.wav");
        var output = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs($"meeting.transcript-{suffix}"));
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, output, default);
        await File.WriteAllTextAsync(output.Outputs.TextPath, "Unrelated lookalike transcript");
        await File.WriteAllTextAsync(output.Outputs.SubtitlePath, "Unrelated lookalike subtitles");

        var result = await new RecordingLibrary(history).ReadTranscriptAsync(recording.Id, output.JobId, default);

        Assert.Null(result.Text);
        Assert.Null(result.Subtitles);
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, Assert.Single(result.Issues).Code);
        Assert.Equal("Unrelated lookalike transcript", await File.ReadAllTextAsync(output.Outputs.TextPath));
    }

    [Fact]
    public async Task ReadTranscriptAsync_RetainedOwnership_DoesNotDependOnReadableJobHistory()
    {
        IRecordingHistory history = new JsonRecordingHistory(_directory.PathFor("history.json"));
        var recording = Recording("meeting.wav");
        var output = new OwnedTranscriptOutputs(Guid.NewGuid(), Outputs("meeting"));
        await history.AddAsync(recording, default);
        await history.RegisterOutputsAsync(recording.Id, output, default);
        await File.WriteAllTextAsync(output.Outputs.TextPath, "Retained exact transcript");
        await File.WriteAllTextAsync(output.Outputs.SubtitlePath, "Retained exact subtitles");
        var jobsPath = _directory.PathFor("jobs.json");
        await File.WriteAllTextAsync(jobsPath, "{broken");
        IRecordingLibrary library = new RecordingLibrary(history, new JsonTranscriptionJobRepository(jobsPath));
        TranscriptReadResult? transcript = null;

        var error = await Record.ExceptionAsync(async () =>
            transcript = await library.ReadTranscriptAsync(recording.Id, output.JobId, default));

        Assert.Null(error);
        Assert.NotNull(transcript);
        Assert.Equal("Retained exact transcript", transcript.Text);
        Assert.Equal("Retained exact subtitles", transcript.Subtitles);
        Assert.Equal("{broken", await File.ReadAllTextAsync(jobsPath));
    }

    private SavedRecording Recording(string name) =>
        new(Guid.NewGuid(), _directory.PathFor(name), "Brazilian Portuguese",
            RecordingOutcome.Recovered, TimeSpan.FromTicks(123456789));

    public void Dispose() => _directory.Dispose();
}
