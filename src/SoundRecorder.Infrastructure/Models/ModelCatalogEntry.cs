using Whisper.net.Ggml;

namespace SoundRecorder.Infrastructure.Models;

public sealed record ModelCatalogEntry(
    string Key,
    string Family,
    string DisplayName,
    QuantizationType Encoding,
    IReadOnlyList<string> Languages,
    Uri DownloadUri);
