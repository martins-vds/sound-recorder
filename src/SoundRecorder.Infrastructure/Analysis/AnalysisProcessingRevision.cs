using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis;

internal static class AnalysisProcessingRevision
{
    internal static async Task<AnalysisStoreSnapshot> RequireActiveAsync(ITranscriptAnalysisStore store, AnalysisStoreRevision revision)
    {
        var snapshot = await store.LoadAsync(revision.Identity, CancellationToken.None);
        if (snapshot.Unfinished?.Id != revision.Id || snapshot.Status != AnalysisStoreStatus.Processing)
            throw new InvalidOperationException("An explicitly active owned revision is required; complete/stopped work is not retried.");
        return snapshot;
    }
}
