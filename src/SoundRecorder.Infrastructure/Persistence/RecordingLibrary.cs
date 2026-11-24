using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Persistence;

/// <summary>Reviews local files using durable library ownership rather than job-history lifetime.</summary>
public sealed partial class RecordingLibrary(
    IRecordingHistory history,
    ITranscriptionJobRepository? jobs = null,
    ILogger<RecordingLibrary>? logger = null,
    Func<Guid, CancellationToken, Task>? cancelRecordingAsync = null,
    Func<Guid, CancellationToken, Task>? deleteJobAsync = null,
    Func<Guid, CancellationToken, Task>? releasePlaybackAsync = null) : IRecordingLibrary
{
    private readonly ILogger<RecordingLibrary> _logger = logger ?? NullLogger<RecordingLibrary>.Instance;

    public async Task<IReadOnlyList<RecordingLibraryEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        var recordings = await history.LoadAsync(cancellationToken);
        var importIssues = await ImportOutputsAsync(recordings, cancellationToken);
        if (jobs is not null)
        {
            recordings = await history.LoadAsync(cancellationToken);
        }
        var unsuccessful = await KnownUnsuccessfulJobsAsync(cancellationToken);

        return recordings.Select(recording =>
        {
            var reviewable = recording with
            {
                TranscriptOutputs = recording.TranscriptOutputs.Where(output => !unsuccessful.Contains(output.JobId)).ToArray(),
            };
            var entry = ToEntry(reviewable);
            return importIssues.TryGetValue(recording.Id, out var issues)
                ? entry with { Issues = [.. entry.Issues, .. issues] } : entry;
        }).ToArray();
    }

    private async Task<Dictionary<Guid, List<LibraryIssue>>> ImportOutputsAsync(
        IReadOnlyList<SavedRecording> recordings, CancellationToken cancellationToken)
    {
        var importIssues = new Dictionary<Guid, List<LibraryIssue>>();
        if (jobs is null)
        {
            return importIssues;
        }

        foreach (var job in await jobs.LoadAsync(cancellationToken))
        {
            var issue = await ImportJobOutputsAsync(job, recordings, cancellationToken);
            if (issue is null)
            {
                continue;
            }

            if (!importIssues.TryGetValue(job.Recording.Id, out var issues))
            {
                importIssues[job.Recording.Id] = issues = [];
            }

            issues.Add(issue);
        }

        return importIssues;
    }

    private async Task<LibraryIssue?> ImportJobOutputsAsync(
        TranscriptionJob job, IReadOnlyList<SavedRecording> recordings, CancellationToken cancellationToken)
    {
        var recording = recordings.FirstOrDefault(recording => recording.Id == job.Recording.Id);
        if (job.Status != TranscriptionJobStatus.Succeeded || recording is null)
        {
            return null;
        }

        if (job.Outputs is null ||
            !string.Equals(recording.AudioPath, job.Recording.AudioPath, StringComparison.OrdinalIgnoreCase) ||
            !OwnsOutputPaths(recording, job.Outputs))
        {
            _logger.LogWarning("Historical output ownership validation failed for recording {RecordingId}, job {JobId}.",
                recording.Id, job.Id);
            return new(LibraryFailureCode.OwnershipInvalid,
                "A historical output pair could not be associated safely. Check its recording and output paths.",
                JobId: job.Id);
        }

        if (AlreadyRegistered(recording, job))
            return null;
        await history.RegisterOutputsAsync(job.Recording.Id,
            new OwnedTranscriptOutputs(job.Id, job.Outputs) { Language = job.Language }, cancellationToken);
        return null;
    }

    private static bool AlreadyRegistered(SavedRecording recording, TranscriptionJob job) =>
        recording.TranscriptOutputs.Any(output => output.JobId == job.Id && output.Outputs == job.Outputs);

    private RecordingLibraryEntry ToEntry(SavedRecording recording)
    {
        if (recording.AudioDeleted)
        {
            return new(recording, AudioAvailability.Deleted, []);
        }

        try
        {
            ValidateArtifactPath(recording.AudioPath);
        }
        catch (Exception exception) when (IsArtifactPathFailure(exception))
        {
            _logger.LogWarning("Audio ownership validation failed for recording {RecordingId}.", recording.Id);
            return new(recording, AudioAvailability.Missing,
                [new(LibraryFailureCode.OwnershipInvalid, "Recording artifacts must be local files without redirected paths.", recording.AudioPath)]);
        }

        if (File.Exists(recording.AudioPath))
        {
            return new(recording, AudioAvailability.Available, []);
        }

        _logger.LogWarning("Audio availability check failed for recording {RecordingId}: original audio is missing.", recording.Id);
        return new(recording, AudioAvailability.Missing,
            [new(LibraryFailureCode.ArtifactMissing, "The original audio is missing. Retained transcripts can still be reviewed.", recording.AudioPath)]);
    }

    public async Task<IReadOnlyList<RecordingLibraryEntry>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var entries = await LoadAsync(cancellationToken);
        var term = query.Trim();
        var matches = new List<RecordingLibraryEntry>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recording = entry.Recording;
            var title = string.IsNullOrWhiteSpace(recording.Title)
                ? Path.GetFileNameWithoutExtension(recording.AudioPath) : recording.Title;
            if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(entry);
                continue;
            }

            var issues = entry.Issues.ToList();
            if (await ContainsTranscriptAsync(recording, term, issues, cancellationToken))
            {
                matches.Add(entry with { Issues = issues });
            }
        }

        return matches;
    }

    private async Task<bool> ContainsTranscriptAsync(
        SavedRecording recording, string term, List<LibraryIssue> issues, CancellationToken cancellationToken)
    {
        foreach (var output in recording.TranscriptOutputs)
        {
            if (WasArtifactRemoved(recording, output.Outputs.TextPath))
                continue;
            if (!OwnsOutputPaths(recording, output.Outputs))
            {
                _logger.LogWarning("Transcript search ownership validation failed for recording {RecordingId}, job {JobId}.",
                    recording.Id, output.JobId);
                continue;
            }

            var text = await ReadArtifactAsync(output.Outputs.TextPath, recording.Id, output.JobId,
                issues, cancellationToken);
            if (text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<TranscriptReadResult> ReadTranscriptAsync(
        Guid recordingId, Guid jobId, CancellationToken cancellationToken)
    {
        var (recording, owned) = await FindOwnedTranscriptAsync(recordingId, jobId, cancellationToken);
        if (owned is null || (await KnownUnsuccessfulJobsAsync(cancellationToken)).Contains(jobId) ||
            !OwnsOutputPaths(recording!, owned.Outputs))
        {
            _logger.LogWarning("Transcript ownership validation failed for recording {RecordingId}, job {JobId}.",
                recordingId, jobId);
            return new(null, null,
                [new(LibraryFailureCode.OwnershipInvalid,
                    "Output ownership could not be verified. Check the original recording and output paths and file access.",
                    JobId: jobId)]);
        }

        var issues = new List<LibraryIssue>();
        var text = await ReadArtifactAsync(owned.Outputs.TextPath, recordingId, jobId, issues, cancellationToken,
            WasArtifactRemoved(recording!, owned.Outputs.TextPath));
        var subtitles = await ReadArtifactAsync(owned.Outputs.SubtitlePath, recordingId, jobId, issues, cancellationToken,
            WasArtifactRemoved(recording!, owned.Outputs.SubtitlePath));
        return new(text, subtitles, issues);
    }

    private async Task<(SavedRecording? Recording, OwnedTranscriptOutputs? Outputs)> FindOwnedTranscriptAsync(
        Guid recordingId, Guid jobId, CancellationToken cancellationToken)
    {
        var recording = (await history.LoadAsync(cancellationToken))
            .FirstOrDefault(recording => recording.Id == recordingId);
        var owned = recording?.TranscriptOutputs.FirstOrDefault(output => output.JobId == jobId);
        if (NeedsLegacyTranscriptLookup(recording, owned))
            return await FindLegacyTranscriptAsync(recordingId, jobId, cancellationToken);
        return (recording, owned);
    }

    private bool NeedsLegacyTranscriptLookup(SavedRecording? recording, OwnedTranscriptOutputs? owned) =>
        owned is null && recording is not null && jobs is not null;

    private async Task<(SavedRecording? Recording, OwnedTranscriptOutputs? Outputs)> FindLegacyTranscriptAsync(
        Guid recordingId, Guid jobId, CancellationToken cancellationToken)
    {
        var recording = (await LoadAsync(cancellationToken))
            .FirstOrDefault(entry => entry.Recording.Id == recordingId)?.Recording;
        var owned = recording?.TranscriptOutputs.FirstOrDefault(output => output.JobId == jobId);
        return (recording, owned);
    }
    private async Task<HashSet<Guid>> KnownUnsuccessfulJobsAsync(CancellationToken token)
    {
        if (jobs is null)
            return new HashSet<Guid>();
        try
        {
            return (await jobs.LoadAsync(token)).Where(job => job.Status != TranscriptionJobStatus.Succeeded)
                .Select(job => job.Id).ToHashSet();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Job-history review status is unavailable ({FailureType}); retained ownership is unchanged.",
                exception.GetType().Name);
            return new HashSet<Guid>();
        }
    }

    private static bool OwnsOutputPaths(SavedRecording recording, TranscriptOutputs outputs)
    {
        try
        {
            ValidateLiveOutputPaths(recording, outputs);
            return IsOutputPairBesideAudio(recording.AudioPath, outputs) &&
                IsTranscriptStem(recording.AudioPath, outputs.TextPath);
        }
        catch (Exception exception) when (IsArtifactPathFailure(exception))
        {
            return false;
        }
    }

    private static void ValidateLiveOutputPaths(SavedRecording recording, TranscriptOutputs outputs)
    {
        if (!recording.AudioDeleted && !WasArtifactRemoved(recording, recording.AudioPath))
            ValidateArtifactPath(recording.AudioPath);
        if (!WasArtifactRemoved(recording, outputs.TextPath))
            ValidateArtifactPath(outputs.TextPath);
        if (!WasArtifactRemoved(recording, outputs.SubtitlePath))
            ValidateArtifactPath(outputs.SubtitlePath);
    }

    private static bool IsOutputPairBesideAudio(string audioPath, TranscriptOutputs outputs)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(audioPath));
        var text = Path.GetFullPath(outputs.TextPath);
        var subtitle = Path.GetFullPath(outputs.SubtitlePath);
        return string.Equals(directory, Path.GetDirectoryName(text), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(text), ".txt", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.ChangeExtension(text, ".srt"), subtitle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTranscriptStem(string audioPath, string textPath)
    {
        var audioStem = Path.GetFileNameWithoutExtension(audioPath);
        var textStem = Path.GetFileNameWithoutExtension(textPath);
        if (string.Equals(audioStem, textStem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = $"{audioStem}.transcript-";
        var suffix = textStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? textStem.AsSpan(prefix.Length) : ReadOnlySpan<char>.Empty;
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) &&
            attempt >= 2 && suffix.SequenceEqual(attempt.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsArtifactPathFailure(Exception exception) =>
        exception is ArgumentException or NotSupportedException or InvalidOperationException or
            InvalidDataException or IOException or UnauthorizedAccessException;

    private static void ValidateArtifactPath(string path)
    {
        LocalPathPolicy.RejectNetworkDestination(path);
        var fullPath = Path.GetFullPath(path);
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                ValidateArtifactAttributes(attributes, current == fullPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing media is reported separately; existing ancestors still require validation.
            }
        }
    }

    private static void ValidateArtifactAttributes(FileAttributes attributes, bool isArtifact)
    {
        if (isArtifact && (attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException();
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException();
    }

    private async Task<string?> ReadArtifactAsync(
        string path, Guid recordingId, Guid jobId,
        List<LibraryIssue> issues, CancellationToken cancellationToken, bool removed = false)
    {
        if (removed)
        {
            issues.Add(new(LibraryFailureCode.ArtifactMissing,
                "This artifact was removed by the requested deletion. A replacement file is not this transcript.", path, jobId));
            return null;
        }
        return await ReadRetainedArtifactAsync(path, recordingId, jobId, issues, cancellationToken);
    }

    private async Task<string?> ReadRetainedArtifactAsync(
        string path, Guid recordingId, Guid jobId,
        List<LibraryIssue> issues, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordArtifactReadFailure(path, recordingId, jobId, issues, exception);
            return null;
        }
    }

    private void RecordArtifactReadFailure(
        string path, Guid recordingId, Guid jobId, List<LibraryIssue> issues, Exception exception)
    {
        var missing = exception is FileNotFoundException or DirectoryNotFoundException;
        var code = missing ? LibraryFailureCode.ArtifactMissing : LibraryFailureCode.ArtifactReadFailed;
        issues.Add(new(code,
            missing ? "The retained transcript artifact is missing. Restore the original file to review it."
                : "The retained transcript artifact could not be read. Check file access and retry.",
            path, jobId));
        _logger.LogWarning("Transcript read failed for recording {RecordingId}, job {JobId} ({FailureCode}).",
            recordingId, jobId, code);
    }

    private static bool WasArtifactRemoved(SavedRecording recording, string path) =>
        recording.PendingDeletion?.Artifacts.Any(artifact => DeletionFinished(artifact) && SamePath(artifact.Path, path)) == true;

    private Task<LibraryDeletionResult> RejectDeletionAsync(Guid targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning("Library deletion rejected for {TargetId}: orchestration is not available.", targetId);
        return Task.FromResult(new LibraryDeletionResult(targetId, LibraryDeletionStatus.Rejected, null,
            [new(LibraryFailureCode.OrchestrationUnavailable, "Deletion is unavailable until related work can be released.")]));
    }
}
