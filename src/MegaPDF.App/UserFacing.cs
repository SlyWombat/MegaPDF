using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;

namespace MegaPDF.App;

/// <summary>
/// What a failure looks like to the person holding the document (#91).
///
/// MegaPDF.Core is an engine and its exception messages are technical English
/// written for a developer — "Object 3 is no longer a text object". They used to
/// be the whole body of the error dialog. Now the body leads with a sentence in
/// the app's language, chosen from the exception's typed reason where one exists,
/// and the engine's own message follows only when nothing better is known
/// (SDD §2.2: plain language first, never an error code as the primary content).
/// </summary>
internal static class UserFacing
{
    public static string Describe(Exception ex) => ex switch
    {
        PdfLoadException { IsFileError: true } => Strings.ErrorFileUnreadable,
        PdfLoadException { IsFormatError: true } => Strings.ErrorNotAPdf,
        PdfLoadException { IsPasswordError: true } => Strings.ErrorPasswordProtected,
        PdfLoadException load => Strings.ErrorCouldNotOpen(load.ErrorCode),
        TextEditException { Reason: TextEditFailure.NoUsableFont } => Strings.ErrorNoUsableFont,
        TextEditException { Reason: TextEditFailure.NotExtractable } => Strings.ErrorNotExtractable,
        VerifiedSave.UnreadableOutputException => Strings.ErrorSavedCopyUnreadable,
        // Windows' own messages (file in use, access denied) are already in the
        // user's language and say what happened; keep them as the lead.
        IOException or UnauthorizedAccessException => ex.Message,
        _ => $"{Strings.ErrorGeneric}\n\n{ex.Message}",
    };
}
