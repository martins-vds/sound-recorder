namespace SoundRecorder.Core;

public sealed class RecoveryManager
{
    private readonly IRecoveryStorage _storage;

    public RecoveryManager(IRecoveryStorage storage)
    {
        _storage = storage;
    }

    public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(
        CancellationToken cancellationToken = default)
    {
        return _storage.FindAsync(cancellationToken);
    }

    public async Task<RecoveredRecordingResult> RecoverAsync(
        RecoveryCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recording = await _storage.RecoverAsync(candidate, cancellationToken);
            return new RecoveredRecordingResult(recording, null);
        }
        catch (Exception exception)
        {
            return new RecoveredRecordingResult(
                null,
                $"The captured audio could not be recovered: {exception.Message}");
        }
    }
}
