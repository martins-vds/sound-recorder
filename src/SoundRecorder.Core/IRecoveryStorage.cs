namespace SoundRecorder.Core;

public interface IRecoveryStorage
{
    Task<IReadOnlyList<RecoveryCandidate>> FindAsync(CancellationToken cancellationToken);

    Task<SavedRecording> RecoverAsync(
        RecoveryCandidate candidate,
        CancellationToken cancellationToken);

    Task DiscardAsync(RecoveryCandidate candidate, CancellationToken cancellationToken);
}
