using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Core;

/// <summary>
/// P/Invoke surface over the shared engine core (`core/megapdf_core.h`, ADR-003):
/// the one implementation of the policy that used to be written per platform.
/// The library is `megapdf_core.dll` on Windows and `libmegapdf_core.dylib` on
/// macOS, built by tools/build-core.* and copied next to pdfium by the Core
/// project.
///
/// The core owns documents: <see cref="megapdf_open"/> copies the bytes and
/// initialises the form-fill environment, pages come from
/// <see cref="megapdf_load_page"/>, and the core serialises every call on its own
/// mutex. Since #112 every contract lives there; the desktop engine makes no
/// PDFium call of its own.
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

    /// <summary>
    /// <see cref="megapdf_last_error"/> when a file is past what the platform can address
    /// through PDFium's custom file access — 4 GiB on Windows, no limit elsewhere (#147).
    /// </summary>
    public const uint OpenErrTooLarge = 100;

    /// <summary>Opens a document from memory; the core copies the bytes. Zero on failure.</summary>
    [DllImport(Dll)]
    public static extern unsafe IntPtr megapdf_open(byte* bytes, nuint length,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    /// <summary>
    /// Opens a document from its file, read on demand (#147, #148): the core keeps the file
    /// open and PDFium reads the parts it needs through it, so nothing holds a copy of the
    /// document and its size stops being a limit. Zero on failure.
    /// </summary>
    [DllImport(Dll)]
    public static extern IntPtr megapdf_open_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    /// <summary>Opens bytes with the credentials <paramref name="like"/> was opened with (#132). Zero on failure.</summary>
    [DllImport(Dll)]
    public static extern unsafe IntPtr megapdf_open_like(IntPtr like, byte* bytes, nuint length);

    /// <summary><see cref="megapdf_open_file"/> with the credentials <paramref name="like"/> was opened with (#132).</summary>
    [DllImport(Dll)]
    public static extern IntPtr megapdf_open_file_like(IntPtr like,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Dll)]
    public static extern void megapdf_close(IntPtr document);

    /// <summary>1 when the document reads the file at <paramref name="path"/> — the same file, not the same name (#147).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_reads_file(IntPtr document, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    /// <summary>
    /// Moves the document onto a private copy of the file it reads, created at
    /// <paramref name="copyPath"/> and unnamed at once, so that file can be written in place
    /// (#147). MEGAPDF_OK (0), or MEGAPDF_ERR_FILE (-9) with the document still on its file.
    /// </summary>
    [DllImport(Dll)]
    public static extern int megapdf_read_from_copy(IntPtr document, [MarshalAs(UnmanagedType.LPUTF8Str)] string copyPath);

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

    // Contract 2: text runs and visual lines (#106) --------------------------

    /// <summary>One text object with visible text; bounds in crop space.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TextRun
    {
        public int ObjectIndex;
        public Rect Bounds;
        public double FontSize;
        public int IsTextBox;
    }

    public const uint TextAll = 0;
    public const uint TextBoxesOnly = 1;

    public const int TextRunText = 0;
    public const int TextRunFont = 1;
    public const int TextRunBoxId = 2;
    public const int TextRunBoxFont = 3;

    /// <summary>Reads a page's runs (lines on first use); the result outlives the page. Zero on failure.</summary>
    [DllImport(Dll)]
    public static extern IntPtr megapdf_text_load(IntPtr page, uint flags);

    [DllImport(Dll)]
    public static extern void megapdf_text_free(IntPtr text);

    [DllImport(Dll)]
    public static extern nuint megapdf_text_run_count(IntPtr text);

    [DllImport(Dll)]
    public static extern int megapdf_text_run_get(IntPtr text, nuint index, out TextRun run);

    /// <summary>UTF-16 code units, no terminator, count-then-fill.</summary>
    [DllImport(Dll)]
    public static extern nuint megapdf_text_run_string(IntPtr text, nuint index, int field, [Out] ushort[]? outUnits, nuint capacity);

    [DllImport(Dll)]
    public static extern nuint megapdf_text_line_count(IntPtr text);

    [DllImport(Dll)]
    public static extern int megapdf_text_line_get(IntPtr text, nuint index, out Rect bounds);

    [DllImport(Dll)]
    public static extern nuint megapdf_text_line_runs(IntPtr text, nuint index, [Out] nuint[]? outRunIndices, nuint capacity);

    /// <summary>A run's string field, decoded.</summary>
    public static string TextRunString(IntPtr text, nuint index, int field)
    {
        var n = (int)megapdf_text_run_string(text, index, field, null, 0);
        if (n == 0)
            return "";
        var units = new ushort[n];
        megapdf_text_run_string(text, index, field, units, (nuint)n);
        return new string(System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, char>(units));
    }

    // Contract 3: AcroForm fields (#107) ------------------------------------

    public const int FieldOther = 0;
    public const int FieldText = 1;
    public const int FieldCheckbox = 2;
    public const int FieldRadio = 3;
    public const int FieldName = 0;
    public const int FieldValue = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct FormField
    {
        public int Kind;
        public int IsChecked;
        public Rect Bounds;
    }

    [DllImport(Dll)]
    public static extern IntPtr megapdf_form_fields_load(IntPtr page);

    [DllImport(Dll)]
    public static extern void megapdf_form_fields_free(IntPtr fields);

    [DllImport(Dll)]
    public static extern nuint megapdf_form_field_count(IntPtr fields);

    [DllImport(Dll)]
    public static extern int megapdf_form_field_get(IntPtr fields, nuint index, out FormField field);

    [DllImport(Dll)]
    public static extern nuint megapdf_form_field_string(IntPtr fields, nuint index, int which, [Out] ushort[]? outUnits, nuint capacity);

    /// <summary>A simulated click at (x, y) in crop space, focus released afterwards.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_form_click(IntPtr page, double x, double y);

    /// <summary>Click, select all, replace the selection, release focus.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_form_set_text(IntPtr page, double x, double y, [MarshalAs(UnmanagedType.LPWStr)] string value);

    /// <summary>Commits any in-progress field edit; call before serialising.</summary>
    [DllImport(Dll)]
    public static extern void megapdf_form_commit(IntPtr document);

    public static string FormFieldString(IntPtr fields, nuint index, int which)
    {
        var n = (int)megapdf_form_field_string(fields, index, which, null, 0);
        if (n == 0)
            return "";
        var units = new ushort[n];
        megapdf_form_field_string(fields, index, which, units, (nuint)n);
        return new string(System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, char>(units));
    }

    // Contract 4: stamps and MegaPDF_Id marks (#108) -------------------------

    public const int MarkCross = 0;
    public const int MarkCheck = 1;
    public const int MarkFilledSquare = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Stamp
    {
        public int AnnotIndex;
        public Rect Bounds;
    }

    [DllImport(Dll)]
    public static extern int megapdf_add_check_mark(IntPtr page, ref Rect square, int style, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(Dll)]
    public static extern unsafe int megapdf_add_image_stamp(IntPtr page, byte* bgra, int width, int height, ref Rect bounds,
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(Dll)]
    public static extern IntPtr megapdf_stamps_load(IntPtr page);

    [DllImport(Dll)]
    public static extern void megapdf_stamps_free(IntPtr stamps);

    [DllImport(Dll)]
    public static extern nuint megapdf_stamp_count(IntPtr stamps);

    [DllImport(Dll)]
    public static extern int megapdf_stamp_get(IntPtr stamps, nuint index, out Stamp stamp);

    [DllImport(Dll)]
    public static extern nuint megapdf_stamp_id(IntPtr stamps, nuint index, [Out] ushort[]? outUnits, nuint capacity);

    [DllImport(Dll)]
    public static extern IntPtr megapdf_stamp_image_load(IntPtr page, int annotIndex);

    [DllImport(Dll)]
    public static extern void megapdf_image_free(IntPtr image);

    [DllImport(Dll)]
    public static extern int megapdf_image_width(IntPtr image);

    [DllImport(Dll)]
    public static extern int megapdf_image_height(IntPtr image);

    [DllImport(Dll)]
    public static extern nuint megapdf_image_pixels(IntPtr image, [Out] byte[]? outBgra, nuint capacity);

    [DllImport(Dll)]
    public static extern int megapdf_remove_annotation(IntPtr page, int annotIndex);

    [DllImport(Dll)]
    public static extern int megapdf_remove_stamp(IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(Dll)]
    public static extern int megapdf_move_image_stamp(IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string id, ref Rect bounds);

    public static string StampId(IntPtr stamps, nuint index)
    {
        var n = (int)megapdf_stamp_id(stamps, index, null, 0);
        if (n == 0)
            return "";
        var units = new ushort[n];
        megapdf_stamp_id(stamps, index, units, (nuint)n);
        return new string(System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, char>(units));
    }

    // Contract 5: whiteouts, text boxes and detached objects (#109) -----------

    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectRect
    {
        public int ObjectIndex;
        public Rect Bounds;
    }

    // Contract 8: redaction (#173) ------------------------------------------
    //
    // Marks are the core's own and are never written to the file, so a document saved
    // with marks on it cannot carry them. The apps draw them in their overlay layer.

    [StructLayout(LayoutKind.Sequential)]
    public struct RedactionArea
    {
        public int MarkId;
        public Rect Bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RedactOptions
    {
        public uint Colour;           // 0xRRGGBB; 0 is black, the default
        public int LeaveAreaBare;     // 1: draw no box at all
        public int KeepMetadata;      // 1: leave /Info and XMP alone
        public int KeepMatchingOutline;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RedactionRefusal
    {
        public int PageIndex;
        public int Reason;            // RedactReason*
        public Rect Area;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RedactionApplied
    {
        public int PageIndex;
        public Rect Marked;
        public Rect Affected;
    }

    /// <summary>megapdf_redaction_counts: what a completed apply removed.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RedactionCounts
    {
        public int Areas, Pages;
        public int Characters, TextRuns, PartialRuns, HiddenCopies;
        public int Images, InlineImages, SoftMasks;
        public int Paths, Shadings, FormXObjects;
        public int Annotations, FormFields, Links;
        public int OutlineEntries, StructureEntries, PageLabels;
        public int MetadataFields;
    }

    public const int RedactReasonType3Font = 1;
    public const int RedactReasonFontCannotRedraw = 2;
    public const int RedactReasonLayout = 3;
    public const int RedactReasonSharedForm = 4;
    public const int RedactReasonImage = 5;
    public const int RedactReasonAnnotation = 6;
    public const int RedactReasonPdfium = 7;

    [DllImport(Dll)]
    public static extern int megapdf_redaction_mark(IntPtr page, ref Rect area, out int markId);

    [DllImport(Dll)]
    public static extern nuint megapdf_redaction_mark_text(IntPtr page, ref Rect selection,
        [Out] int[]? outMarkIds, nuint capacity);

    [DllImport(Dll)]
    public static extern nuint megapdf_redaction_marks(IntPtr page, [Out] RedactionArea[]? outAreas, nuint capacity);

    [DllImport(Dll)]
    public static extern int megapdf_redaction_move_mark(IntPtr page, int markId, ref Rect area);

    [DllImport(Dll)]
    public static extern int megapdf_redaction_remove_mark(IntPtr page, int markId);

    [DllImport(Dll)]
    public static extern nuint megapdf_redaction_mark_count(IntPtr document);

    [DllImport(Dll)]
    public static extern void megapdf_redaction_clear(IntPtr document);

    [DllImport(Dll)]
    public static extern int megapdf_redact_apply(IntPtr document, ref RedactOptions options, out IntPtr report);

    [DllImport(Dll)]
    public static extern void megapdf_redaction_report_free(IntPtr report);

    [DllImport(Dll)]
    public static extern int megapdf_redaction_report_counts(IntPtr report, out RedactionCounts counts);

    [DllImport(Dll)]
    public static extern nuint megapdf_redaction_refusals(IntPtr report, [Out] RedactionRefusal[]? outRefusals,
        nuint capacity);

    [DllImport(Dll)]
    public static extern nuint megapdf_redaction_applied_areas(IntPtr report, [Out] RedactionApplied[]? outAreas,
        nuint capacity);

    [DllImport(Dll)]
    private static extern nuint megapdf_redaction_refusal_message(IntPtr report, nuint index, byte[]? outBytes,
        nuint capacity);

    /// <summary>The refusal's English sentence — for logs and tests; the UI shows its own wording.</summary>
    public static string RefusalMessage(IntPtr report, nuint index)
    {
        var n = (int)megapdf_redaction_refusal_message(report, index, null, 0);
        if (n <= 1)
            return "";
        var bytes = new byte[n];
        megapdf_redaction_refusal_message(report, index, bytes, (nuint)n);
        return System.Text.Encoding.UTF8.GetString(bytes, 0, n - 1);
    }

    [DllImport(Dll)]
    public static extern int megapdf_redaction_poisoned(IntPtr document);

    [DllImport(Dll)]
    public static extern int megapdf_page_object_count(IntPtr page);

    [DllImport(Dll)]
    public static extern int megapdf_add_whiteout(IntPtr page, ref Rect bounds, out int objectIndex);

    [DllImport(Dll)]
    public static extern nuint megapdf_whiteouts(IntPtr page, [Out] ObjectRect[]? outRects, nuint capacity);

    [DllImport(Dll)]
    public static extern int megapdf_add_text_box(IntPtr page, int objectIndex, [MarshalAs(UnmanagedType.LPWStr)] string text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fontName, double fontSize, double baselineX, double baselineY,
        [MarshalAs(UnmanagedType.LPWStr)] string id, out int outObjectIndex);

    [DllImport(Dll)]
    public static extern int megapdf_restyle_text_box(IntPtr page, int objectIndex, [MarshalAs(UnmanagedType.LPWStr)] string text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fontName, double fontSize, double left, double bottom,
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(Dll)]
    public static extern int megapdf_find_text_box(IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(Dll)]
    public static extern int megapdf_object_type(IntPtr page, int objectIndex);

    [DllImport(Dll)]
    public static extern int megapdf_object_bounds(IntPtr page, int objectIndex, out Rect bounds);

    [DllImport(Dll)]
    public static extern int megapdf_move_text_box(IntPtr page, int objectIndex, double left, double bottom);

    [DllImport(Dll)]
    public static extern int megapdf_remove_text_box(IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string id);

    /// <summary>Removes exactly one page object and keeps it alive for undo; the core owns it until restored, discarded or the document closes.</summary>
    [DllImport(Dll)]
    public static extern IntPtr megapdf_detach_object(IntPtr page, int objectIndex);

    /// <summary>Puts a one-object handle back at the index; consumes the handle on success. Refuses a handle holding more (#136).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_restore_object(IntPtr page, IntPtr detached, int objectIndex);

    [DllImport(Dll)]
    public static extern void megapdf_discard_detached(IntPtr detached);

    /// <summary>
    /// Removes body text (a line or one run) with the hidden copies drawn under it (#136), all
    /// in one handle; <see cref="megapdf_restore_detached"/> puts everything back.
    /// </summary>
    [DllImport(Dll)]
    public static extern IntPtr megapdf_detach_text_runs(IntPtr page, int[] objectIndices, nuint count);

    /// <summary>Undoes whatever produced the handle, every object back at its own index; consumes it on success.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_restore_detached(IntPtr page, IntPtr detached);

    [StructLayout(LayoutKind.Sequential)]
    public struct DetachedPart
    {
        public int ObjectIndex;
        public int CopyOf;
    }

    [DllImport(Dll)]
    public static extern nuint megapdf_detached_count(IntPtr detached);

    [DllImport(Dll)]
    public static extern int megapdf_detached_get(IntPtr detached, nuint index, out DetachedPart part);

    // Contract 6: save, flatten and images (#110) -----------------------------

    /// <summary>Receives one block of the serialised PDF; return 1 to continue, 0 to abort.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteDelegate(IntPtr context, IntPtr data, nuint size);

    /// <summary>Commits form edits and writes the whole document (full rewrite, #97) through the callback.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_save(IntPtr document, WriteDelegate write, IntPtr context);

    // Document security (#131) ------------------------------------------------

    /// <summary>megapdf_security: 32-bit permissions on every platform, so one layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct megapdf_security
    {
        public int encrypted;
        public int revision;
        public uint permissions;
        public int full_access;
    }

    [DllImport(Dll)]
    public static extern int megapdf_security_info(IntPtr document, out megapdf_security security);

    /// <summary>A copy under new AES-256 security. MEGAPDF_ERR_RESTRICTED (-6) without full access.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_save_with_security(IntPtr document,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? userPassword,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? ownerPassword,
        uint permissions, WriteDelegate write, IntPtr context);

    /// <summary>A copy with no security. MEGAPDF_ERR_RESTRICTED (-6) without full access.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_save_without_security(IntPtr document, WriteDelegate write, IntPtr context);

    [DllImport(Dll)]
    public static extern int megapdf_flatten_all(IntPtr document);

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageInfo
    {
        public int PageIndex;
        public int ObjectIndex;
        public int PixelWidth;
        public int PixelHeight;
        public double DisplayWidth;
        public double DisplayHeight;
        public long StoredBytes;
    }

    [DllImport(Dll)]
    public static extern IntPtr megapdf_images_load(IntPtr document);

    [DllImport(Dll)]
    public static extern void megapdf_images_free(IntPtr images);

    [DllImport(Dll)]
    public static extern nuint megapdf_image_count(IntPtr images);

    [DllImport(Dll)]
    public static extern int megapdf_image_get(IntPtr images, nuint index, out ImageInfo info);

    [DllImport(Dll)]
    public static extern IntPtr megapdf_render_image(IntPtr document, int pageIndex, int objectIndex, int width, int height);

    [DllImport(Dll)]
    public static extern unsafe int megapdf_replace_image_jpeg(IntPtr document, int pageIndex, int objectIndex, byte* jpeg, nuint length);

    // Contract 7: render policy (#111) -----------------------------------------

    public const uint RenderBgra = 0;
    public const uint RenderRgba = 1;

    /// <summary>The aspect-preserving clamp of an ideal raster size (16,384 px a side, 32 MP).</summary>
    [DllImport(Dll)]
    public static extern void megapdf_render_size(double idealWidth, double idealHeight, out int width, out int height);

    [DllImport(Dll)]
    public static extern int megapdf_render_is_capped(double idealWidth, double idealHeight);

    /// <summary>White ground, page content with annotations and LCD text, then live form values, into the caller's buffer.</summary>
    [DllImport(Dll)]
    public static extern unsafe int megapdf_render(IntPtr page, byte* buffer, int width, int height, int stride, uint flags);

    // Phase 3: body-text editing (#112) ---------------------------------------

    public const int ErrNoFont = -4;
    public const int ErrLayout = -5;
    public const int EditInPlace = 0;
    public const int EditSubstituted = 1;

    public const uint SetTextForceSubstitute = 1;

    /// <summary>
    /// Sets a run's text as a new object at the same index; the untouched original, with any
    /// hidden copy of the run (#136), is handed back through <paramref name="replaced"/> for a
    /// byte-identical undo with <see cref="megapdf_restore_detached"/> (#117).
    /// </summary>
    [DllImport(Dll)]
    public static extern int megapdf_set_text(IntPtr page, int objectIndex, [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint flags, out int outcome, out IntPtr replaced);

    /// <summary>
    /// megapdf_set_text on the first index, the line's other runs removed and every run's hidden
    /// copies with them (#136), in one call and one handle.
    /// </summary>
    [DllImport(Dll)]
    public static extern int megapdf_set_line_text(IntPtr page, int[] objectIndices, nuint count,
        [MarshalAs(UnmanagedType.LPWStr)] string text, uint flags, out int outcome, out IntPtr replaced);

    [DllImport(Dll)]
    public static extern int megapdf_insert_text_run(IntPtr page, int objectIndex, [MarshalAs(UnmanagedType.LPWStr)] string text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fontName, double fontSize, double left, double baseline);

    /// <summary>1 when rewriting the object's content stream keeps the page as it is, 0 when not (#118).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_text_editable(IntPtr page, int objectIndex);

    /// <summary>megapdf_layout_verdict (#128): why the layout guard answered as it did.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct megapdf_layout_verdict
    {
        public int editable;
        public int cause;
        public int where;
        public int changed_pixels;
        public int total_pixels;
        public double max_shift_pt;
    }

    /// <summary>megapdf_text_editable with its reason: 1, 0, or MEGAPDF_ERR_ARGUMENT (-1).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_text_editable_reason(IntPtr page, int objectIndex, out megapdf_layout_verdict verdict);

    /// <summary>The verdict behind this thread's latest layout refusal; read straight after the refused call.</summary>
    [DllImport(Dll)]
    public static extern int megapdf_last_layout_verdict(out megapdf_layout_verdict verdict);

    /// <summary>Whether regenerating the page changes how it looks (#139): 1 no, 0 yes, or MEGAPDF_ERR_ARGUMENT (-1).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_page_regeneration_verdict(IntPtr page, out megapdf_layout_verdict verdict);

    /// <summary>MEGAPDF_ERR_CANCELLED: a page check stopped early (#145).</summary>
    public const int ErrCancelled = -7;

    /// <summary>MEGAPDF_ERR_NOT_JUDGED: no cached page verdict yet (#145).</summary>
    public const int ErrNotJudged = -8;

    /// <summary>The page check with a cancel flag; lets other calls run between its stages (#145).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_page_regeneration_verdict_cancellable(IntPtr page, IntPtr cancel, out megapdf_layout_verdict verdict);

    /// <summary>The cached page verdict: 1, 0, or MEGAPDF_ERR_NOT_JUDGED (#145).</summary>
    [DllImport(Dll)]
    public static extern int megapdf_page_regeneration_verdict_cached(IntPtr page, out megapdf_layout_verdict verdict);

    [DllImport(Dll)]
    public static extern IntPtr megapdf_cancel_new();

    /// <summary>Safe from any thread, at any time before the flag is freed.</summary>
    [DllImport(Dll)]
    public static extern void megapdf_cancel_raise(IntPtr cancel);

    [DllImport(Dll)]
    public static extern void megapdf_cancel_free(IntPtr cancel);

    [DllImport(Dll)]
    public static extern int megapdf_is_subset_font_name([MarshalAs(UnmanagedType.LPUTF8Str)] string baseName);

    [DllImport(Dll)]
    public static extern nuint megapdf_map_to_standard_font([MarshalAs(UnmanagedType.LPUTF8Str)] string originalName,
        [Out] byte[]? outName, nuint capacity);
}
