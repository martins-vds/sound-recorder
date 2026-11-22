using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;

namespace SoundRecorder.Infrastructure.Persistence;

public sealed class JsonTranscriptionJobRepository : ITranscriptionJobRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ILogger<JsonTranscriptionJobRepository> _logger;

    public JsonTranscriptionJobRepository(
        string path,
        ILogger<JsonTranscriptionJobRepository>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<JsonTranscriptionJobRepository>.Instance;
    }

    public async Task<IReadOnlyList<TranscriptionJob>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try { return await ReadSnapshotAsync(cancellationToken); }
        finally { _saveGate.Release(); }
    }

    private async Task<IReadOnlyList<TranscriptionJob>> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var stream = OpenSnapshot();
        if (stream is null)
        {
            return [];
        }

        var states = await JsonSerializer.DeserializeAsync<List<TranscriptionJobState>>(
            stream,
            JsonOptions,
            cancellationToken) ?? [];
        var jobs = states
            .Select(state => new TranscriptionJob(
                state.Id,
                state.Recording,
                state.CreatedAt,
                state.Status,
                state.Outputs,
                state.Error,
                state.ModelId,
                state.InterruptionReason))
            .ToArray();
        InfrastructureLog.TranscriptionJobsLoaded(_logger, jobs.Length);
        return jobs;
    }

    private FileStream? OpenSnapshot()
    {
        try { return File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task SaveAsync(
        IReadOnlyList<TranscriptionJob> jobs,
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    var states = jobs
                        .Select(job => new TranscriptionJobState(
                            job.Id,
                            job.Recording,
                            job.Status,
                            job.CreatedAt,
                            job.Outputs,
                            job.Error,
                            job.ModelId,
                            job.InterruptionReason))
                        .ToArray();
                    await JsonSerializer.SerializeAsync(stream, states, JsonOptions, cancellationToken);
                }

                AtomicSnapshotPublication.Publish(temporaryPath, _path);
                InfrastructureLog.TranscriptionJobsPersisted(_logger, jobs.Count);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private sealed record TranscriptionJobState(
        Guid Id,
        SavedRecording Recording,
        TranscriptionJobStatus Status,
        DateTimeOffset CreatedAt,
        TranscriptOutputs? Outputs,
        string? Error,
        string? ModelId = null,
        string? InterruptionReason = null);
}
