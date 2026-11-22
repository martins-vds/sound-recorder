using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

[Trait("type", "IntegrationTests")]
public sealed class RecordingLibraryMutationTests : IDisposable
{
    private readonly LibraryDeletionFixture _fixture = new();

    [Fact]
    public async Task SearchAsync_TitleDiffersFromFilename_ReturnsOneEntryWithoutDuplicatingContentMatches()
    {
        await _fixture.InitializeAsync();
        await _fixture.History.UpdateAsync(_fixture.Recording with { Title = "Quarterly roadmap" }, default);
        await File.WriteAllTextAsync(_fixture.Outputs[0].Outputs.TextPath, "Quarterly transcript content");
        var results = await _fixture.CreateLibrary().SearchAsync("Quarterly", default);
        Assert.Equal(_fixture.Recording.Id, Assert.Single(results).Recording.Id);
        Assert.Equal("Quarterly roadmap", results[0].Recording.Title);
    }

    [Fact]
    public async Task LoadAsync_JobWhoseRecordingWasRemoved_DoesNotResurrectItsEntryOrRegisterItsOutputs()
    {
        await _fixture.InitializeAsync();
        var orphan = new SavedRecording(Guid.NewGuid(), Path.Combine(_fixture.Root, "old.wav"),
            "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        var job = new TranscriptionJob(Guid.NewGuid(), orphan, DateTimeOffset.UtcNow, TranscriptionJobStatus.Succeeded,
            new(Path.Combine(_fixture.Root, "old.txt"), Path.Combine(_fixture.Root, "old.srt")), null);
        await _fixture.Jobs.SaveAsync([.. await _fixture.Jobs.LoadAsync(default), job], default);
        var before = await File.ReadAllBytesAsync(_fixture.HistoryPath);

        var entry = Assert.Single(await _fixture.CreateLibrary().LoadAsync(default));

        Assert.Equal(_fixture.Recording.Id, entry.Recording.Id);
        Assert.Equal(_fixture.Outputs, entry.Recording.TranscriptOutputs);
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.HistoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadTranscriptAsync_UnknownRecordingOrLegacyAttempt_ReportsOwnershipRatherThanThrowing(bool legacy)
    {
        await _fixture.InitializeAsync();
        var recordingId = legacy ? _fixture.Recording.Id : Guid.NewGuid();
        if (legacy)
        {
            var recording = _fixture.Recording;
            await File.WriteAllTextAsync(_fixture.HistoryPath, JsonSerializer.Serialize(new[]
            {
                new { recording.Id, recording.AudioPath, recording.Language, recording.Outcome, recording.CapturedDuration },
            }));
        }
        var transcript = await _fixture.CreateLibrary().ReadTranscriptAsync(recordingId, Guid.NewGuid(), default);
        Assert.Null(transcript.Text);
        Assert.Null(transcript.Subtitles);
        AssertOwnershipIssue(Assert.Single(transcript.Issues));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("ownership validation failed"));
        Assert.All(_fixture.Logger.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Message)));
    }

    [Theory]
    [InlineData("text-folder")]
    [InlineData("subtitle-folder")]
    [InlineData("extension")]
    [InlineData("subtitle-name")]
    [InlineData("attempt-one")]
    [InlineData("unrelated")]
    public async Task ReadTranscriptAsync_UnownedPairShape_DoesNotReadOrSearchForeignContent(string invalid)
    {
        await _fixture.InitializeAsync();
        var other = Path.Combine(_fixture.Root, "other");
        Directory.CreateDirectory(other);
        var text = Path.Combine(_fixture.Root, "meeting.transcript-2.txt");
        var subtitle = Path.Combine(_fixture.Root, "meeting.transcript-2.srt");
        (text, subtitle) = invalid switch
        {
            "text-folder" => (Path.Combine(other, "meeting.transcript-2.txt"), subtitle),
            "subtitle-folder" => (text, Path.Combine(other, "meeting.transcript-2.srt")),
            "extension" => (Path.Combine(_fixture.Root, "meeting.transcript-2.json"), subtitle),
            "subtitle-name" => (text, Path.Combine(_fixture.Root, "another.srt")),
            "attempt-one" => (Path.Combine(_fixture.Root, "meeting.transcript-1.txt"), Path.Combine(_fixture.Root, "meeting.transcript-1.srt")),
            _ => (Path.Combine(_fixture.Root, "unrelated-reference-22.txt"), Path.Combine(_fixture.Root, "unrelated-reference-22.srt")),
        };
        var foreign = new OwnedTranscriptOutputs(Guid.NewGuid(), new(text, subtitle));
        await _fixture.History.RegisterOutputsAsync(_fixture.Recording.Id, foreign, default);
        await File.WriteAllTextAsync(text, "Unique foreign secret");
        await File.WriteAllTextAsync(subtitle, "Unique foreign subtitle");

        var library = _fixture.CreateLibrary();
        var transcript = await library.ReadTranscriptAsync(_fixture.Recording.Id, foreign.JobId, default);

        Assert.Null(transcript.Text);
        Assert.Null(transcript.Subtitles);
        AssertOwnershipIssue(Assert.Single(transcript.Issues));
        Assert.Empty(await library.SearchAsync("Unique foreign secret", default));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("search ownership validation failed"));
        Assert.Equal("Unique foreign secret", await File.ReadAllTextAsync(text));
    }

    [Fact]
    public async Task LoadAsync_LocalExtendedPathIsRejected_ReportsOwnershipWithoutNetworkAccess()
    {
        await _fixture.InitializeAsync();
        var extended = @"\\?\" + _fixture.Recording.AudioPath;
        await _fixture.History.UpdateAsync(_fixture.Recording with { AudioPath = extended }, default);
        IRecordingLibrary library = new RecordingLibrary(_fixture.History, logger: _fixture.Logger);

        var entry = Assert.Single(await library.LoadAsync(default));

        Assert.Equal(AudioAvailability.Missing, entry.AudioAvailability);
        AssertOwnershipIssue(Assert.Single(entry.Issues));
        Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("Audio ownership validation failed"));
        Assert.True(File.Exists(_fixture.Recording.AudioPath));
    }

    [Theory]
    [InlineData("audio-intent")]
    [InlineData("audio-outcome")]
    [InlineData("subtitle-outcome")]
    public async Task ReadTranscriptAsync_DeletedCounterpartIsReplacedByDirectory_StillReadsRetainedText(string tombstone)
    {
        await _fixture.InitializeAsync();
        var recording = Assert.Single(await _fixture.History.LoadAsync(default));
        var output = _fixture.Outputs[0];
        var removedPath = tombstone == "subtitle-outcome" ? output.Outputs.SubtitlePath : recording.AudioPath;
        File.Delete(removedPath);
        Directory.CreateDirectory(removedPath);
        recording = tombstone == "audio-intent"
            ? recording with { AudioDeleted = true }
            : recording with
            {
                PendingDeletion = new(Guid.NewGuid(), RecordingDeletionScope.AllArtifacts,
                    [new(tombstone == "audio-outcome" ? RecordingArtifactKind.Audio : RecordingArtifactKind.Subtitle,
                        removedPath, tombstone == "audio-outcome" ? null : output.JobId, ArtifactDeletionState.Removed, null)],
                    [output.JobId], null),
            };
        await _fixture.History.UpdateAsync(recording, default);

        var transcript = await _fixture.CreateLibrary().ReadTranscriptAsync(recording.Id, output.JobId, default);

        Assert.Equal("Transcript attempt 1", transcript.Text);
        if (tombstone == "subtitle-outcome")
        {
            Assert.Null(transcript.Subtitles);
            Assert.Contains("removed", Assert.Single(transcript.Issues).Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.NotNull(transcript.Subtitles);
            Assert.Empty(transcript.Issues);
        }
        Assert.True(Directory.Exists(removedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadTranscriptAsync_UnavailableArtifact_ExplainsMissingVersusAccessFailure(bool locked)
    {
        await _fixture.InitializeAsync();
        var output = _fixture.Outputs[0];
        FileStream? blocker = null;
        if (locked)
            blocker = File.Open(output.Outputs.TextPath, FileMode.Open, FileAccess.Read, FileShare.None);
        else
            File.Delete(output.Outputs.TextPath);
        try
        {
            var result = await _fixture.CreateLibrary().ReadTranscriptAsync(_fixture.Recording.Id, output.JobId, default);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(output.Outputs.TextPath, issue.Path);
            Assert.Contains(locked ? "file access" : "Restore the original file", issue.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(_fixture.Logger.Entries, entry => entry.Message.Contains("Transcript read failed"));
            Assert.All(_fixture.Logger.Entries, entry => Assert.DoesNotContain(_fixture.Root, entry.Message));
        }
        finally { blocker?.Dispose(); }
    }

    private static void AssertOwnershipIssue(LibraryIssue issue)
    {
        Assert.Equal(LibraryFailureCode.OwnershipInvalid, issue.Code);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        Assert.Contains("path", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _fixture.Dispose();
}
