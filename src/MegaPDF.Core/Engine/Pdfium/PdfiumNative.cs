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

    [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr document);

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

    // --- Text search (fpdf_text.h; simple find, issue #26) ---

    /// <summary>findWhat is an FPDF_WIDESTRING (UTF-16LE, NUL-terminated); flags 0 = case-insensitive substring.</summary>

    /// <summary>Computes the rects covering a char range; FPDFText_GetRect then reads them by index.</summary>

    // --- Font substitution (tier 2, SDD §3.1) ---

    /// <summary>Buffer is UTF-8; returns bytes incl. NUL. Subset fonts carry an ABCDEF+ prefix.</summary>
    [DllImport(Dll)] public static extern nuint FPDFFont_GetBaseFontName(IntPtr font, [Out] byte[]? buffer, nuint length);

    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadStandardFont(IntPtr document, [MarshalAs(UnmanagedType.LPUTF8Str)] string font);
    [DllImport(Dll)] public static extern void FPDFFont_Close(IntPtr font);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);
    [DllImport(Dll)] public static extern int FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr pageObject, nuint index);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);
    [DllImport(Dll)] public static extern void FPDFPageObj_Destroy(IntPtr pageObject);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, out FS_MATRIX matrix);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetMatrix(IntPtr pageObject, ref FS_MATRIX matrix);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetFillColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX
    {
        public float A, B, C, D, E, F;
    }

    // --- AcroForm form-fill environment (fpdf_formfill.h) ---

    public const int FPDF_FORMFIELD_CHECKBOX = 2;
    public const int FPDF_FORMFIELD_RADIOBUTTON = 3;
    public const int FPDF_FORMFIELD_TEXTFIELD = 6;
    public const int FPDF_ANNOT_SUBTYPE_WIDGET = 20;
    public const int FPDF_ANNOT_SUBTYPE_STAMP = 13;

    // The FPDF_FORMFILLINFO environment, page load/close hooks, document open and
    // text search moved into the shared core with #105 (ADR-003); the form handle
    // used below comes from CoreNative.megapdf_document_form_raw.
    [DllImport(Dll)] public static extern void FPDF_FFLDraw(IntPtr formHandle, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // --- Annotations (fpdf_annot.h) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF
    {
        public float Left, Top, Right, Bottom;
    }

    [DllImport(Dll)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetRect(IntPtr annot, out FS_RECTF rect);
    /// <summary>UTF-16 buffer; returns length in bytes incl. NUL.</summary>
    /// <summary>UTF-16 buffer; returns length in bytes incl. NUL.</summary>

    // --- Drawn-square detection & mark stamps (SDD §3.2) ---

    public const int FPDF_PAGEOBJ_PATH = 2;

    [DllImport(Dll)] public static extern int FPDFPath_GetDrawMode(IntPtr path, out int fillMode, out int stroke);

    [DllImport(Dll)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);
    [DllImport(Dll)] public static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr pageObject);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    /// <summary>UTF-16 buffer; returns length in bytes incl. NUL.</summary>
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [Out] byte[]? buffer, uint buflen);

    // --- Image stamps (signatures, SDD §3.3) ---

    public const int FPDFBitmap_BGRA = 4;

    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);
    [DllImport(Dll)] public static extern int FPDFImageObj_SetBitmap(IntPtr[] pages, int count, IntPtr imageObject, IntPtr bitmap);
    /// <summary>Renders the image object (masks applied) to a new BGRA bitmap the caller destroys.</summary>
    [DllImport(Dll)] public static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr imageObject);
    [DllImport(Dll)] public static extern IntPtr FPDFAnnot_GetObject(IntPtr annot, int index);
    [DllImport(Dll)] public static extern int FPDFAnnot_UpdateObject(IntPtr annot, IntPtr pageObject);
    [DllImport(Dll)] public static extern int FPDFImageObj_GetImagePixelSize(IntPtr imageObject, out uint width, out uint height);

    /// <summary>Bakes annotations and form fields into page content. 0=fail, 1=success, 2=nothing to do.</summary>
    [DllImport(Dll)] public static extern int FPDFPage_Flatten(IntPtr page, int flags);
    public const int FLAT_NORMALDISPLAY = 0;

    // --- Image compression (shrink-for-email) ---

    /// <summary>Returns the image's stored (compressed) stream length in bytes.</summary>
    [DllImport(Dll)] public static extern nuint FPDFImageObj_GetImageDataRaw(IntPtr imageObject, [Out] byte[]? buffer, nuint buflen);

    /// <summary>Replaces the image's stream with a JPEG read synchronously (inline) from the file access.</summary>
    [DllImport(Dll)] public static extern int FPDFImageObj_LoadJpegFileInline(IntPtr[]? pages, int count, IntPtr imageObject, ref FPDF_FILEACCESS fileAccess);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetBlockDelegate(IntPtr param, uint position, IntPtr buffer, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint FileLen;      // unsigned long is 4 bytes on Windows
        public IntPtr GetBlock;
        public IntPtr Param;
    }

    public const int FPDF_PAGEOBJ_IMAGE = 3;

    // --- Content marks (identify MegaPDF whiteout objects) ---

    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_AddMark(IntPtr pageObject, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll)] public static extern int FPDFPageObj_CountMarks(IntPtr pageObject);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_GetMark(IntPtr pageObject, int index);
    /// <summary>UTF-16 buffer; out length in bytes incl. NUL.</summary>
    [DllImport(Dll)] public static extern int FPDFPageObjMark_GetName(IntPtr mark, [Out] byte[]? buffer, uint buflen, out uint outBuflen);
    [DllImport(Dll)] public static extern int FPDFPageObjMark_SetStringParam(IntPtr document, IntPtr pageObject, IntPtr mark, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    /// <summary>UTF-16 buffer; out length in bytes incl. NUL.</summary>
    [DllImport(Dll)] public static extern int FPDFPageObjMark_GetParamStringValue(IntPtr mark, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [Out] byte[]? buffer, uint buflen, out uint outBuflen);

    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Dll)] public static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);
    [DllImport(Dll)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Dll)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr pageObject, float width);

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
