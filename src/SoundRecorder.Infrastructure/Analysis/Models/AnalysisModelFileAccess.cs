namespace SoundRecorder.Infrastructure.Analysis.Models;

internal sealed record AnalysisModelFileAccess
{
    internal static AnalysisModelFileAccess Default { get; } = new();

    internal Func<string, FileStream> OpenSelection { get; init; } = path =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);

    internal Func<string, DriveType> GetDriveType { get; init; } = path =>
        new DriveInfo(Path.GetPathRoot(path)!).DriveType;

    internal Func<string, FileAttributes> GetAttributes { get; init; } = File.GetAttributes;

    internal Func<string, FileStream> OpenStage { get; init; } = File.Create;
}
