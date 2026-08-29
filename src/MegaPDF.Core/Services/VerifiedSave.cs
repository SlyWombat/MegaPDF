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

    /// <summary>
    /// Writes to a path with <see cref="AtomicFileWriter"/>'s swap, after verifying.
    /// </summary>
    public static void ToPath(IPdfEngine engine, IPdfDocument document, string path)
    {
        Stage(engine, document, staged => AtomicFileWriter.Write(path, CopyFrom(staged)));
    }

    /// <summary>
    /// Writes through a stream the host already holds open — the macOS sandbox
    /// path — after verifying.
    /// </summary>
    public static void ToStream(IPdfEngine engine, IPdfDocument document, Stream destination)
    {
        Stage(engine, document, staged => StagedStreamWriter.Write(destination, CopyFrom(staged)));
    }

    private static Action<Stream> CopyFrom(string stagedPath) => target =>
    {
        using var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        staged.CopyTo(target);
    };

    private static void Stage(IPdfEngine engine, IPdfDocument document, Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);

        var stagingPath = Path.Combine(Path.GetTempPath(), $"megapdf-verify-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                document.Save(staging);
                staging.Flush(flushToDisk: true);
            }

            if (new FileInfo(stagingPath).Length == 0)
                throw new UnreadableOutputException(new InvalidDataException("the engine produced an empty document"));

            try
            {
                // Reopened with the engine, not merely length-checked: "parses" is
                // the property that matters, and only a parse establishes it.
                using var reopened = engine.Open(stagingPath);
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
