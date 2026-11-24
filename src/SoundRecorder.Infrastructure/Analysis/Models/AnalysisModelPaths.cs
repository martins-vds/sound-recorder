namespace SoundRecorder.Infrastructure.Analysis.Models;

internal static class AnalysisModelPaths
{
    internal static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Choose an explicit local path, not a network share or device path.");
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Network model paths are not supported. Choose local storage.");
        if (System.Text.Encoding.UTF8.GetByteCount(fullPath) > 4096)
            throw new InvalidDataException("The model path is too long. Choose a shorter local directory.");
        return fullPath;
    }

    internal static void RequireLocalUnlinked(string path)
    {
        var fullPath = LocalPath(path);
        RequireLocalDrive(fullPath);
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
            RejectLink(current);
    }

    private static void RequireLocalDrive(string fullPath)
    {
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidDataException("Choose available local storage, not a mapped network drive.");
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links and reparse points are not supported for model setup. Choose an owned local path.");
    }
}
