using System.Text.Json;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Persistence;

public sealed class JsonTranscriptionJobRepository : ITranscriptionJobRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonTranscriptionJobRepository(string path)
    {
        _path = path;
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
        return states
            .Select(state => new TranscriptionJob(
                state.Id,
                state.Recording,
                state.CreatedAt,
                state.Status,
                state.Outputs,
                state.Error))
            .ToArray();
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
                        job.Error))
                    .ToArray();
                await JsonSerializer.SerializeAsync(stream, states, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
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
        string? Error);
}
