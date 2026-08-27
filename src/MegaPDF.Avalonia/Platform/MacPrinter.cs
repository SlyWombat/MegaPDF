using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Printing on macOS (SDD §3.5), through PDFKit's <c>PDFDocument</c> and AppKit's
/// <c>NSPrintOperation</c>.
///
/// **Why PDFKit is allowed here, when ADR-001 rejected it.** ADR-001 ruled PDFKit
/// out as an engine because it *rewrites* annotation appearance streams on save
/// (`appearance-streams-preserved=false`), which would strand stamps placed on
/// other platforms. Printing never saves. ADR-001's own wording leaves this open:
/// PDFKit "may still be used for incidental viewing niceties, but never to *write*
/// documents". Nothing here opens a write path — the file goes in, a print
/// operation comes out.
///
/// **Why not a temp file handed to Preview.** That is the easy answer outside the
/// sandbox and unavailable inside it: a Mac App Store build cannot scatter files
/// where another app can read them, nor launch one to finish the job. NSPrintOperation
/// runs in-process, needs only `com.apple.security.print`, and gives the real system
/// print panel rather than a detour through someone else's app.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacPrinter
{
    private const string Objc = "/usr/lib/libobjc.dylib";
    private const string Dl = "/usr/lib/libSystem.dylib";

    // One declaration per distinct signature. objc_msgSend's ABI varies by return
    // and argument type — on arm64 especially, reusing a single declaration with
    // different parameters is silent corruption rather than an exception.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Utf8(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern long MsgSend_Long(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSend_Bool(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_PrintOp(IntPtr receiver, IntPtr selector,
        IntPtr printInfo, long scalingMode, [MarshalAs(UnmanagedType.I1)] bool autoRotate);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dl)]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    private const int RtldNow = 2;

    /// <summary>
    /// The frameworks must be loaded before their classes can be looked up —
    /// objc_getClass returns nil for a framework the process has never linked.
    /// AppKit as well as PDFKit: NSPrintInfo and NSPrintOperation live there, and a
    /// console-mode run has not linked it.
    /// </summary>
    private static bool EnsureFrameworks() =>
        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", RtldNow) != IntPtr.Zero
        && dlopen("/System/Library/Frameworks/PDFKit.framework/PDFKit", RtldNow) != IntPtr.Zero;

    /// <summary>
    /// Wraps a C# string as an NSString.
    ///
    /// This is what the first version got wrong, and it cost a segfault rather than
    /// an error: `fileURLWithPath:` takes an NSString*, but marshalling a C# string
    /// as LPUTF8Str hands it a char*. Objective-C then sends messages to a pointer
    /// that is not an object. `stringWithUTF8String:` is the one selector here that
    /// genuinely does take a C string, so it is the bridge.
    /// </summary>
    private static IntPtr NSString(string value)
    {
        var cls = objc_getClass("NSString");
        return cls == IntPtr.Zero
            ? IntPtr.Zero
            : MsgSend_Utf8(cls, sel_registerName("stringWithUTF8String:"), value);
    }

    /// <summary>kPDFPrintPageScaleDownToFit — fit each page to the paper.</summary>
    private const long ScaleDownToFit = 1;

    /// <summary>What a probe or a print attempt found, in words a status bar can show.</summary>
    internal sealed record Outcome(bool Ok, string Message);

    /// <summary>
    /// Verifies the whole interop chain against a real file without printing:
    /// framework loads, classes and selectors resolve, a PDFDocument is constructed,
    /// and its page count matches what the engine says.
    ///
    /// That last check is the one that matters. A wrong objc_msgSend signature
    /// returns a plausible-looking pointer rather than failing, so only reading a
    /// value back and comparing it to a known-good number proves the marshalling
    /// is actually right.
    /// </summary>
    internal static Outcome Probe(string pdfPath, int expectedPageCount)
    {
        if (!OperatingSystem.IsMacOS())
            return new Outcome(false, "not macOS");

        if (!EnsureFrameworks())
            return new Outcome(false, "AppKit or PDFKit did not load");

        var nsUrl = objc_getClass("NSURL");
        var pdfDocument = objc_getClass("PDFDocument");
        var nsPrintInfo = objc_getClass("NSPrintInfo");
        if (nsUrl == IntPtr.Zero || pdfDocument == IntPtr.Zero || nsPrintInfo == IntPtr.Zero)
            return new Outcome(false,
                $"class lookup failed (NSURL={nsUrl}, PDFDocument={pdfDocument}, NSPrintInfo={nsPrintInfo})");

        var fileUrlWithPath = sel_registerName("fileURLWithPath:");
        var alloc = sel_registerName("alloc");
        var initWithUrl = sel_registerName("initWithURL:");
        var pageCountSel = sel_registerName("pageCount");
        var sharedPrintInfo = sel_registerName("sharedPrintInfo");
        var printOperationSel = sel_registerName("printOperationForPrintInfo:scalingMode:autoRotate:");
        var runOperation = sel_registerName("runOperation");
        if (fileUrlWithPath == IntPtr.Zero || alloc == IntPtr.Zero || initWithUrl == IntPtr.Zero
            || pageCountSel == IntPtr.Zero || sharedPrintInfo == IntPtr.Zero
            || printOperationSel == IntPtr.Zero || runOperation == IntPtr.Zero)
            return new Outcome(false, "one or more selectors did not resolve");

        var pathString = NSString(pdfPath);
        if (pathString == IntPtr.Zero)
            return new Outcome(false, "could not create an NSString for the path");

        var url = MsgSend_Ptr(nsUrl, fileUrlWithPath, pathString);
        if (url == IntPtr.Zero)
            return new Outcome(false, "fileURLWithPath: returned nil");

        var document = MsgSend_Ptr(MsgSend(pdfDocument, alloc), initWithUrl, url);
        if (document == IntPtr.Zero)
            return new Outcome(false, "PDFDocument initWithURL: returned nil");

        var pages = MsgSend_Long(document, pageCountSel);
        if (pages != expectedPageCount)
            return new Outcome(false,
                $"pageCount disagreed with the engine: PDFKit says {pages}, PdfiumEngine says {expectedPageCount}");

        return new Outcome(true, $"PDFKit agrees the document has {pages} page(s)");
    }

    /// <summary>
    /// Opens the system print panel for a PDF on disk and runs the operation. Must
    /// be called on the UI thread — NSPrintOperation drives AppKit.
    /// </summary>
    internal static Outcome Print(string pdfPath)
    {
        if (!OperatingSystem.IsMacOS())
            return new Outcome(false, "Printing is only available on macOS in this build.");

        if (!EnsureFrameworks())
            return new Outcome(false, "Could not load the system printing components.");

        var nsUrl = objc_getClass("NSURL");
        var pdfDocument = objc_getClass("PDFDocument");
        var nsPrintInfo = objc_getClass("NSPrintInfo");
        if (nsUrl == IntPtr.Zero || pdfDocument == IntPtr.Zero || nsPrintInfo == IntPtr.Zero)
            return new Outcome(false, "The system print components are unavailable.");

        var pathString = NSString(pdfPath);
        if (pathString == IntPtr.Zero)
            return new Outcome(false, "The document path could not be prepared.");

        var url = MsgSend_Ptr(nsUrl, sel_registerName("fileURLWithPath:"), pathString);
        var document = MsgSend_Ptr(
            MsgSend(pdfDocument, sel_registerName("alloc")), sel_registerName("initWithURL:"), url);
        if (document == IntPtr.Zero)
            return new Outcome(false, "The document could not be prepared for printing.");

        var printInfo = MsgSend(nsPrintInfo, sel_registerName("sharedPrintInfo"));
        var operation = MsgSend_PrintOp(
            document, sel_registerName("printOperationForPrintInfo:scalingMode:autoRotate:"),
            printInfo, ScaleDownToFit, true);
        if (operation == IntPtr.Zero)
            return new Outcome(false, "The print operation could not be created.");

        // Returns false when the user cancels the panel, which is not an error.
        var ran = MsgSend_Bool(operation, sel_registerName("runOperation"));
        return new Outcome(true, ran ? "Sent to the printer." : "Printing cancelled.");
    }
}
