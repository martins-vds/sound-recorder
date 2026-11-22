namespace SoundRecorder.Infrastructure.Persistence;

internal static class AtomicSnapshotPublication
{
    internal static void Publish(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            var previousSnapshot = $"{temporaryPath}.previous";
            try
            {
                File.Replace(temporaryPath, destinationPath, previousSnapshot);
            }
            finally
            {
                if (File.Exists(previousSnapshot))
                    File.Delete(previousSnapshot);
            }
        }
        else
            File.Move(temporaryPath, destinationPath);
    }
}
