using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Core;

/// <summary>
/// P/Invoke surface over the shared engine core (`core/megapdf_core.h`, ADR-003):
/// the one implementation of the policy that used to be written per platform.
/// The library is `megapdf_core.dll` on Windows and `libmegapdf_core.dylib` on
/// macOS, built by tools/build-core.* and copied next to pdfium by the Core
/// project.
///
/// Since #105 the core owns documents: <see cref="megapdf_open"/> copies the bytes
/// and initialises the form-fill environment, pages come from
/// <see cref="megapdf_load_page"/>, and the core serialises every call on its own
/// mutex. The engine still takes <see cref="Pdfium.PdfiumLibrary.Lock"/> around
/// the contracts that have not migrated yet, which reach PDFium through the raw
/// handle accessors.
///
/// Buffers are caller-owned, count-then-fill, so the marshalling here is
/// deliberately boring.
/// </summary>
internal static class CoreNative
{
    private const string Dll = "megapdf_core";

    /// <summary>A rectangle in PDF points, bottom-left origin, crop-relative (the core's convention).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public double Left;
        public double Bottom;
        public double Right;
        public double Top;
    }

    // Errors ---------------------------------------------------------------

    /// <summary>PDFium's FPDF_GetLastError() for the last failed open on this thread.</summary>
    [DllImport(Dll)]
    public static extern uint megapdf_last_error();

    [DllImport(Dll)]
    private static extern IntPtr megapdf_last_error_message();

    public static string LastErrorMessage() => Marshal.PtrToStringUTF8(megapdf_last_error_message()) ?? "";

    // Documents ------------------------------------------------------------

    /// <summary>Opens a document from memory; the core copies the bytes. Zero on failure.</summary>
    [DllImport(Dll)]
    public static extern unsafe IntPtr megapdf_open(byte* bytes, nuint length,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Dll)]
    public static extern void megapdf_close(IntPtr document);

    [DllImport(Dll)]
    public static extern int megapdf_page_count(IntPtr document);

    // Pages ----------------------------------------------------------------

    [DllImport(Dll)]
    public static extern IntPtr megapdf_load_page(IntPtr document, int index);

    [DllImport(Dll)]
    public static extern void megapdf_close_page(IntPtr page);

    [DllImport(Dll)]
    public static extern double megapdf_page_width(IntPtr page);

    [DllImport(Dll)]
    public static extern double megapdf_page_height(IntPtr page);

    /// <summary>The CropBox origin the core subtracts, in PDF user space.</summary>
    [DllImport(Dll)]
    public static extern void megapdf_page_crop_origin(IntPtr page, out double x, out double y);

    // Contracts ------------------------------------------------------------

    /// <summary>
    /// Drawn-checkbox candidates on a page (SDD §6.2 contract 2). Returns how many
    /// there are; fills up to <paramref name="capacity"/> of them into
    /// <paramref name="outRects"/>, which may be null when <paramref name="capacity"/> is 0.
    /// </summary>
    [DllImport(Dll)]
    public static extern nuint megapdf_detect_checkbox_squares(IntPtr page, [Out] Rect[]? outRects, nuint capacity);

    /// <summary>
    /// Text search (contract 1, #26). Returns the total number of doubles in the packed
    /// stream — per match: rect count, then (left, bottom, right, top) per rect — and
    /// fills up to <paramref name="capacity"/> of them.
    /// </summary>
    [DllImport(Dll)]
    public static extern nuint megapdf_search_page(IntPtr page,
        [MarshalAs(UnmanagedType.LPWStr)] string term, [Out] double[]? outDoubles, nuint capacity);

    // Raw handles, for the contracts still bound directly (#106–#112) --------

    [DllImport(Dll)]
    public static extern IntPtr megapdf_document_raw(IntPtr document);

    [DllImport(Dll)]
    public static extern IntPtr megapdf_document_form_raw(IntPtr document);

    [DllImport(Dll)]
    public static extern IntPtr megapdf_page_raw(IntPtr page);
}
