namespace SoundRecorder.Infrastructure.Privacy;

public static class LocalPathPolicy
{
    /// <summary>Rejects UNC and mapped network destinations without substituting a local path.</summary>
    public static void RejectNetworkDestination(string path) =>
        RejectNetworkDestination(path, GetDriveType);

    internal static void RejectNetworkDestination(
        string path,
        Func<string, DriveType> getDriveType)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Network destinations are not supported. Select a local folder.");
        }

        var root = Path.GetPathRoot(fullPath)!;
        if (getDriveType(root) == DriveType.Network)
        {
            throw new InvalidOperationException(
                "Network destinations are not supported. Select a local folder.");
        }
    }

    private static DriveType GetDriveType(string root) => new DriveInfo(root).DriveType;

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
