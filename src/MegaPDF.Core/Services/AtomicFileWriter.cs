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

            TempFileCreatedForTests?.Invoke(tempPath);

            using (var stream = created)
            {
                writeContent(stream);
                stream.Flush(flushToDisk: true);
            }

            SwapInPlace(destinationPath, tempPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// The swap this class ends with, for a caller that has already written and verified its own
    /// temp file (#334). <see cref="VerifiedSave"/> has one: it stages the save beside the
    /// destination (#193) and reads it back, and handing that file here instead of copying it
    /// into a second temp file is one copy of the document on disk rather than two.
    ///
    /// <paramref name="tempPath"/> is consumed: it is renamed into place, and is gone afterwards.
    /// It must be in the destination's own directory, because the swap is a rename and a rename
    /// only happens within one file system. From anywhere else <see cref="File.Replace"/> fails
    /// and <see cref="File.Move"/> quietly becomes a copy-then-delete — not atomic, and the one
    /// thing this class exists to prevent. So the directory is checked here rather than trusted,
    /// and a caller staging somewhere else (<see cref="VerifiedSave"/> under the snap's `home`
    /// plug, #158) copies through <see cref="Write"/> instead.
    /// </summary>
    internal static void SwapInPlace(string destinationPath, string tempPath)
    {
        if (!SameDirectory(destinationPath, tempPath))
            throw new InvalidOperationException(
                $"The file to swap in must be in the destination's own directory: {tempPath} is not beside {destinationPath}.");

        if (File.Exists(destinationPath))
            // File.Replace preserves the destination's ACLs and attributes (Win32 ReplaceFile).
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tempPath, destinationPath);
    }

    /// <summary>
    /// Whether two paths name files in the same directory, as the file system would resolve them:
    /// full paths, and case ignored on the file systems that do.
    /// </summary>
    internal static bool SameDirectory(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(DirectoryOf(a), DirectoryOf(b), comparison);

        static string DirectoryOf(string path) => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
    }

    /// <summary>
    /// Stands in for a look at which temp files a write makes: called with each one before
    /// anything is written into it (#334). A save to a path swaps its staged copy rather than
    /// making a second temp file, so it must leave this uncalled.
    /// </summary>
    [ThreadStatic]
    internal static Action<string>? TempFileCreatedForTests;
}
