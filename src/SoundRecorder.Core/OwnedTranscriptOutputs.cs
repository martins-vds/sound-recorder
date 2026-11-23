namespace SoundRecorder.Core;

/// <summary>A completed output pair retained independently of its job-history entry.</summary>
public sealed record OwnedTranscriptOutputs(Guid JobId, TranscriptOutputs Outputs)
{
    /// <summary>The successful attempt's language, retained after job-history deletion; null means legacy unknown.</summary>
    public string? Language { get; init; }
}
