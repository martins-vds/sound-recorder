using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SoundRecorder.Core;

public sealed class RecoveryManager
{
    private readonly IRecoveryStorage _storage;
    private readonly ILogger<RecoveryManager> _logger;

    public RecoveryManager(
        IRecoveryStorage storage,
        ILogger<RecoveryManager>? logger = null)
    {
        _storage = storage;
        _logger = logger ?? NullLogger<RecoveryManager>.Instance;
    }

    public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(
        CancellationToken cancellationToken = default)
    {
        CoreLog.RecoveryCandidatesRequested(_logger);
        return _storage.FindAsync(cancellationToken);
    }

    public async Task<RecoveredRecordingResult> RecoverAsync(
        RecoveryCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recording = await _storage.RecoverAsync(candidate, cancellationToken);
            CoreLog.RecordingRecovered(_logger, candidate.RecordingId);
            return new RecoveredRecordingResult(recording, null);
        }
        catch (Exception exception)
        {
            CoreLog.RecordingRecoveryFailed(_logger, exception, candidate.RecordingId);
            return new RecoveredRecordingResult(
                null,
                $"The captured audio could not be recovered: {exception.Message}");
        }
    }
}
