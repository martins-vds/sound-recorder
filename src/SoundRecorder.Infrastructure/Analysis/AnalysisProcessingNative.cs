using System.Security.Cryptography;
using System.Text;
using SoundRecorder.Analysis;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Privacy;

namespace SoundRecorder.Infrastructure.Analysis;

/// <summary>Loads only the job's pinned installed Phi-mini/Nomic profile, never the selected global setup.</summary>
public sealed class AnalysisProcessingNative(ITranscriptAnalysisStore store, AnalysisStoreRevision revision,
    string modelDirectory, string artifactDirectory, AnalysisProcessingMode mode,
    TranscriptDiscussionContext? discussion = null, Func<CancellationToken, Task<IDisposable>>? enterInferenceAsync = null,
    NativeModelLoader? loader = null, ModelFileVerifier? verifier = null) : ILocalTranscriptAnalysis
{
    private readonly string directory = Path.GetFullPath(modelDirectory);

    /// <summary>Builds the exact initial algorithm/runtime/configuration identity before a job is queued.</summary>
    public static async Task<AnalysisStoreIdentity> PinAsync(TranscriptAnalysisRequest request, CancellationToken token)
    {
        LocalPathPolicy.RejectNetworkDestination(request.Outputs.TextPath);
        LocalPathPolicy.RejectNetworkDestination(request.Outputs.SubtitlePath);
        await using var text = File.OpenRead(request.Outputs.TextPath);
        await using var subtitles = File.OpenRead(request.Outputs.SubtitlePath);
        return new(request.AttemptId, Convert.ToHexString(await SHA256.HashDataAsync(text, token)),
            Convert.ToHexString(await SHA256.HashDataAsync(subtitles, token)), request.Language,
            GenerationModel(), EmbeddingModel(), "processing-v1", 768);
    }

    /// <inheritdoc />
    public async Task<TranscriptAnalysisResult> AnalyzeAsync(TranscriptAnalysisRequest request,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        _ = await AnalysisProcessingRevision.RequireActiveAsync(store, revision);
        try
        {
            LocalPathPolicy.RejectNetworkDestination(directory);
            if (revision.Identity != await PinAsync(request, cancellationToken))
                throw new InvalidDataException("Pinned source/model/configuration is incompatible; explicit rebuild is required.");
            progress.Report("Verifying the job's pinned installed model profile; no downloads");
            using var generation = await LoadGenerationAsync(cancellationToken);
            using var embedding = await LoadEmbeddingAsync(cancellationToken);
            return await new AnalysisProcessingService(store, revision, artifactDirectory, generation, embedding, mode,
                discussion, enterInferenceAsync).AnalyzeAsync(request, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Cancelled, CancellationToken.None);
            throw;
        }
        catch
        {
            await store.SetUnfinishedAsync(revision, AnalysisStoreStatus.Failed, CancellationToken.None);
            throw;
        }
    }

    private async Task<LlamaGeneration> LoadGenerationAsync(CancellationToken token)
    {
        using var lease = enterInferenceAsync is null ? null : await enterInferenceAsync(token);
        return await LlamaGeneration.LoadAsync(directory, loader ?? LlamaNativeModel.LoadAsync,
            verifier ?? ModelFiles.VerifyAsync, token, phiMini: true);
    }

    private async Task<LlamaEmbedding> LoadEmbeddingAsync(CancellationToken token)
    {
        using var lease = enterInferenceAsync is null ? null : await enterInferenceAsync(token);
        return await LlamaEmbedding.LoadAsync(directory, loader ?? LlamaNativeModel.LoadAsync,
            verifier ?? ModelFiles.VerifyAsync, token);
    }

    private static AnalysisStoreModel GenerationModel() => Model(Array.Find(ModelFiles.PhiMiniFiles(), file => file.Role == "generation")!,
        """{"runtime":"LLamaSharp-0.30.0","template":"phi-mini","context":4096,"gpuLayers":0,"noKqvOffload":true,"opOffload":false,"threads":4,"batchThreads":4,"batchSize":128,"ubatchSize":128,"grammar":"StructuredClaims-v1","seed":42,"temperature":0,"topK":40,"topP":0.9,"minP":0.1,"presencePenalty":0,"overflow":"ThrowException","summaryTokens":1024,"answerTokens":512}""");

    private static AnalysisStoreModel EmbeddingModel() => Model(Array.Find(ModelFiles.CandidateFiles(), file => file.Role == "embedding")!,
        """{"runtime":"LLamaSharp-0.30.0","context":512,"gpuLayers":0,"noKqvOffload":true,"opOffload":false,"threads":4,"batchThreads":4,"batchSize":512,"ubatchSize":512,"pooling":"Mean","dimensions":768,"queryPrefix":"search_query: ","documentPrefix":"search_document: ","normalize":true}""");

    private static AnalysisStoreModel Model(PinnedModelFile file, string configuration) =>
        new(file.Name, file.Sha256.ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration))), configuration);
}
