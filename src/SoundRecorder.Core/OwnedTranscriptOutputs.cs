namespace SoundRecorder.Core;

/// <summary>A completed output pair retained independently of its job-history entry.</summary>
public sealed record OwnedTranscriptOutputs(Guid JobId, TranscriptOutputs Outputs);
