namespace SoundRecorder.Core;

/// <summary>One analysis weight identity, separate from speech-recognition models.</summary>
public sealed record AnalysisModelFile(string Role, string Name, long Bytes, string Sha256, string License);

/// <summary>The immutable initial local CPU analysis profile.</summary>
public sealed record AnalysisModelProfile(string Id, string Runtime, IReadOnlyList<AnalysisModelFile> Files);

/// <summary>Readiness never triggers installation, selection, or network activity.</summary>
public enum AnalysisModelReadinessState { SetupRequired, Ready, Invalid, ResourceUnavailable }

/// <summary>An actionable offline observation of the explicitly selected profile.</summary>
public sealed record AnalysisModelReadiness(AnalysisModelReadinessState State, string? Directory, string? Error);

/// <summary>Caller-configured conservative budgets, not supported hardware thresholds or an OOM guarantee.</summary>
public sealed record AnalysisModelLimits(long DiskReserveBytes, long RequiredAvailableMemoryBytes);

/// <summary>Transfer bytes and verification are reported separately; completion follows atomic activation.</summary>
public enum AnalysisModelSetupPhase { Transferring, Verifying, Activating, Completed }

/// <summary>Exact aggregate bytes for the pinned files, excluding packaging overhead.</summary>
public sealed record AnalysisModelSetupProgress(AnalysisModelSetupPhase Phase, long BytesTransferred, long TotalBytes);

/// <summary>Explicit setup actions install or select only the pinned initial analysis profile.</summary>
public interface IAnalysisModelSetup
{
    /// <summary>Exposes approved file identities and per-role licenses without network access.</summary>
    AnalysisModelProfile Profile { get; }

    /// <summary>Revalidates selected files and local resources without loading a native engine.</summary>
    Task<AnalysisModelReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Imports a local ZIP containing the exact pinned filenames; untrusted manifests are not accepted.</summary>
    Task<string> ImportAsync(string packagePath, IProgress<AnalysisModelSetupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads only the approved immutable URLs, upon this explicit request.</summary>
    Task<string> DownloadAsync(IProgress<AnalysisModelSetupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies and selects an existing local cache without modifying its weights.</summary>
    Task SelectDirectoryAsync(string directory, CancellationToken cancellationToken = default);
}
