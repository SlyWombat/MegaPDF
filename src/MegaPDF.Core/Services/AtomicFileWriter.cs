namespace MegaPDF.Core.Services;

/// <summary>
/// Atomic save protocol (SDD §3.4): write to a temp file in the same directory,
/// flush to disk, then swap into place. A crash or full disk mid-save must never
/// corrupt or truncate the destination file.
/// </summary>
public static class AtomicFileWriter
{
    public static void Write(string destinationPath, Action<Stream> writeContent)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
            ?? throw new ArgumentException($"Path has no directory: {destinationPath}", nameof(destinationPath));

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.megapdf-tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destinationPath))
                // File.Replace preserves the destination's ACLs and attributes (Win32 ReplaceFile).
                File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, destinationPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
