using MegaPDF.Core.Engine;
using MegaPDF.Core.Services;

namespace MegaPDF.Core.Imaging;

/// <summary>
/// The honest-result check for Shrink for email (#6, design step 3.5): a copy is offered only if
/// it is strictly smaller than what the person would otherwise attach.
///
/// Replacing images does not mean the file got smaller. A full PDFium save drops object streams,
/// and an image drawn several times becomes one JPEG per placement, so the survey found 231 private
/// documents with a replaced image whose shrunk copy was not smaller than the original. Counting
/// replaced images (what both desktops did) says nothing about that; only the serialised bytes do.
///
/// The decision lives here, not in either UI, so Windows, macOS and Linux cannot disagree on it
/// and it is testable without a window or a save picker.
/// </summary>
public static class SmallerCopyGate
{
    /// <summary>True when the copy is strictly smaller than the baseline. Equal is not smaller.</summary>
    public static bool IsSmaller(long copyBytes, long baselineBytes) => copyBytes < baselineBytes;

    /// <summary>
    /// Serialises <paramref name="copy"/> to a verified temporary file (not the destination, which
    /// is not chosen yet) and returns it only if its byte size is strictly below
    /// <paramref name="baselineBytes"/>: the desktops' baseline is the file on disk. Otherwise the
    /// temporary file is deleted and null is returned, and the caller must open no save picker and
    /// write nothing.
    /// </summary>
    public static VerifiedSave.StagedCopy? StageIfSmaller(IPdfEngine engine, IPdfDocument copy,
        long baselineBytes, Action<VerifiedSave.SaveStage>? onStage = null)
    {
        var staged = VerifiedSave.ToStagedFile(engine, copy, onStage);
        if (IsSmaller(staged.Length, baselineBytes))
            return staged;
        staged.Dispose();
        return null;
    }
}
