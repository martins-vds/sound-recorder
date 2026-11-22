using Microsoft.Extensions.Logging;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Persistence;

public sealed partial class RecordingLibrary
{
    private readonly SemaphoreSlim _deletionGate = new(1, 1);

    public async Task<LibraryDeletionResult> DeleteRecordingAsync(
        Guid recordingId, RecordingDeletionScope scope, CancellationToken cancellationToken)
    {
        await _deletionGate.WaitAsync(cancellationToken);
        try
        {
            return await DeleteRecordingCoreAsync(recordingId, scope, cancellationToken);
        }
        finally
        {
            _deletionGate.Release();
        }
    }

    private async Task<LibraryDeletionResult> DeleteRecordingCoreAsync(
        Guid recordingId, RecordingDeletionScope scope, CancellationToken token)
    {
        DeletionOperation? operation = null;
        try
        {
            var prepared = await PrepareRecordingDeletionAsync(recordingId, scope, token);
            if (prepared.Result is not null)
                return prepared.Result;
            operation = prepared.Operation!;
            return await ExecuteRecordingDeletionAsync(operation, scope, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await RecordCanceledDeletionAsync(operation);
            throw;
        }
        catch (Exception exception) when (ShouldReportRecordingDeletionFailure(exception, token))
        {
            return await RecordingDeletionFailureResultAsync(recordingId, operation, exception);
        }
    }

    private static bool ShouldReportRecordingDeletionFailure(Exception exception, CancellationToken token) =>
        exception is not OperationCanceledException || !token.IsCancellationRequested;

    private async Task<(DeletionOperation? Operation, LibraryDeletionResult? Result)> PrepareRecordingDeletionAsync(
        Guid recordingId, RecordingDeletionScope scope, CancellationToken token)
    {
        var recordings = await history.LoadAsync(token);
        var recording = recordings.FirstOrDefault(item => item.Id == recordingId);
        if (recording is null)
            return (null, new(recordingId, LibraryDeletionStatus.NotFound, null, []));
        var jobHistory = jobs is null ? [] : await jobs.LoadAsync(token);
        recording = RetainSuccessfulOutputs(recording, jobHistory);
        var rejection = RecordingDeletionRejection(recording, scope, recordings, jobHistory);
        if (rejection is not null)
            return (null, rejection);
        if (!CallbacksAvailable(scope))
            return (null, await RejectDeletionAsync(recordingId, token));
        return (new(recording with { PendingDeletion = MergeDeletionPlan(recording, scope, jobHistory) }), null);
    }

    private LibraryDeletionResult? RecordingDeletionRejection(
        SavedRecording recording, RecordingDeletionScope scope,
        IReadOnlyList<SavedRecording> recordings, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var issue = ValidateRecordingOwnership(recording, recordings, jobHistory);
        if (issue is not null)
            return new(recording.Id, LibraryDeletionStatus.Rejected, recording.PendingDeletion, [issue]);
        if (DeletionScopeConflicts(recording, scope))
            return new(recording.Id, LibraryDeletionStatus.Rejected, recording.PendingDeletion,
                [new(LibraryFailureCode.DeletionConflict, "Explicitly retry the original deletion scope before requesting another scope.")]);
        return null;
    }

    private static bool DeletionScopeConflicts(SavedRecording recording, RecordingDeletionScope scope) =>
        !Enum.IsDefined(scope) || (recording.PendingDeletion is { } pending && pending.Scope != scope);

    private async Task<LibraryDeletionResult> ExecuteRecordingDeletionAsync(
        DeletionOperation operation, RecordingDeletionScope scope, CancellationToken token)
    {
        var recordingId = operation.Recording.Id;
        await history.UpdateAsync(operation.Recording, token);
        operation.FailureCode = LibraryFailureCode.WorkerReleaseFailed;
        await cancelRecordingAsync!(recordingId, token);
        await EnsureWorkersReleasedAsync(recordingId, token);
        operation.FailureCode = LibraryFailureCode.PlaybackReleaseFailed;
        await releasePlaybackAsync!(recordingId, token);
        await RefreshDeletionPlanAsync(operation, scope, token);
        await DeletePlanArtifactsAsync(operation, token);
        if (operation.Recording.PendingDeletion!.Artifacts.Any(artifact => !DeletionFinished(artifact)))
            return DeletionResult(operation, LibraryDeletionStatus.Incomplete);
        await CompleteDeletionAsync(operation, token);
        return new(recordingId, LibraryDeletionStatus.Completed, null, []);
    }

    private async Task RecordCanceledDeletionAsync(DeletionOperation? operation)
    {
        if (operation is not null)
            await RecordDeletionFailureAsync(operation, LibraryFailureCode.DeletionCanceled, new OperationCanceledException());
    }

    private async Task<LibraryDeletionResult> RecordingDeletionFailureResultAsync(
        Guid recordingId, DeletionOperation? operation, Exception exception)
    {
        if (operation is not null)
        {
            await RecordDeletionFailureAsync(operation, operation.FailureCode, exception);
            return DeletionResult(operation, operation.FailureCode == LibraryFailureCode.OwnershipInvalid
                ? LibraryDeletionStatus.Rejected : LibraryDeletionStatus.Incomplete);
        }
        var code = exception is InvalidDataException ? LibraryFailureCode.OwnershipInvalid : LibraryFailureCode.PersistenceFailed;
        _logger.LogError("Recording deletion preparation failed for {RecordingId} ({FailureType}).", recordingId, exception.GetType().Name);
        return new(recordingId, LibraryDeletionStatus.Rejected, null,
            [new(code, "Library metadata could not be validated. Check the saved library and explicitly retry.")]);
    }

    public async Task<LibraryDeletionResult> DeleteJobHistoryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _deletionGate.WaitAsync(cancellationToken);
        var failureCode = LibraryFailureCode.PersistenceFailed;
        try
        {
            var prepared = await PrepareJobHistoryDeletionAsync(jobId, cancellationToken);
            if (prepared.Result is not null)
                return prepared.Result;
            var recording = prepared.Recording!;
            await history.UpdateAsync(recording, cancellationToken);
            failureCode = LibraryFailureCode.HistoryDeleteFailed;
            await DeleteOwnedJobHistoryAsync(recording.Id, jobId, cancellationToken);
            return new(jobId, LibraryDeletionStatus.Completed, null, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Job-history deletion failed for {JobId} ({FailureCode}, {FailureType}).",
                jobId, failureCode, exception.GetType().Name);
            return new(jobId, LibraryDeletionStatus.Incomplete, null,
                [new(failureCode, "Job history could not be removed. Check storage access and explicitly retry.", JobId: jobId)]);
        }
        finally
        {
            _deletionGate.Release();
        }
    }

    private async Task<(SavedRecording? Recording, LibraryDeletionResult? Result)> PrepareJobHistoryDeletionAsync(
        Guid jobId, CancellationToken token)
    {
        if (!JobHistoryRemovalAvailable())
            return (null, await RejectDeletionAsync(jobId, token));
        var jobHistory = await jobs!.LoadAsync(token);
        var job = jobHistory.FirstOrDefault(job => job.Id == jobId);
        if (job is null)
            return (null, new(jobId, LibraryDeletionStatus.NotFound, null, []));
        if (!IsTerminal(job))
            return (null, new(jobId, LibraryDeletionStatus.Rejected, null,
                [new(LibraryFailureCode.WorkerReleaseFailed, "Stop or cancel the job and wait for worker release before deleting its history.", JobId: jobId)]));
        var recordings = await history.LoadAsync(token);
        var recording = recordings.FirstOrDefault(recording => recording.Id == job.Recording.Id);
        if (recording is null)
            return (null, new(jobId, LibraryDeletionStatus.Rejected, null,
                [new(LibraryFailureCode.OwnershipInvalid, "The recording entry is required to retain this job's outputs.", JobId: jobId)]));
        recording = RetainSuccessfulOutputs(recording, jobHistory);
        var issue = ValidateRecordingOwnership(recording, recordings, jobHistory);
        if (issue is not null)
            return (null, new(jobId, LibraryDeletionStatus.Rejected, recording.PendingDeletion, [issue]));
        return (recording, null);
    }

    private bool JobHistoryRemovalAvailable() => jobs is not null && deleteJobAsync is not null;

    private static SavedRecording RetainSuccessfulOutputs(
        SavedRecording recording, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var outputs = recording.TranscriptOutputs.ToList();
        foreach (var job in jobHistory.Where(job => job.Recording.Id == recording.Id &&
            job.Status == TranscriptionJobStatus.Succeeded && job.Outputs is not null))
        {
            var existing = outputs.FirstOrDefault(output => output.JobId == job.Id);
            if (existing is null)
            {
                outputs.Add(new(job.Id, job.Outputs!));
            }
            else if (existing.Outputs != job.Outputs)
            {
                throw new InvalidDataException("Historical output ownership conflicts with recording metadata.");
            }
        }
        return recording with { TranscriptOutputs = outputs };
    }

    private static PendingRecordingDeletion CreateDeletionPlan(
        SavedRecording recording, RecordingDeletionScope scope, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var artifacts = new List<ArtifactDeletion>
        {
            new(RecordingArtifactKind.Audio, recording.AudioPath, null,
                recording.AudioDeleted ? ArtifactDeletionState.Removed : ArtifactDeletionState.Pending, null),
        };
        var jobIds = Array.Empty<Guid>();
        if (scope == RecordingDeletionScope.AllArtifacts)
        {
            foreach (var output in FullDeletionOutputPairs(recording, jobHistory))
            {
                artifacts.Add(new(RecordingArtifactKind.Text, output.Outputs.TextPath, output.JobId, ArtifactDeletionState.Pending, null));
                artifacts.Add(new(RecordingArtifactKind.Subtitle, output.Outputs.SubtitlePath, output.JobId, ArtifactDeletionState.Pending, null));
            }
            jobIds = jobHistory.Where(job => job.Recording.Id == recording.Id).Select(job => job.Id)
                .Concat(recording.TranscriptOutputs.Select(output => output.JobId)).Distinct().ToArray();
        }
        return new(Guid.NewGuid(), scope, artifacts, jobIds, null);
    }

    private static IEnumerable<(Guid JobId, TranscriptOutputs Outputs)> FullDeletionOutputPairs(
        SavedRecording recording, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var included = new HashSet<Guid>();
        foreach (var output in recording.TranscriptOutputs)
        {
            included.Add(output.JobId);
            yield return (output.JobId, output.Outputs);
        }
        foreach (var job in jobHistory.Where(job => IsResidualOutputJob(recording.Id, job)))
        {
            if (included.Add(job.Id))
                yield return (job.Id, job.Outputs!);
        }
        foreach (var proof in FinishedResidualPairs(recording, jobHistory))
        {
            if (included.Add(proof.JobId))
                yield return proof;
        }
    }

    private static bool IsResidualOutputJob(Guid recordingId, TranscriptionJob job) =>
        job.Recording.Id == recordingId && job.Status != TranscriptionJobStatus.Succeeded &&
        IsTerminal(job) && job.Outputs is not null;

    private static IEnumerable<(Guid JobId, TranscriptOutputs Outputs)> FinishedResidualPairs(
        SavedRecording recording, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        if (recording.PendingDeletion is not { Scope: RecordingDeletionScope.AllArtifacts } pending)
            yield break;
        foreach (var group in pending.Artifacts.Where(artifact => artifact.JobId is not null)
            .GroupBy(artifact => artifact.JobId!.Value))
        {
            var pair = ReconstructFinishedResidualPair(recording, jobHistory, group);
            if (pair is not null)
                yield return (group.Key, pair);
        }
    }

    private static TranscriptOutputs? ReconstructFinishedResidualPair(
        SavedRecording recording, IReadOnlyList<TranscriptionJob> jobHistory,
        IGrouping<Guid, ArtifactDeletion> receipts)
    {
        if (jobHistory.Any(job => job.Id == receipts.Key) || receipts.Any(artifact => !DeletionFinished(artifact)))
            return null;
        var text = receipts.SingleOrDefault(artifact => artifact.Kind == RecordingArtifactKind.Text);
        var subtitles = receipts.SingleOrDefault(artifact => artifact.Kind == RecordingArtifactKind.Subtitle);
        if (text is null || subtitles is null)
            return null;
        var pair = new TranscriptOutputs(text.Path, subtitles.Path);
        return OwnsOutputPaths(recording, pair) ? pair : null;
    }

    private async Task EnsureWorkersReleasedAsync(Guid recordingId, CancellationToken token)
    {
        if (jobs is not null && (await jobs.LoadAsync(token)).Any(job =>
            job.Recording.Id == recordingId && !IsTerminal(job)))
        {
            throw new InvalidOperationException("Related work has not been released.");
        }
    }

    private static bool IsTerminal(TranscriptionJob job) =>
        job.Status is not (TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running) && !job.IsTerminationPending;

    private async Task DeletePlanArtifactsAsync(DeletionOperation operation, CancellationToken token)
    {
        var artifacts = operation.Recording.PendingDeletion!.Artifacts.ToArray();
        for (var index = 0; index < artifacts.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (DeletionFinished(artifacts[index]))
                continue;
            await EnsureCurrentOwnershipAsync(operation, token);
            artifacts[index] = DeleteArtifact(operation.Recording.Id, artifacts[index]);
            operation.Recording = operation.Recording with
            {
                AudioDeleted = operation.Recording.AudioDeleted ||
                    (artifacts[index].Kind == RecordingArtifactKind.Audio && DeletionFinished(artifacts[index])),
                PendingDeletion = operation.Recording.PendingDeletion with { Artifacts = artifacts.ToArray() },
            };
            operation.FailureCode = LibraryFailureCode.PersistenceFailed;
            await history.UpdateAsync(operation.Recording, CancellationToken.None);
        }
    }

    private async Task EnsureCurrentOwnershipAsync(DeletionOperation operation, CancellationToken token)
    {
        operation.FailureCode = LibraryFailureCode.PersistenceFailed;
        var recordings = await history.LoadAsync(token);
        var jobHistory = jobs is null ? [] : await jobs.LoadAsync(token);
        var recording = recordings.FirstOrDefault(item => item.Id == operation.Recording.Id)
            ?? throw new InvalidDataException("Recording ownership disappeared during deletion.");
        operation.FailureCode = LibraryFailureCode.OwnershipInvalid;
        if (ValidateRecordingOwnership(recording, recordings, jobHistory) is not null)
            throw new InvalidDataException("Recording ownership changed during deletion.");
    }

    private async Task CompleteDeletionAsync(DeletionOperation operation, CancellationToken token)
    {
        var recording = operation.Recording;
        if (recording.PendingDeletion!.Scope == RecordingDeletionScope.AudioOnly)
        {
            operation.FailureCode = LibraryFailureCode.PersistenceFailed;
            await history.UpdateAsync(recording with { PendingDeletion = null }, CancellationToken.None);
            return;
        }

        foreach (var jobId in recording.PendingDeletion.RemainingJobIds.ToArray())
        {
            operation.FailureCode = LibraryFailureCode.HistoryDeleteFailed;
            await DeleteOwnedJobHistoryAsync(recording.Id, jobId, token);
            recording = recording with
            {
                PendingDeletion = recording.PendingDeletion with
                {
                    RemainingJobIds = recording.PendingDeletion.RemainingJobIds.Where(id => id != jobId).ToArray(),
                },
            };
            operation.Recording = recording;
            operation.FailureCode = LibraryFailureCode.PersistenceFailed;
            await history.UpdateAsync(recording, CancellationToken.None);
        }
        operation.FailureCode = LibraryFailureCode.PersistenceFailed;
        await history.RemoveAsync(recording.Id, CancellationToken.None);
    }

    private async Task DeleteOwnedJobHistoryAsync(Guid recordingId, Guid jobId, CancellationToken token)
    {
        var jobHistory = await jobs!.LoadAsync(token);
        var job = jobHistory.FirstOrDefault(item => item.Id == jobId);
        if (job is null)
            return;
        if (job.Recording.Id != recordingId || !IsTerminal(job))
            throw new InvalidDataException("Job ownership or terminal worker-release state changed.");
        await deleteJobAsync!(jobId, token);
        if ((await jobs.LoadAsync(token)).Any(item => item.Id == jobId))
            throw new IOException("Job-history removal was not durably persisted.");
    }

    private static PendingRecordingDeletion MergeDeletionPlan(
        SavedRecording recording, RecordingDeletionScope scope, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var expected = CreateDeletionPlan(recording, scope, jobHistory);
        var existing = recording.PendingDeletion;
        if (existing is null)
            return expected;
        if (existing.Artifacts.Any(artifact => !expected.Artifacts.Any(candidate => SameArtifact(candidate, artifact))))
            throw new InvalidDataException("Pending deletion contains an unowned artifact reference.");
        var knownOutputJobs = existing.Artifacts.Where(artifact => artifact.JobId is not null)
            .Select(artifact => artifact.JobId!.Value).ToHashSet();
        return expected with
        {
            OperationId = existing.OperationId,
            Artifacts = expected.Artifacts.Select(artifact => DeletionFinished(artifact) ? artifact :
                existing.Artifacts.FirstOrDefault(previous => SameArtifact(previous, artifact)) ?? artifact).ToArray(),
            RemainingJobIds = existing.RemainingJobIds.Concat(expected.RemainingJobIds
                .Where(id => !knownOutputJobs.Contains(id))).Distinct().ToArray(),
        };
    }

    private static bool SameArtifact(ArtifactDeletion first, ArtifactDeletion second) =>
        first.Kind == second.Kind && first.JobId == second.JobId && SamePath(first.Path, second.Path);

    private async Task RefreshDeletionPlanAsync(
        DeletionOperation operation, RecordingDeletionScope scope, CancellationToken token)
    {
        operation.FailureCode = LibraryFailureCode.PersistenceFailed;
        var recordings = await history.LoadAsync(token);
        var jobHistory = jobs is null ? [] : await jobs.LoadAsync(token);
        var recording = recordings.FirstOrDefault(item => item.Id == operation.Recording.Id)
            ?? throw new InvalidDataException("Recording ownership disappeared during release.");
        recording = RetainSuccessfulOutputs(recording, jobHistory);
        operation.FailureCode = LibraryFailureCode.OwnershipInvalid;
        if (ValidateRecordingOwnership(recording, recordings, jobHistory) is not null)
            throw new InvalidDataException("Recording ownership changed during release.");
        operation.Recording = recording with { PendingDeletion = MergeDeletionPlan(recording, scope, jobHistory) };
        operation.FailureCode = LibraryFailureCode.PersistenceFailed;
        await history.UpdateAsync(operation.Recording, CancellationToken.None);
    }

    private ArtifactDeletion DeleteArtifact(Guid recordingId, ArtifactDeletion artifact)
    {
        try
        {
            File.GetAttributes(artifact.Path);
            File.Delete(artifact.Path);
            return artifact with { State = ArtifactDeletionState.Removed, Failure = null };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return artifact with { State = ArtifactDeletionState.AlreadyMissing, Failure = null };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Artifact deletion failed for recording {RecordingId}, job {JobId} ({FailureType}).",
                recordingId, artifact.JobId, exception.GetType().Name);
            return artifact with
            {
                State = ArtifactDeletionState.Failed,
                Failure = new(LibraryFailureCode.ArtifactDeleteFailed,
                    "The artifact could not be removed. Close programs holding it, check file access, and explicitly retry.",
                    artifact.Path, artifact.JobId),
            };
        }
    }

    private static bool DeletionFinished(ArtifactDeletion artifact) =>
        artifact.State is ArtifactDeletionState.Removed or ArtifactDeletionState.AlreadyMissing;

    private static LibraryDeletionResult DeletionResult(DeletionOperation operation, LibraryDeletionStatus status)
    {
        var pending = operation.Recording.PendingDeletion!;
        var issues = pending.Artifacts.Where(artifact => artifact.Failure is not null)
            .Select(artifact => artifact.Failure!).ToList();
        if (pending.Failure is not null)
            issues.Add(pending.Failure);
        issues.AddRange(operation.AdditionalIssues);
        return new(operation.Recording.Id, status, pending, issues);
    }

    private async Task RecordDeletionFailureAsync(
        DeletionOperation operation, LibraryFailureCode code, Exception exception)
    {
        var message = DeletionFailureMessage(code);
        operation.Recording = operation.Recording with
        {
            PendingDeletion = operation.Recording.PendingDeletion! with { Failure = new(code, message) },
        };
        _logger.LogError("Recording deletion failed for {RecordingId} ({FailureCode}, {FailureType}).",
            operation.Recording.Id, code, exception.GetType().Name);
        try
        {
            await history.UpdateAsync(operation.Recording, CancellationToken.None);
        }
        catch (Exception persistenceException)
        {
            operation.AdditionalIssues.Add(new(LibraryFailureCode.PersistenceFailed,
                "The latest deletion outcome could not be persisted. Check library storage before explicitly retrying."));
            _logger.LogError("Deletion failure persistence failed for {RecordingId} ({FailureType}).",
                operation.Recording.Id, persistenceException.GetType().Name);
        }
    }

    private static string DeletionFailureMessage(LibraryFailureCode code) => code switch
    {
        LibraryFailureCode.WorkerReleaseFailed => "Related transcription work could not be released. Stop blocked work and explicitly retry; no further artifact will be removed.",
        LibraryFailureCode.PlaybackReleaseFailed => "Playback could not be closed. Close playback and explicitly retry; no further artifact will be removed.",
        LibraryFailureCode.HistoryDeleteFailed => "Associated job history could not be removed. Wait for worker release, check storage access, and explicitly retry.",
        LibraryFailureCode.OwnershipInvalid => "Artifact ownership changed or could not be verified. Check the original paths and shared references before explicitly retrying.",
        LibraryFailureCode.DeletionCanceled => "Deletion was interrupted. Review the remaining artifacts and explicitly retry.",
        _ => "Deletion progress could not be saved. Check library storage access and explicitly retry; no further artifact will be removed.",
    };

    private sealed class DeletionOperation(SavedRecording recording)
    {
        public SavedRecording Recording { get; set; } = recording;
        public LibraryFailureCode FailureCode { get; set; } = LibraryFailureCode.PersistenceFailed;
        public List<LibraryIssue> AdditionalIssues { get; } = [];
    }

    private bool CallbacksAvailable(RecordingDeletionScope scope) =>
        RecordingReleaseAvailable() && HistoryRemovalAvailable(scope);

    private bool RecordingReleaseAvailable() =>
        cancelRecordingAsync is not null && releasePlaybackAsync is not null;

    private bool HistoryRemovalAvailable(RecordingDeletionScope scope) =>
        scope == RecordingDeletionScope.AudioOnly || (deleteJobAsync is not null && jobs is not null);

    private LibraryIssue? ValidateRecordingOwnership(
        SavedRecording recording, IReadOnlyList<SavedRecording> recordings,
        IReadOnlyList<TranscriptionJob> jobHistory)
    {
        try
        {
            ValidateAudioOwnership(recording);
            ValidateTranscriptOwnership(recording);
            ValidateRelatedJobOwnership(recording, jobHistory);
            ValidatePendingOwnership(recording, recordings, jobHistory);
            ValidateExclusiveArtifactOwnership(recording, recordings, jobHistory);
            return null;
        }
        catch (Exception exception) when (IsArtifactPathFailure(exception))
        {
            _logger.LogWarning("Deletion ownership validation failed for recording {RecordingId} ({FailureType}).",
                recording.Id, exception.GetType().Name);
            return new(LibraryFailureCode.OwnershipInvalid,
                "Artifact ownership could not be verified. Check the original recording, output paths, and shared references.");
        }
    }

    private static void ValidateAudioOwnership(SavedRecording recording)
    {
        if (!Path.IsPathFullyQualified(recording.AudioPath) ||
            !string.Equals(Path.GetExtension(recording.AudioPath), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid recording audio reference.");
        if (!recording.AudioDeleted && !WasArtifactRemoved(recording, recording.AudioPath))
            ValidateArtifactPath(recording.AudioPath);
    }

    private static void ValidateTranscriptOwnership(SavedRecording recording)
    {
        foreach (var output in recording.TranscriptOutputs)
        {
            if (!OwnsOutputPaths(recording, output.Outputs))
                throw new InvalidDataException("Invalid transcript output reference.");
        }
    }

    private static void ValidateRelatedJobOwnership(
        SavedRecording recording, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        foreach (var job in jobHistory.Where(job => job.Recording.Id == recording.Id))
        {
            if (!SamePath(job.Recording.AudioPath, recording.AudioPath) ||
                (job.Outputs is not null && !OwnsOutputPaths(recording, job.Outputs)))
                throw new InvalidDataException("Invalid related job artifact reference.");
        }
    }

    private static void ValidateExclusiveArtifactOwnership(
        SavedRecording recording, IReadOnlyList<SavedRecording> recordings, IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var ownedPaths = RecordingPaths(recording).Concat(
            FullDeletionOutputPairs(recording, jobHistory).SelectMany(output => new[] { output.Outputs.TextPath, output.Outputs.SubtitlePath })
                .Where(path => !WasArtifactRemoved(recording, path)).Select(Path.GetFullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignPaths = recordings.Where(item => item.Id != recording.Id).SelectMany(RecordingPaths)
            .Concat(jobHistory.Where(job => job.Recording.Id != recording.Id)
                .SelectMany(job => JobPaths(job)));
        if (foreignPaths.Any(path => ownedPaths.Contains(Path.GetFullPath(path))))
            throw new InvalidDataException("Another recording references this artifact.");
    }

    private static void ValidatePendingOwnership(
        SavedRecording recording, IReadOnlyList<SavedRecording> recordings,
        IReadOnlyList<TranscriptionJob> jobHistory)
    {
        var pending = recording.PendingDeletion;
        if (pending is null)
            return;
        var expected = CreateDeletionPlan(recording, pending.Scope, jobHistory);
        if (pending.OperationId == Guid.Empty ||
            pending.Artifacts.Any(artifact => !expected.Artifacts.Any(candidate => SameArtifact(candidate, artifact))))
            throw new InvalidDataException("Pending deletion contains an unowned artifact.");
        var foreignJobs = jobHistory.Where(job => job.Recording.Id != recording.Id).Select(job => job.Id)
            .Concat(recordings.Where(item => item.Id != recording.Id)
                .SelectMany(item => item.TranscriptOutputs.Select(output => output.JobId))).ToHashSet();
        if (pending.RemainingJobIds.Any(id => id == Guid.Empty || foreignJobs.Contains(id)))
            throw new InvalidDataException("Pending deletion contains an unowned job-history reference.");
    }

    private static IEnumerable<string> RecordingPaths(SavedRecording recording)
    {
        if (!recording.AudioDeleted && !WasArtifactRemoved(recording, recording.AudioPath))
            yield return Path.GetFullPath(recording.AudioPath);
        foreach (var output in recording.TranscriptOutputs)
        {
            if (!WasArtifactRemoved(recording, output.Outputs.TextPath))
                yield return Path.GetFullPath(output.Outputs.TextPath);
            if (!WasArtifactRemoved(recording, output.Outputs.SubtitlePath))
                yield return Path.GetFullPath(output.Outputs.SubtitlePath);
        }
    }

    private static IEnumerable<string> JobPaths(TranscriptionJob job)
    {
        yield return Path.GetFullPath(job.Recording.AudioPath);
        if (job.Outputs is not null)
        {
            yield return Path.GetFullPath(job.Outputs.TextPath);
            yield return Path.GetFullPath(job.Outputs.SubtitlePath);
        }
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
