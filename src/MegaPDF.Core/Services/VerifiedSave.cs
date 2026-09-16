using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Services;

/// <summary>
/// Saving that proves the result is readable before it replaces anything (#56).
///
/// <see cref="AtomicFileWriter"/> and <see cref="StagedStreamWriter"/> guarantee
/// the destination is never seen half-written. That is a different guarantee from
/// the document being valid: a fully-written corrupt file replaces the original
/// just as cleanly as a good one. The person this protects is the one whose only
/// copy of a signed form is the file being overwritten.
///
/// So: serialise to a temporary file, reopen it with the engine, and only then
/// write. Both mobile apps already do this — iOS in ViewerModel.save, Android in
/// ViewerViewModel.writeTo — and this is that protocol brought to the desktops so
/// all four platforms state the same guarantee.
///
/// The cost is one extra parse of a document already in memory, against a save
/// that has just serialised the whole thing.
/// </summary>
public static class VerifiedSave
{
    /// <summary>Thrown when a save produced bytes the engine cannot read back.</summary>
    public sealed class UnreadableOutputException(Exception inner)
        : Exception("The saved document could not be read back, so the original was left untouched.", inner);

    /// <summary>Where a save is, for the busy label (#145): "Saving…", then "Checking the saved file…".</summary>
    public enum SaveStage { Writing, Verifying }

    /// <summary>
    /// A save that has been written to a temporary file and read back, waiting to be written
    /// to its destination (#147). A host that must open the destination only once the save is
    /// known good — the macOS sandbox, where opening the file for writing truncates it — holds
    /// one of these instead of the bytes in memory, so a large document never is. Disposing it
    /// deletes the file.
    /// </summary>
    public sealed class StagedCopy : IDisposable
    {
        internal StagedCopy(string path) => Path = path;

        /// <summary>The staged file.</summary>
        public string Path { get; }

        public long Length => new FileInfo(Path).Length;

        /// <summary>Streams the staged file into <paramref name="target"/> from its current position.</summary>
        public void CopyTo(Stream target)
        {
            using var staged = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            staged.CopyTo(target, 1 << 20);
        }

        /// <summary>
        /// Writes the staged file over <paramref name="destination"/> from its start, and cuts
        /// off whatever of the previous contents was longer. The destination is written in
        /// place: if it is the file an open document reads, move that document off it first
        /// (<see cref="IPdfDocument.ReadFromCopy"/>).
        /// </summary>
        public void WriteOver(Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (destination.CanSeek)
                destination.Seek(0, SeekOrigin.Begin);
            CopyTo(destination);
            destination.Flush();
            if (destination.CanSeek)
                destination.SetLength(destination.Position);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch (IOException)
            {
                // Left in the temp folder; nothing depends on it any more.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Writes to a path with <see cref="AtomicFileWriter"/>'s swap, after verifying.
    /// </summary>
    public static void ToPath(IPdfEngine engine, IPdfDocument document, string path, Action<SaveStage>? onStage = null)
    {
        Stage(engine, document, document.Save, OpenLike(engine, document),
            staged => AtomicFileWriter.Write(path, CopyFrom(staged)), onStage);
    }

    /// <summary>
    /// Writes through a stream the host already holds open — the macOS sandbox
    /// path — after verifying.
    /// </summary>
    public static void ToStream(IPdfEngine engine, IPdfDocument document, Stream destination, Action<SaveStage>? onStage = null)
    {
        Stage(engine, document, document.Save, OpenLike(engine, document),
            staged => StagedStreamWriter.Write(destination, CopyFrom(staged)), onStage);
    }

    /// <summary>
    /// A copy under new security (#131), verified by opening it with the new password —
    /// it no longer opens like the document. Throws <see cref="DocumentRestrictedException"/>
    /// unless the document's open has full access.
    /// </summary>
    public static void ToPathWithSecurity(IPdfEngine engine, IPdfDocument document, string path,
        string userPassword, string? ownerPassword, PdfPermissions permissions, Action<SaveStage>? onStage = null)
    {
        Stage(engine, document, target => document.SaveWithSecurity(target, userPassword, ownerPassword, permissions),
            OpenWith(engine, userPassword), staged => AtomicFileWriter.Write(path, CopyFrom(staged)), onStage);
    }

    /// <inheritdoc cref="ToPathWithSecurity"/>
    public static void ToStreamWithSecurity(IPdfEngine engine, IPdfDocument document, Stream destination,
        string userPassword, string? ownerPassword, PdfPermissions permissions, Action<SaveStage>? onStage = null)
    {
        Stage(engine, document, target => document.SaveWithSecurity(target, userPassword, ownerPassword, permissions),
            OpenWith(engine, userPassword), staged => StagedStreamWriter.Write(destination, CopyFrom(staged)), onStage);
    }

    /// <summary>
    /// A copy with no security (#131), verified by opening it without a password. Throws
    /// <see cref="DocumentRestrictedException"/> unless the document's open has full access.
    /// </summary>
    public static void ToPathWithoutSecurity(IPdfEngine engine, IPdfDocument document, string path, Action<SaveStage>? onStage = null)
    {
        Stage(engine, document, document.SaveWithoutSecurity, OpenWith(engine, null),
            staged => AtomicFileWriter.Write(path, CopyFrom(staged)), onStage);
    }

    /// <inheritdoc cref="ToPathWithoutSecurity"/>
    public static void ToStreamWithoutSecurity(IPdfEngine engine, IPdfDocument document, Stream destination, Action<SaveStage>? onStage = null)
    {
        Stage(engine, document, document.SaveWithoutSecurity, OpenWith(engine, null),
            staged => StagedStreamWriter.Write(destination, CopyFrom(staged)), onStage);
    }

    /// <summary>
    /// The save, written to a temporary file and verified, for the caller to write where it
    /// must (#147). Throws as <see cref="ToPath"/> does, with nothing left behind.
    /// </summary>
    public static StagedCopy ToStagedFile(IPdfEngine engine, IPdfDocument document, Action<SaveStage>? onStage = null) =>
        StageOnly(engine, document, document.Save, OpenLike(engine, document), onStage);

    /// <inheritdoc cref="ToPathWithSecurity"/>
    public static StagedCopy ToStagedFileWithSecurity(IPdfEngine engine, IPdfDocument document,
        string userPassword, string? ownerPassword, PdfPermissions permissions, Action<SaveStage>? onStage = null) =>
        StageOnly(engine, document, target => document.SaveWithSecurity(target, userPassword, ownerPassword, permissions),
            OpenWith(engine, userPassword), onStage);

    /// <inheritdoc cref="ToPathWithoutSecurity"/>
    public static StagedCopy ToStagedFileWithoutSecurity(IPdfEngine engine, IPdfDocument document, Action<SaveStage>? onStage = null) =>
        StageOnly(engine, document, document.SaveWithoutSecurity, OpenWith(engine, null), onStage);

    private static StagedCopy StageOnly(IPdfEngine engine, IPdfDocument document, Action<Stream> save,
        Func<string, IPdfDocument> reopen, Action<SaveStage>? onStage)
    {
        StagedCopy? staged = null;
        Stage(engine, document, save, reopen, stagingPath =>
        {
            // Keep the file: move it out from under Stage's cleanup.
            var kept = Path.Combine(Path.GetDirectoryName(stagingPath)!, $"megapdf-staged-{Guid.NewGuid():N}.pdf");
            File.Move(stagingPath, kept);
            staged = new StagedCopy(kept);
        }, onStage);
        return staged!;
    }

    private static Action<Stream> CopyFrom(string stagedPath) => target =>
    {
        using var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        staged.CopyTo(target);
    };

    // Opened like the document, because a protected document's copy is still protected
    // and needs the same password to read back (#132).
    private static Func<string, IPdfDocument> OpenLike(IPdfEngine engine, IPdfDocument document) =>
        stagedPath => engine.OpenLike(document, stagedPath);

    // An empty user password is no password: the copy opens without one.
    private static Func<string, IPdfDocument> OpenWith(IPdfEngine engine, string? userPassword) =>
        stagedPath => engine.Open(stagedPath, string.IsNullOrEmpty(userPassword) ? null : userPassword);

    /// <summary>
    /// Where this thread stages its copy, when a test needs its own folder: the shared temp
    /// folder is also used by tests running in parallel, so counting files there races them.
    /// </summary>
    [ThreadStatic]
    internal static string? StagingDirectoryForTests;

    private static void Stage(IPdfEngine engine, IPdfDocument document, Action<Stream> save,
        Func<string, IPdfDocument> reopen, Action<string> write, Action<SaveStage>? onStage)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);

        var stagingPath = Path.Combine(StagingDirectoryForTests ?? Path.GetTempPath(), $"megapdf-verify-{Guid.NewGuid():N}.pdf");
        try
        {
            // A refusal (DocumentRestrictedException) or a failed write propagates as
            // itself: nothing was produced to verify.
            onStage?.Invoke(SaveStage.Writing);
            using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                save(staging);
                staging.Flush(flushToDisk: true);
            }

            if (new FileInfo(stagingPath).Length == 0)
                throw new UnreadableOutputException(new InvalidDataException("the engine produced an empty document"));

            onStage?.Invoke(SaveStage.Verifying);
            try
            {
                // Reopened with the engine, not merely length-checked: "parses" is
                // the property that matters, and only a parse establishes it.
                using var reopened = reopen(stagingPath);
                if (reopened.PageCount == 0)
                    throw new InvalidDataException("the saved document has no pages");
            }
            catch (Exception ex) when (ex is not UnreadableOutputException)
            {
                throw new UnreadableOutputException(ex);
            }

            write(stagingPath);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }
}
