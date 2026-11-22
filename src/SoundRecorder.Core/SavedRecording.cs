namespace SoundRecorder.Core;

public sealed record SavedRecording(
    Guid Id,
    string AudioPath,
    string Language,
    RecordingOutcome Outcome,
    TimeSpan CapturedDuration)
{
    public string? Title { get; init; }

    /// <summary>Intentional audio removal, separate from an interrupted or recovered capture.</summary>
    public bool AudioDeleted { get; init; }

    public PendingRecordingDeletion? PendingDeletion { get; init; }

    public IReadOnlyList<OwnedTranscriptOutputs> TranscriptOutputs
    {
        get;
        init => field = value.Count == 0
            ? Array.Empty<OwnedTranscriptOutputs>()
            : Array.AsReadOnly(value.ToArray());
    }
        = Array.Empty<OwnedTranscriptOutputs>();
}
