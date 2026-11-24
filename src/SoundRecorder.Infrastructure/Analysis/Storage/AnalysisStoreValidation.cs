using System.Security.Cryptography;
using System.Text;
using SoundRecorder.Core;

namespace SoundRecorder.Infrastructure.Analysis.Storage;

internal static class AnalysisStoreValidation
{
    internal static AnalysisStoreIdentity Canonical(AnalysisStoreIdentity identity)
    {
        if (identity.AttemptId == Guid.Empty) throw new ArgumentException("A successful transcript attempt is required.");
        Text(identity.Language, 64);
        Text(identity.AlgorithmVersion, 128);
        if (identity.VectorDimensions is < 1 or > 4096)
            throw new ArgumentException("Bounded index dimensions are required.");
        return identity with
        {
            TextSha256 = Hash(identity.TextSha256),
            SubtitleSha256 = Hash(identity.SubtitleSha256),
            Generation = Model(identity.Generation),
            Embedding = Model(identity.Embedding)
        };
    }

    private static AnalysisStoreModel Model(AnalysisStoreModel model)
    {
        Text(model.Id, 256);
        Text(model.CanonicalConfiguration, 4096);
        var configurationHash = Hash(model.ConfigurationSha256);
        if (configurationHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.CanonicalConfiguration))))
            throw new ArgumentException("The pinned effective model configuration fingerprint is incompatible.");
        return model with { Sha256 = Hash(model.Sha256), ConfigurationSha256 = configurationHash };
    }

    private static string Hash(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact SHA-256 identity is required.");
        return hash.ToUpperInvariant();
    }

    internal static void Chunk(AnalysisStoreChunk chunk, int dimensions)
    {
        Text(chunk.SourceId, 256);
        Text(chunk.Text, 4096);
        if (chunk.Offset < 0) throw new InvalidDataException("An original source offset is required.");
        Timestamps(chunk.StartMs, chunk.EndMs);
        Vector(chunk.Vector, dimensions);
    }

    internal static void Checkpoint(AnalysisStoreCheckpoint checkpoint)
    {
        Text(checkpoint.Stage, 64);
        if (checkpoint.NextOrdinal < 0 || checkpoint.Cursor.Length > 1024)
            throw new InvalidDataException("A checkpoint requires a bounded compatible cursor and nonnegative ordinal.");
    }

    internal static void Output(AnalysisStoreOutput output)
    {
        OutputKind(output);
        Text(output.Content, 16384);
        if (output.Claims.Count > 16) throw new InvalidDataException("Claims must be streamed in bounded portions.");
        foreach (var claim in output.Claims) Claim(claim);
    }

    private static void OutputKind(AnalysisStoreOutput output)
    {
        if (output.Kind is not ("Summary" or "Discussion"))
            throw new InvalidDataException("Stored output must identify summary or discussion content.");
        if (output.Question is not null) Text(output.Question, 512);
    }

    private static void Claim(TranscriptAnalysisClaim claim)
    {
        Text(claim.Text, 4096);
        if (claim.Evidence.Count is < 1 or > 16)
            throw new InvalidDataException("A claim requires bounded original citations.");
        foreach (var evidence in claim.Evidence)
        {
            Text(evidence.Reference, 256);
            Text(evidence.Text, 4096);
            if (evidence.StartMs < 0) throw new InvalidDataException("An original citation timestamp cannot be negative.");
        }
    }

    private static void Vector(IReadOnlyList<float> vector, int dimensions)
    {
        if (vector.Count == 0) return;
        if (vector.Count != dimensions || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Index vector dimensions or finite values are incompatible.");
        if (vector.All(value => value == 0))
            throw new InvalidDataException("An index embedding cannot be zero.");
    }

    private static void Timestamps(long? start, long? end)
    {
        if (start < 0 || end <= start || (start is null) != (end is null))
            throw new InvalidDataException("Original saved-audio timestamps are invalid.");
    }

    private static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new InvalidDataException("A stored field is missing or exceeds its bounded portion limit.");
    }
}
