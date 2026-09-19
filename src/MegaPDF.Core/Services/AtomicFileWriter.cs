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

        var name = $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.megapdf-tmp";
        var tempPath = Path.Combine(directory, "." + name);

        try
        {
            FileStream created;
            try
            {
                created = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (UnauthorizedAccessException)
            {
                // The folder takes the document but not a hidden file beside it. That is
                // the snap's `home` plug exactly (#158): AppArmor lets a confined app write
                // non-hidden files at the top of the home folder and nothing hidden there,
                // so ~/form.pdf could be opened but never saved. The same swap under a
                // visible name keeps the save atomic. Anywhere a hidden file is refused
                // for some other reason this is refused too, and the save fails as before.
                tempPath = Path.Combine(directory, name);
                created = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }

            using (var stream = created)
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
