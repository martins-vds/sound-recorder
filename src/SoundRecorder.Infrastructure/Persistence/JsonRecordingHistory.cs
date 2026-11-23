using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;

namespace SoundRecorder.Infrastructure.Persistence;

public sealed class JsonRecordingHistory : IRecordingHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonRecordingHistory> _logger;

    public JsonRecordingHistory(
        string path,
        ILogger<JsonRecordingHistory>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<JsonRecordingHistory>.Instance;
    }

    public async Task<IReadOnlyList<SavedRecording>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        await ChangeAsync(history =>
        {
            history.Add(recording);
            return true;
        }, cancellationToken);
        InfrastructureLog.RecordingHistorySaved(_logger, recording.Id);
    }

    public async Task UpdateAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        await ChangeAsync(history =>
        {
            var index = history.FindIndex(item => item.Id == recording.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException("The recording library entry no longer exists.");
            }

            var previous = history[index];
            history[index] = recording with
            {
                AudioDeleted = previous.AudioDeleted || recording.AudioDeleted,
                PendingDeletion = recording.PendingDeletion ??
                    (NeedsDeletionCompletion(previous.PendingDeletion) ? previous.PendingDeletion : null),
                TranscriptOutputs = previous.TranscriptOutputs
                    .Concat(recording.TranscriptOutputs).Distinct().ToArray(),
            };
            return true;
        }, cancellationToken);
    }

    public Task RemoveAsync(Guid recordingId, CancellationToken cancellationToken) =>
        ChangeAsync(history => history.RemoveAll(recording => recording.Id == recordingId) > 0,
            cancellationToken);

    public Task RegisterOutputsAsync(
        Guid recordingId,
        OwnedTranscriptOutputs outputs,
        CancellationToken cancellationToken) =>
        ChangeAsync(history => RegisterOutputPair(history, recordingId, outputs), cancellationToken);

    private static bool RegisterOutputPair(
        List<SavedRecording> history, Guid recordingId, OwnedTranscriptOutputs outputs)
    {
        var index = history.FindIndex(recording => recording.Id == recordingId);
        if (index < 0)
        {
            throw new KeyNotFoundException("The recording library entry no longer exists.");
        }

        var recording = history[index];
        var existing = recording.TranscriptOutputs.FirstOrDefault(item => item.JobId == outputs.JobId);
        if (existing is not null)
        {
            EnsureSameOutputPair(existing, outputs);
            if (existing.Language is not null || outputs.Language is null) return false;
            history[index] = recording with
            {
                TranscriptOutputs = recording.TranscriptOutputs.Select(item => item.JobId == outputs.JobId
                    ? item with { Language = outputs.Language } : item).ToArray()
            };
            return true;
        }

        EnsurePublicationAvailable(recording);
        history[index] = recording with
        {
            TranscriptOutputs = [.. recording.TranscriptOutputs, outputs],
        };
        return true;
    }

    private static void EnsureSameOutputPair(OwnedTranscriptOutputs existing, OwnedTranscriptOutputs outputs)
    {
        if (existing.Outputs != outputs.Outputs ||
            existing.Language is not null && outputs.Language is not null && existing.Language != outputs.Language)
            throw new InvalidDataException("The job already owns a different transcript output pair.");
    }

    private static void EnsurePublicationAvailable(SavedRecording recording)
    {
        if (recording.AudioDeleted || recording.PendingDeletion is not null)
            throw new InvalidOperationException("New output publication is unavailable while recording deletion is pending or audio is deleted.");
    }

    private async Task ChangeAsync(
        Func<List<SavedRecording>, bool> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = (await ReadAsync(cancellationToken)).ToList();
            if (!change(history))
            {
                return;
            }

            Validate(history);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new HistoryState(1, history),
                        JsonOptions,
                        cancellationToken: cancellationToken);
                }

                AtomicSnapshotPublication.Publish(temporaryPath, _path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SavedRecording>> ReadAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = OpenHistoryIfPresent();
        if (stream is null)
        {
            return [];
        }

        var root = await JsonSerializer.DeserializeAsync<JsonElement>(
            stream, cancellationToken: cancellationToken);
        if (root.ValueKind == JsonValueKind.Array)
        {
            return Validate(root.Deserialize<List<SavedRecording>>(JsonOptions)!);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Recording history must be a legacy array or a supported versioned library.");
        }

        var state = root.Deserialize<HistoryState>(JsonOptions)!;
        if (state.Version != 1)
        {
            throw new JsonException("The recording library version is not supported.");
        }

        return Validate(state.Recordings);
    }

    private FileStream? OpenHistoryIfPresent()
    {
        try
        {
            return File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static IReadOnlyList<SavedRecording> Validate(IReadOnlyList<SavedRecording> recordings)
    {
        var ids = new HashSet<Guid>();
        foreach (var recording in recordings)
        {
            ValidateRecording(recording, ids);
        }

        var jobIds = new HashSet<Guid>();
        var paths = recordings.Select(recording => Path.GetFullPath(recording.AudioPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var recording in recordings)
        {
            foreach (var output in recording.TranscriptOutputs)
            {
                ValidateOutput(output);
                RegisterOwnership(output, jobIds, paths);
            }

            ValidatePendingDeletion(recording.PendingDeletion);
        }

        return recordings;
    }

    private static void ValidateRecording(SavedRecording recording, HashSet<Guid> ids)
    {
        if (recording is null || recording.Id == Guid.Empty || !ids.Add(recording.Id))
        {
            throw new JsonException("Recording history contains an invalid or duplicate recording.");
        }

        ValidateRecordingMetadata(recording);
    }

    private static void ValidateRecordingMetadata(SavedRecording recording)
    {
        if (string.IsNullOrWhiteSpace(recording.AudioPath) || string.IsNullOrWhiteSpace(recording.Language) ||
            !Enum.IsDefined(recording.Outcome) || recording.CapturedDuration < TimeSpan.Zero)
        {
            throw new JsonException("Recording history contains invalid recording metadata.");
        }
    }

    private static void ValidateOutput(OwnedTranscriptOutputs output)
    {
        if (output is null || output.JobId == Guid.Empty || output.Outputs is null ||
            string.IsNullOrWhiteSpace(output.Outputs.TextPath) || string.IsNullOrWhiteSpace(output.Outputs.SubtitlePath))
        {
            throw new JsonException("Recording history contains an invalid transcript output pair.");
        }
    }

    private static void RegisterOwnership(
        OwnedTranscriptOutputs output, HashSet<Guid> jobIds, HashSet<string> paths)
    {
        if (!jobIds.Add(output.JobId) || !paths.Add(Path.GetFullPath(output.Outputs.TextPath)) ||
            !paths.Add(Path.GetFullPath(output.Outputs.SubtitlePath)))
        {
            throw new InvalidDataException("A job or artifact is already owned by another output pair or recording.");
        }
    }

    private static void ValidatePendingDeletion(PendingRecordingDeletion? pending)
    {
        if (pending is null)
            return;
        ValidatePendingArtifactKinds(pending);
        ValidateAudioOnlyHistory(pending);
        ValidateUniqueArtifactOutcomes(pending);
    }

    private static void ValidatePendingArtifactKinds(PendingRecordingDeletion pending)
    {
        if (!Enum.IsDefined(pending.Scope) || pending.Artifacts.Any(HasUnknownArtifactKind))
        {
            throw new JsonException("Recording history contains unknown pending-deletion metadata.");
        }
    }

    private static bool HasUnknownArtifactKind(ArtifactDeletion artifact) =>
        artifact is null || !Enum.IsDefined(artifact.Kind) || !Enum.IsDefined(artifact.State);

    private static void ValidateAudioOnlyHistory(PendingRecordingDeletion pending)
    {
        if (pending.Scope == RecordingDeletionScope.AudioOnly && pending.RemainingJobIds.Count != 0)
            throw new JsonException("Audio-only deletion cannot remove job history.");
    }

    private static void ValidateUniqueArtifactOutcomes(PendingRecordingDeletion pending)
    {
        if (pending.Artifacts.Select(artifact => Path.GetFullPath(artifact.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.Artifacts.Count)
            throw new JsonException("Pending deletion contains duplicate artifact outcomes.");
    }

    private static bool NeedsDeletionCompletion(PendingRecordingDeletion? pending) =>
        pending is not null && (pending.Scope == RecordingDeletionScope.AllArtifacts ||
            pending.Artifacts.Any(artifact => artifact.State is ArtifactDeletionState.Pending or ArtifactDeletionState.Failed));

    private void LogFailure(Exception exception) =>
        _logger.LogError("Recording library storage failed ({FailureType}).", exception.GetType().Name);

    private sealed record HistoryState(int Version, IReadOnlyList<SavedRecording> Recordings);
}
