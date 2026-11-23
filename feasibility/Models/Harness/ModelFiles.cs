using System.Text.Json;
using System.Security.Cryptography;

namespace Models.Harness;

/// <summary>One exact local candidate file; a URL is approval metadata, never a download instruction.</summary>
public sealed record PinnedModelFile(string Role, string Name, long Bytes, string Sha256, string Url);

/// <summary>Replaceable local integrity boundary; native inference cannot start before it succeeds.</summary>
public delegate Task ModelFileVerifier(string directory, IReadOnlyList<PinnedModelFile> files, CancellationToken cancellationToken);

/// <summary>Checks local files without network access or native inference.</summary>
public static class ModelFiles
{
    /// <summary>Reads the embedded, proposed file identities.</summary>
    public static PinnedModelFile[] CandidateFiles()
        => CandidateFiles(() => typeof(ModelFiles).Assembly.GetManifestResourceStream("Models.Harness.candidate-profile.json"));

    /// <summary>Reads profile data through a replaceable local resource transport.</summary>
    public static PinnedModelFile[] CandidateFiles(Func<Stream?> openProfile)
    {
        using var stream = openProfile()
            ?? throw new InvalidDataException("Embedded candidate profile is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("files").Deserialize<PinnedModelFile[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>Requires exact size and SHA-256 before opening any native model.</summary>
    public static async Task VerifyAsync(string directory, IReadOnlyList<PinnedModelFile> files,
        CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The explicitly approved local model file is not installed.", file.Name);
            }
            await using var stream = File.OpenRead(path);
            if (stream.Length != file.Bytes)
            {
                throw new InvalidDataException($"Local file size does not match the pinned candidate: {file.Name}.");
            }
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Local SHA-256 does not match the pinned candidate: {file.Name}.");
            }
        }
    }
}
