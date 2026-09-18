namespace MegaPDF.Core.Recovery;

/// <summary>
/// A document the app was launched with ("Open with MegaPDF", a double-clicked PDF) and
/// the crash-recovery offer, in the right order (#145).
///
/// The launched file used to win outright: the app opened it and never offered recovery,
/// so a crash's unsaved edits were silently left behind the first time someone
/// double-clicked a PDF afterwards. Now recovery is offered first and the launched file
/// is opened after it — unless restoring already opened that same document, with its
/// recovered edits. Opening it again at that point would either ask to save the edits
/// just recovered or, worse, replace them with the file on disk.
/// </summary>
public static class LaunchedDocument
{
    /// <summary>
    /// Whether <paramref name="launchedPath"/> still has to be opened, given the document
    /// open once the recovery offer is over (<c>null</c> when nothing is).
    /// </summary>
    /// <remarks>
    /// Decided by what is open, not by what the person answered: a restore that failed to
    /// open its document leaves nothing open, and the launched file must still appear. A
    /// different recovered document is not closed here — opening the launched one asks
    /// about its unsaved changes the way any other open does, so neither is lost.
    /// </remarks>
    public static bool NeedsOpening(string launchedPath, string? openDocumentPath) =>
        openDocumentPath is null || !SameFile(launchedPath, openDocumentPath);

    /// <summary>
    /// The same file, as the file system would resolve it: full paths, and case ignored on
    /// Windows and macOS, whose default file systems are case-insensitive. Symbolic links
    /// are not followed; if two spellings of one file ever go unmatched, the launched file
    /// is opened again, which asks about the recovered edits rather than losing them.
    /// </summary>
    public static bool SameFile(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Normalize(a), Normalize(b), comparison);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
