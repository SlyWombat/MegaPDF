using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Core;

/// <summary>
/// P/Invoke surface over the shared engine core (`core/megapdf_core.h`, ADR-003):
/// the one implementation of the policy that used to be written per platform.
/// The library is `megapdf_core.dll` on Windows and `libmegapdf_core.dylib` on
/// macOS, built by tools/build-core.* and copied next to pdfium by the Core
/// project. Every call must hold <see cref="Pdfium.PdfiumLibrary.Lock"/>, because
/// the core calls PDFium and PDFium is not thread-safe.
///
/// The ABI passes bare page handles and caller-owned buffers (count-then-fill),
/// so the marshalling here is deliberately boring.
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

    /// <summary>
    /// Drawn-checkbox candidates on a page (SDD §6.2 contract 2). Returns how many
    /// there are; fills up to <paramref name="capacity"/> of them into
    /// <paramref name="outRects"/>, which may be null when <paramref name="capacity"/> is 0.
    /// </summary>
    [DllImport(Dll)]
    public static extern nuint megapdf_detect_checkbox_squares(IntPtr page, [Out] Rect[]? outRects, nuint capacity);

    /// <summary>The CropBox origin the core subtracts, in PDF user space.</summary>
    [DllImport(Dll)]
    public static extern void megapdf_crop_origin(IntPtr page, out double x, out double y);
}
