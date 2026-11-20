using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure;

namespace SoundRecorder.Infrastructure.Persistence;

public sealed class JsonRecordingHistory : IRecordingHistory
{
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
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = (await ReadAsync(cancellationToken)).ToList();
            history.Add(recording);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    history,
                    cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            InfrastructureLog.RecordingHistorySaved(_logger, recording.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SavedRecording>> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<SavedRecording>>(
                   stream,
                   cancellationToken: cancellationToken)
               ?? [];
    }
}
