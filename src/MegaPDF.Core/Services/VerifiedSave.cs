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
///
/// **Where the staged copy goes.** Beside the destination when the save has one
/// (#193). It used to go in <see cref="Path.GetTempPath"/> always, which costs a
/// second whole copy of the document on a filesystem nobody chose: on Linux that is
/// <c>/tmp</c>, which on Fedora is a tmpfs sized at half of RAM, so saving a large
/// document became an allocation of memory the size of the document — exactly the
/// cost #147 and #148 took out of opening one, and it failed with 67 GB free where
/// the person was actually saving to. The destination's own folder is a directory we
/// are about to write the document into anyway, is on the same filesystem as the
/// destination by definition — so <see cref="AtomicFileWriter"/>'s swap stays a
/// rename, and stays atomic — and is where that writer already puts its own temp
/// file. The system temp folder remains the fallback for a destination folder that
/// cannot be written to, and remains the only choice for
/// <see cref="ToStream(IPdfEngine, IPdfDocument, Stream, Action{SaveStage}?)"/> and
/// <see cref="ToStagedFile"/>, which have no destination path: those are the macOS
/// sandbox's paths, where the app is granted the file the person picked and not its
/// folder, so it may not create a sibling of it at all.
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
    ///
    /// This one stays in the system temp folder: the caller has a stream, not a path, so there
    /// is no destination folder to put it beside, and under the sandbox there would be no
    /// permission to write in one (#193).
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
            staged => AtomicFileWriter.Write(path, CopyFrom(staged)), onStage, path);
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
            OpenWith(engine, userPassword), staged => AtomicFileWriter.Write(path, CopyFrom(staged)), onStage, path);
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
            staged => AtomicFileWriter.Write(path, CopyFrom(staged)), onStage, path);
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
    /// Stands in for <see cref="Path.GetTempPath"/> on this thread, when a test needs its own
    /// folder: the shared temp folder is also used by tests running in parallel, so counting
    /// files there races them.
    ///
    /// It does not override the destination's folder, which a save with a path stages in
    /// first (#193) — so a test sees the same choice a real save makes, and the fallback is
    /// somewhere a test can look.
    /// </summary>
    [ThreadStatic]
    internal static string? StagingDirectoryForTests;

    /// <summary>
    /// The staged file, open for writing. Created before anything is serialised into it, so
    /// which directory a save could actually use is settled while the file is still empty.
    /// </summary>
    private readonly record struct StagingFile(FileStream Stream, string Path);

    /// <summary>
    /// Creates the staged file beside <paramref name="destinationPath"/> (#193), or in the
    /// system temp folder when there is no destination path or its folder will not take the
    /// file.
    ///
    /// The fallback is decided on creating an empty file, which fails because the folder is
    /// read-only, gone, or not ours — not because there is no room, since a file of no bytes
    /// needs none. So a full destination still fails as a full destination rather than
    /// quietly staging somewhere else and failing later.
    /// </summary>
    private static StagingFile CreateStagingFile(string? destinationPath)
    {
        if (destinationPath is not null &&
            Path.GetDirectoryName(Path.GetFullPath(destinationPath)) is { Length: > 0 } beside)
        {
            try
            {
                return Create(beside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Read-only media, a folder someone else owns, a quota: the save may still
                // work — AtomicFileWriter has its own sibling to create and will say so if it
                // cannot — and staging in the temp folder is what this did before #193.
            }
        }
        return Create(StagingDirectoryForTests ?? Path.GetTempPath());

        static StagingFile Create(string directory)
        {
            // Hidden and named like AtomicFileWriter's own temp file, because it is now in the
            // same folder: one left behind by a crash mid-save should look like what it is.
            var path = Path.Combine(directory, $".megapdf-verify-{Guid.NewGuid():N}.megapdf-tmp");
            return new StagingFile(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), path);
        }
    }

    /// <param name="destinationPath">
    /// Where the save is going, when it is going to a path: the staged copy is made in that
    /// folder (#193). Null for a save through a stream the host holds open.
    /// </param>
    private static void Stage(IPdfEngine engine, IPdfDocument document, Action<Stream> save,
        Func<string, IPdfDocument> reopen, Action<string> write, Action<SaveStage>? onStage,
        string? destinationPath = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);

        // A refusal (DocumentRestrictedException) or a failed write propagates as
        // itself: nothing was produced to verify.
        onStage?.Invoke(SaveStage.Writing);
        var staged = CreateStagingFile(destinationPath);
        var stagingPath = staged.Path;
        try
        {
            using (var staging = staged.Stream)
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
