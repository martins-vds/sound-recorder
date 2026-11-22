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
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = File.OpenRead(_path);
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

    public async Task SaveAsync(
        IReadOnlyList<TranscriptionJob> jobs,
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
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

            File.Move(temporaryPath, _path, overwrite: true);
            InfrastructureLog.TranscriptionJobsPersisted(_logger, jobs.Count);
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
