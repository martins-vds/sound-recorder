namespace SoundRecorder.Infrastructure.Privacy;

public static class LocalPathPolicy
{
    public static void RejectNetworkDestination(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Network destinations are not supported. Select a local folder.");
        }

        var root = Path.GetPathRoot(fullPath)!;
        if (new DriveInfo(root).DriveType == DriveType.Network)
        {
            throw new InvalidOperationException(
                "Network destinations are not supported. Select a local folder.");
        }
    }

    public static bool IsKnownSynchronizedFolder(
        string path,
        IEnumerable<string?> knownRoots)
    {
        var fullPath = Path.GetFullPath(path);
        return knownRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root!))
            .Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
