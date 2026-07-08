using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Pdfium;

/// <summary>
/// Thin P/Invoke surface over pdfium.dll (SDD §4.3: a wrapper we own, no generated bindings).
/// PDFium is not thread-safe — every call must hold <see cref="PdfiumLibrary.Lock"/>.
/// Targets win-x64, where stdcall/cdecl are the same ABI.
/// </summary>
internal static class PdfiumNative
{
    private const string Dll = "pdfium";

    // FPDF_RenderPageBitmap flags
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;

    // FPDF_SaveAsCopy flags
    public const uint SAVE_DEFAULT = 0;
    public const uint FPDF_INCREMENTAL = 1;

    // FPDF_GetLastError codes
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;

    [DllImport(Dll)] public static extern void FPDF_InitLibrary();

    [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument(IntPtr dataBuf, int size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll)] public static extern uint FPDF_GetLastError();
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

    // --- Text extraction & editing (fpdf_edit.h, fpdf_text.h) ---

    public const int FPDF_PAGEOBJ_TEXT = 1;

    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(Dll)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);

    /// <summary>Buffer is UTF-16LE; length in FPDF_WCHARs; returns chars incl. NUL.</summary>
    [DllImport(Dll)] public static extern uint FPDFTextObj_GetText(IntPtr textObject, IntPtr textPage, [Out] byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern int FPDFTextObj_GetFontSize(IntPtr textObject, out float size);
    [DllImport(Dll)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObject);

    /// <summary>Buffer is UTF-8; returns bytes incl. NUL.</summary>
    [DllImport(Dll)] public static extern nuint FPDFFont_GetFamilyName(IntPtr font, [Out] byte[]? buffer, nuint length);

    [DllImport(Dll)] public static extern int FPDFText_SetText(IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Dll)] public static extern int FPDFPage_GenerateContent(IntPtr page);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteBlockDelegate(IntPtr self, IntPtr data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int Version;
        public IntPtr WriteBlock;
    }
}

/// <summary>
/// Global PDFium state: one-time init and the process-wide lock that serializes all
/// PDFium calls. The library is never torn down — it lives for the process lifetime.
/// </summary>
internal static class PdfiumLibrary
{
    public static readonly object Lock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Lock)
        {
            if (_initialized)
                return;
            PdfiumNative.FPDF_InitLibrary();
            _initialized = true;
        }
    }
}
