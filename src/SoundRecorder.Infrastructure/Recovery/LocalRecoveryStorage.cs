using System.Text.Json;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;

namespace SoundRecorder.Infrastructure.Recovery;

public sealed class LocalRecoveryStorage : IRecoveryStorage
{
    private readonly string _stateDirectory;

    public LocalRecoveryStorage(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
    }

    public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_stateDirectory))
        {
            return Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);
        }

        var candidates = Directory
            .EnumerateFiles(_stateDirectory, "*.capture.json")
            .Select(ReadCandidate)
            .Where(candidate => candidate is not null)
            .Cast<RecoveryCandidate>()
            .ToArray();
        return Task.FromResult<IReadOnlyList<RecoveryCandidate>>(candidates);
    }

    public async Task<SavedRecording> RecoverAsync(
        RecoveryCandidate candidate,
        CancellationToken cancellationToken)
    {
        var checkpoint = await ReadCheckpointAsync(candidate.CapturedAudioPath, cancellationToken);
        var sourcePaths = new[] { checkpoint.MicrophonePath, checkpoint.SystemAudioPath }
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            throw new InvalidDataException("No captured audio is available to recover.");
        }

        foreach (var path in sourcePaths)
        {
            RepairWaveHeader(path);
        }

        var outputPath = CreateUniqueRecoveryPath(checkpoint);
        WaveRecordingFinalizer.Finalize(
            checkpoint.MicrophonePath,
            checkpoint.SystemAudioPath,
            checkpoint.Source,
            outputPath);
        var duration = sourcePaths
            .Select(GetWaveDuration)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        await File.WriteAllTextAsync(
            outputPath + ".recovered.json",
            JsonSerializer.Serialize(new { checkpoint.RecordingId }),
            cancellationToken);
        await DiscardSourcesAsync(checkpoint, candidate.CapturedAudioPath, cancellationToken);
        return new SavedRecording(
            checkpoint.RecordingId,
            outputPath,
            checkpoint.Language,
            RecordingOutcome.Recovered,
            duration);
    }

    public Task DiscardAsync(
        RecoveryCandidate candidate,
        CancellationToken cancellationToken)
    {
        var checkpointPath = candidate.CapturedAudioPath;
        var checkpoint = JsonSerializer.Deserialize<CaptureCheckpoint>(
            File.ReadAllText(checkpointPath))
            ?? throw new InvalidDataException("The recovery checkpoint is invalid.");
        return DiscardSourcesAsync(checkpoint, checkpointPath, cancellationToken);
    }

    private static Task DiscardSourcesAsync(
        CaptureCheckpoint checkpoint,
        string checkpointPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfExists(checkpoint.MicrophonePath);
        DeleteIfExists(checkpoint.SystemAudioPath);
        DeleteIfExists(checkpointPath);
        return Task.CompletedTask;
    }

    private static RecoveryCandidate? ReadCandidate(string path)
    {
        try
        {
            var checkpoint = JsonSerializer.Deserialize<CaptureCheckpoint>(
                File.ReadAllText(path));
            return checkpoint is null
                ? null
                : new RecoveryCandidate(
                    checkpoint.RecordingId,
                    path,
                    checkpoint.Language,
                    TimeSpan.FromTicks(checkpoint.CapturedTicks),
                    checkpoint.LastSavedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<CaptureCheckpoint> ReadCheckpointAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CaptureCheckpoint>(
                   stream,
                   cancellationToken: cancellationToken)
               ?? throw new InvalidDataException("The recovery checkpoint is invalid.");
    }

    private static void RepairWaveHeader(string path)
    {
        if (new FileInfo(path).Length < 44)
        {
            throw new InvalidDataException(
                $"The capture '{Path.GetFileName(path)}' has no recoverable WAV data.");
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        var riff = new string(reader.ReadChars(4));
        stream.Position = 8;
        var wave = new string(reader.ReadChars(4));
        if (riff != "RIFF" || wave != "WAVE")
        {
            throw new InvalidDataException(
                $"The capture '{Path.GetFileName(path)}' is not a WAV file.");
        }

        stream.Position = 12;
        var dataPosition = -1L;
        while (stream.Position <= stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkLength = reader.ReadUInt32();
            var chunkDataPosition = stream.Position;
            if (chunkId == "data")
            {
                dataPosition = chunkDataPosition;
                break;
            }

            stream.Position = checked(chunkDataPosition + chunkLength + (chunkLength % 2));
        }

        if (dataPosition < 0)
        {
            throw new InvalidDataException(
                $"The capture '{Path.GetFileName(path)}' has no WAV data chunk.");
        }

        var dataLength = checked((int)(stream.Length - dataPosition));
        stream.Position = 4;
        stream.Write(BitConverter.GetBytes(checked((int)(stream.Length - 8))));
        stream.Position = dataPosition - 4;
        stream.Write(BitConverter.GetBytes(dataLength));
    }

    private static TimeSpan GetWaveDuration(string path)
    {
        using var reader = new NAudio.Wave.WaveFileReader(path);
        return reader.TotalTime;
    }

    private static string CreateUniqueRecoveryPath(CaptureCheckpoint checkpoint)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var title = string.IsNullOrWhiteSpace(checkpoint.Title)
            ? string.Empty
            : $"-{RecordingFileName.Sanitize(checkpoint.Title)}";
        var basePath = Path.Combine(
            checkpoint.DestinationFolder,
            $"{timestamp}{title}-recovered");
        var path = $"{basePath}.wav";
        for (var attempt = 1; File.Exists(path); attempt++)
        {
            path = $"{basePath}-{attempt}.wav";
        }

        return path;
    }

    private static void DeleteIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
