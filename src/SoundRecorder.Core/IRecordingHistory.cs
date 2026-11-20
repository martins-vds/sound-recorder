namespace SoundRecorder.Core;

public interface IRecordingHistory
{
    Task<IReadOnlyList<SavedRecording>> LoadAsync(
        CancellationToken cancellationToken);

    Task AddAsync(
        SavedRecording recording,
        CancellationToken cancellationToken);
}
