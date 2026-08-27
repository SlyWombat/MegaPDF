namespace MegaPDF.Core.Services;

/// <summary>
/// Saves through a caller-supplied destination stream, staging the work in a
/// temporary file first.
///
/// This exists because <see cref="AtomicFileWriter"/> cannot work under the macOS
/// App Sandbox. That writes <c>.name.guid.megapdf-tmp</c> *in the destination's
/// directory* and swaps it in, but the sandbox grants access to the FILE the user
/// picked, not its folder — so creating that sibling is denied outright (proven in
/// ADR-002's sandbox probe: an unprivileged read of a fixture threw
/// UnauthorizedAccessException). The host instead opens the user's file through the
/// platform's own picker/bookmark machinery and hands the writable stream here.
///
/// **The guarantee is weaker, and deliberately so.** AtomicFileWriter promises the
/// destination is never seen half-written (SDD §3.4) because the swap is a single
/// filesystem operation. This cannot promise that: the destination is written in
/// place. What it does instead is make the dangerous window as small as possible —
/// all the slow work (serialising the PDF, which is where a crash or a full disk is
/// actually likely) happens against a temp file, and the destination is only touched
/// by a straight byte copy once that has fully succeeded. A save that fails to
/// serialise never touches the user's file at all.
///
/// Windows keeps AtomicFileWriter and its stronger guarantee. Nothing here changes
/// that path.
/// </summary>
public static class StagedStreamWriter
{
    public static void Write(Stream destination, Action<Stream> writeContent)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(writeContent);

        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));

        // Path.GetTempPath() is inside the app container under the sandbox, which is
        // exactly where we are allowed to write freely.
        var stagingPath = Path.Combine(Path.GetTempPath(), $"megapdf-save-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writeContent(staging);
                staging.Flush(flushToDisk: true);
            }

            // Only now is the user's file touched. Everything above could have thrown
            // without the destination having been opened for truncation.
            using var staged = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            destination.Seek(0, SeekOrigin.Begin);
            staged.CopyTo(destination);
            destination.Flush();

            // A shorter document must not leave the tail of the previous one behind.
            if (destination.CanSeek)
                destination.SetLength(destination.Position);
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }
}
