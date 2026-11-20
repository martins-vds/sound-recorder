namespace SoundRecorder.Infrastructure.Models;

public sealed record TranscriptionModelPackage(
    int FormatVersion,
    string Id,
    string Engine,
    string ModelFile,
    string Sha256,
    string Platform,
    IReadOnlyList<string> Languages);
