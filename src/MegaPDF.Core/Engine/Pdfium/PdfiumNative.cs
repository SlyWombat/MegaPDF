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

    /// <summary>
    /// FPDF_FORMFILLINFO version 1. PDFium keeps the POINTER we pass — the struct
    /// must live in unmanaged memory for the document's lifetime.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FORMFILLINFO
    {
        public int Version;
        public IntPtr Release;
        public IntPtr FFI_Invalidate;
        public IntPtr FFI_OutputSelectedRect;
        public IntPtr FFI_SetCursor;
        public IntPtr FFI_SetTimer;
        public IntPtr FFI_KillTimer;
        public IntPtr FFI_GetLocalTime;
        public IntPtr FFI_OnChange;
        public IntPtr FFI_GetPage;
        public IntPtr FFI_GetCurrentPage;
        public IntPtr FFI_GetRotation;
        public IntPtr FFI_ExecuteNamedAction;
        public IntPtr FFI_SetTextFieldFocus;
        public IntPtr FFI_DoURIAction;
        public IntPtr FFI_DoGoToAction;
        public IntPtr JsPlatform;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    // Callback delegate shapes for the FFI members PDFium may invoke.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiVoidDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiInvalidateDelegate(IntPtr self, IntPtr page, double left, double top, double right, double bottom);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiSetCursorDelegate(IntPtr self, int cursorType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FfiSetTimerDelegate(IntPtr self, int elapseMs, IntPtr timerFunc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiKillTimerDelegate(IntPtr self, int timerId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate FPDF_SYSTEMTIME FfiGetLocalTimeDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr FfiGetPageDelegate(IntPtr self, IntPtr document, int pageIndex);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr FfiGetCurrentPageDelegate(IntPtr self, IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FfiGetRotationDelegate(IntPtr self, IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiExecuteNamedActionDelegate(IntPtr self, IntPtr namedAction);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiSetTextFieldFocusDelegate(IntPtr self, IntPtr value, uint valueLen, int isFocus);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiDoUriActionDelegate(IntPtr self, IntPtr uri);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FfiDoGoToActionDelegate(IntPtr self, int pageIndex, int zoomMode, IntPtr posArray, int arraySize);

    [DllImport(Dll)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);
    [DllImport(Dll)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);
    [DllImport(Dll)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr formHandle);
    [DllImport(Dll)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr formHandle);
    [DllImport(Dll)] public static extern int FORM_OnLButtonDown(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Dll)] public static extern int FORM_OnLButtonUp(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Dll)] public static extern int FORM_SelectAllText(IntPtr formHandle, IntPtr page);
    [DllImport(Dll)] public static extern void FORM_ReplaceSelection(IntPtr formHandle, IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Dll)] public static extern int FORM_ForceToKillFocus(IntPtr formHandle);
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
    [DllImport(Dll)] public static extern int FPDFAnnot_GetFormFieldType(IntPtr formHandle, IntPtr annot);
    /// <summary>UTF-16 buffer; returns length in bytes incl. NUL.</summary>
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetFormFieldName(IntPtr formHandle, IntPtr annot, [Out] byte[]? buffer, uint buflen);
    /// <summary>UTF-16 buffer; returns length in bytes incl. NUL.</summary>
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetFormFieldValue(IntPtr formHandle, IntPtr annot, [Out] byte[]? buffer, uint buflen);
    [DllImport(Dll)] public static extern int FPDFAnnot_IsChecked(IntPtr formHandle, IntPtr annot);

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
