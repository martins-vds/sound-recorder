namespace SoundRecorder.Core;

public interface ITranscriptionJobRepository
{
    Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken cancellationToken);
}
