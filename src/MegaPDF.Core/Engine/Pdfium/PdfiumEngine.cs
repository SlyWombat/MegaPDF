using System.Runtime.InteropServices;
using MegaPDF.Core.Engine.Core;

namespace MegaPDF.Core.Engine.Pdfium;

/// <summary>Thrown when a document cannot be opened.</summary>
public sealed class PdfLoadException(string path, uint errorCode) : Exception(MessageFor(path, errorCode))
{
    public uint ErrorCode { get; } = errorCode;

    /// <summary>True when a password is required or the supplied one was wrong.</summary>
    public bool IsPasswordError => ErrorCode == PdfiumNative.FPDF_ERR_PASSWORD;

    /// <summary>True when the file itself could not be read (missing, locked, unreadable).</summary>
    public bool IsFileError => ErrorCode == PdfiumNative.FPDF_ERR_FILE;

    /// <summary>True when the bytes are not a PDF at all.</summary>
    public bool IsFormatError => ErrorCode == PdfiumNative.FPDF_ERR_FORMAT;

    private static string MessageFor(string path, uint code) => code switch
    {
        PdfiumNative.FPDF_ERR_FILE => $"The file could not be read: {path}",
        PdfiumNative.FPDF_ERR_FORMAT => $"The file is not a valid PDF: {path}",
        PdfiumNative.FPDF_ERR_PASSWORD => $"The PDF is password-protected: {path}",
        _ => $"The PDF could not be opened (error {code}): {path}",
    };
}

/// <summary>
/// PDFium-backed engine (SDD §4.3). Documents are loaded fully into memory so the
/// original file is never held open — Save can atomically replace it (SDD §3.4),
/// and cloud-synced files are never locked. The bytes, the PDFium document and its
/// form-fill environment are owned by the shared core (ADR-003, #105); this class
/// adapts the core's handles to <see cref="IPdfEngine"/> and still binds the
/// not-yet-migrated contracts to PDFium directly through the core's raw handles.
/// </summary>
public sealed class PdfiumEngine : IPdfEngine
{
    public IPdfDocument Open(string filePath, string? password = null)
    {
        PdfiumLibrary.EnsureInitialized();
        var bytes = File.ReadAllBytes(filePath);
        lock (PdfiumLibrary.Lock)
        {
            IntPtr core;
            unsafe
            {
                fixed (byte* p = bytes)
                    core = CoreNative.megapdf_open(p, (nuint)bytes.Length, password);
            }
            if (core == IntPtr.Zero)
                throw new PdfLoadException(filePath, CoreNative.megapdf_last_error());
            return new PdfiumDocument(core);
        }
    }

    public void Dispose()
    {
        // PDFium itself stays initialized for the process lifetime (PdfiumLibrary).
    }
}

internal sealed class PdfiumDocument : IPdfDocument
{
    /// <summary>The core's document handle (owns the bytes, the FPDF_DOCUMENT and the form environment).</summary>
    private readonly IntPtr _core;
    /// <summary>Raw FPDF_DOCUMENT, for the contracts still bound to PDFium directly.</summary>
    private readonly IntPtr _handle;
    /// <summary>Raw FPDF_FORMHANDLE, likewise.</summary>
    private readonly IntPtr _forms;
    private bool _disposed;

    internal PdfiumDocument(IntPtr core)
    {
        _core = core;
        _handle = CoreNative.megapdf_document_raw(core);
        _forms = CoreNative.megapdf_document_form_raw(core);
    }

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return CoreNative.megapdf_page_count(_core);
        }
    }

    public IPdfPage GetPage(int pageIndex)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var page = CoreNative.megapdf_load_page(_core, pageIndex);
            if (page == IntPtr.Zero)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page {pageIndex} could not be loaded.");
            return new PdfiumPage(_handle, _forms, page, pageIndex);
        }
    }

    public void Save(Stream target)
    {
        ThrowIfDisposed();
        // Always a full rewrite, on purpose (#97): PDFium's FPDF_INCREMENTAL does not
        // track which objects changed — it copies the original file and then appends
        // every indirect object the document has loaded, which after the open-time
        // size pass is the whole document again (1.97x the original at the median
        // over 4,263 corpus files, against 1.00x for the rewrite). The core commits
        // any in-progress form edit first and streams blocks to this callback (#110).
        Exception? writeError = null;
        int Write(IntPtr _, IntPtr data, nuint size)
        {
            try
            {
                var buffer = new byte[(int)size];
                Marshal.Copy(data, buffer, 0, buffer.Length);
                target.Write(buffer, 0, buffer.Length);
                return 1;
            }
            catch (Exception ex)
            {
                writeError = ex;
                return 0;
            }
        }

        var callback = new CoreNative.WriteDelegate(Write);
        var status = CoreNative.megapdf_save(_core, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (writeError is not null)
            throw new IOException("Writing the PDF failed.", writeError);
        if (status != 0)
            throw new IOException("PDFium could not serialize the document.");
    }

    public void FlattenAllPages()
    {
        ThrowIfDisposed();
        // Commits any in-progress form editing, then bakes every page — in the core.
        if (CoreNative.megapdf_flatten_all(_core) != 0)
            throw new InvalidOperationException("Flattening the document failed.");
    }

    public IReadOnlyList<PdfImageInfo> GetImages()
    {
        ThrowIfDisposed();
        var images = CoreNative.megapdf_images_load(_core);
        if (images == IntPtr.Zero)
            return [];
        try
        {
            var count = (int)CoreNative.megapdf_image_count(images);
            var result = new List<PdfImageInfo>(count);
            for (var i = 0; i < count; i++)
            {
                CoreNative.megapdf_image_get(images, (nuint)i, out var info);
                result.Add(new PdfImageInfo(info.PageIndex, info.ObjectIndex, info.PixelWidth, info.PixelHeight,
                    info.DisplayWidth, info.DisplayHeight, info.StoredBytes));
            }
            return result;
        }
        finally
        {
            CoreNative.megapdf_images_free(images);
        }
    }

    public StampImage RenderImageAt(PdfImageInfo image, int targetWidth, int targetHeight)
    {
        ThrowIfDisposed();
        var rendered = CoreNative.megapdf_render_image(_core, image.PageIndex, image.ObjectIndex, targetWidth, targetHeight);
        if (rendered == IntPtr.Zero)
            throw new InvalidOperationException("The image could not be rendered.");
        try
        {
            var pixels = new byte[(int)CoreNative.megapdf_image_pixels(rendered, null, 0)];
            CoreNative.megapdf_image_pixels(rendered, pixels, (nuint)pixels.Length);
            return new StampImage(pixels, CoreNative.megapdf_image_width(rendered), CoreNative.megapdf_image_height(rendered));
        }
        finally
        {
            CoreNative.megapdf_image_free(rendered);
        }
    }

    public void ReplaceImageWithJpeg(PdfImageInfo image, byte[] jpegBytes)
    {
        ThrowIfDisposed();
        int status;
        unsafe
        {
            fixed (byte* p = jpegBytes)
                status = CoreNative.megapdf_replace_image_jpeg(_core, image.PageIndex, image.ObjectIndex, p, (nuint)jpegBytes.Length);
        }
        if (status != 0)
            throw new InvalidOperationException("The compressed image could not be applied.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (PdfiumLibrary.Lock)
        {
            // Tears down the form environment and any page still open, then the document.
            CoreNative.megapdf_close(_core);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class PdfiumPage : IPdfPage
{
    private readonly IntPtr _document;
    private readonly IntPtr _forms;
    /// <summary>The core's page handle; the form-fill hooks were applied on load.</summary>
    private readonly IntPtr _core;
    /// <summary>Raw FPDF_PAGE, for the contracts still bound to PDFium directly.</summary>
    private readonly IntPtr _handle;
    private bool _disposed;

    internal PdfiumPage(IntPtr document, IntPtr forms, IntPtr core, int index)
    {
        _document = document;
        _forms = forms;
        _core = core;
        _handle = CoreNative.megapdf_page_raw(core);
        Index = index;
        Width = CoreNative.megapdf_page_width(core);
        Height = CoreNative.megapdf_page_height(core);
        // pdfium reports page *content* in user space, whose origin is the
        // MediaBox — but it renders, and sizes, the CropBox. When the two differ
        // (imposed pages, trimmed scans) every coordinate we hand the UI is out by
        // the difference: highlights, checkbox squares and click targets all land
        // on the wrong part of the page (#28). The core owns that origin; the
        // contracts it has absorbed already return crop space, and the ones still
        // bound directly convert through it below.
        CoreNative.megapdf_page_crop_origin(core, out var cropX, out var cropY);
        _cropLeft = cropX;
        _cropTop = cropY + Height;
    }

    public int Index { get; }
    public double Width { get; }
    public double Height { get; }

    private readonly double _cropLeft;
    private readonly double _cropTop;

    /// <summary>PDF user space (bottom-left, MediaBox origin) to view space (top-left, crop origin).</summary>
    private double ViewX(double userX) => userX - _cropLeft;
    private double ViewY(double userTop) => _cropTop - userTop;

    /// <summary>The core's crop space (bottom-left, crop origin) to view space (top-left).</summary>
    private PdfRect CropToView(double left, double bottom, double right, double top) =>
        new(left, Height - top, right - left, top - bottom);

    /// <summary>View space (top-left) to the core's crop space (bottom-left).</summary>
    private CoreNative.Rect ViewToCrop(PdfRect r) =>
        new() { Left = r.X, Bottom = Height - r.Bottom, Right = r.Right, Top = Height - r.Y };

    /// <summary>View space back to PDF user space.</summary>
    private double UserX(double viewX) => viewX + _cropLeft;
    private double UserY(double viewY) => _cropTop - viewY;

    public RenderedPage Render(int pixelWidth, int pixelHeight)
    {
        ThrowIfDisposed();
        // The shared recipe — white ground, page content with annotations and LCD
        // text, then live form-field values — and the refusal policy live in the core
        // (#111); this allocates the pixels and hands them over.
        var pixels = new byte[checked(pixelWidth * pixelHeight * 4)];
        int status;
        unsafe
        {
            fixed (byte* buffer = pixels)
                status = CoreNative.megapdf_render(_core, buffer, pixelWidth, pixelHeight, pixelWidth * 4, CoreNative.RenderBgra);
        }
        if (status == -2)
            throw new OutOfMemoryException($"Could not allocate a {pixelWidth}x{pixelHeight} render bitmap.");
        if (status != 0)
            throw new InvalidOperationException($"A {pixelWidth}x{pixelHeight} render is outside the limits; fit it with RenderLimits first.");
        return new RenderedPage(pixelWidth, pixelHeight, pixels);
    }

    public PageHit HitTest(PdfPoint point)
    {
        // Our own stamps sit on top of everything (clicking one removes/selects it).
        foreach (var (_, id, bounds) in GetMegaPdfStamps())
        {
            if (bounds.Contains(point))
                return new PageHit(PageHitKind.StampAnnotation, AnnotationId: id, Bounds: bounds);
        }

        // Form fields win over body text — they sit on top and are the reliable path.
        foreach (var field in GetFormFields())
        {
            if (!field.Bounds.Contains(point))
                continue;
            return field.Kind switch
            {
                FormFieldKind.Text => new PageHit(PageHitKind.FormTextField, Field: field),
                FormFieldKind.Checkbox or FormFieldKind.RadioButton => new PageHit(PageHitKind.FormCheckbox, Field: field),
                _ => new PageHit(PageHitKind.None),
            };
        }

        // MegaPDF text boxes are directly selectable (move/nudge/delete), so they win
        // over the body-text/whiteout layer beneath. Highest object index first, since a
        // later text box paints over an earlier one.
        foreach (var box in GetTextBoxes().OrderByDescending(b => b.ObjectIndex))
        {
            if (box.Bounds.Contains(point))
                return new PageHit(PageHitKind.TextBox, TextRun: box, Bounds: box.Bounds,
                    ObjectIndex: box.ObjectIndex,
                    TextLine: new PdfTextLine([box], box.Text, box.Bounds, box.FontName, box.FontSize));
        }

        // Whiteouts and text share the content layer; later objects paint on top,
        // so when both are under the click, the higher object index wins (a text
        // box placed over a whiteout must stay editable).
        var whiteout = GetWhiteouts()
            .Where(w => w.Bounds.Contains(point))
            .OrderByDescending(w => w.ObjectIndex)
            .Select(w => ((int Index, PdfRect Bounds)?)w)
            .FirstOrDefault();
        var line = GetTextLines().FirstOrDefault(l => l.Bounds.Contains(point));

        if (whiteout is { } w2 && (line is null || line.Runs.Max(r => r.ObjectIndex) < w2.Index))
            return new PageHit(PageHitKind.Whiteout, Bounds: w2.Bounds, ObjectIndex: w2.Index);

        foreach (var square in DetectCheckboxSquares())
        {
            if (square.Contains(point))
                return new PageHit(PageHitKind.DrawnCheckbox, Bounds: square);
        }

        if (line is not null)
            return new PageHit(PageHitKind.TextRun, TextRun: line.Runs[0], TextLine: line);
        return new PageHit(PageHitKind.None);
    }

    public IReadOnlyList<PdfTextLine> GetTextLines()
    {
        ThrowIfDisposed();
        // Contract 2 (#106): which runs share a baseline, where a baseline splits into
        // columns, and how lines are ordered is decided once, in the core.
        var text = CoreNative.megapdf_text_load(_core, CoreNative.TextAll);
        if (text == IntPtr.Zero)
            return [];
        try
        {
            var runs = ReadRuns(text, boxesOnly: false);
            var byIndex = runs.ToDictionary(r => r.Index, r => r.Run);
            var count = (int)CoreNative.megapdf_text_line_count(text);
            var lines = new List<PdfTextLine>(count);
            for (var j = 0; j < count; j++)
            {
                var n = (int)CoreNative.megapdf_text_line_runs(text, (nuint)j, null, 0);
                var indices = new nuint[n];
                CoreNative.megapdf_text_line_runs(text, (nuint)j, indices, (nuint)n);
                CoreNative.megapdf_text_line_get(text, (nuint)j, out var b);
                var members = indices.Select(k => byIndex[(int)k]).ToList();
                lines.Add(new PdfTextLine(members, string.Concat(members.Select(r => r.Text)),
                    CropToView(b.Left, b.Bottom, b.Right, b.Top), members[0].FontName, members[0].FontSize));
            }
            return lines;
        }
        finally
        {
            CoreNative.megapdf_text_free(text);
        }
    }

    /// <summary>
    /// The core's runs as <see cref="PdfTextRun"/>s with their run index. With
    /// <paramref name="boxesOnly"/> only MegaPDF text boxes, carrying their id and face
    /// (SDD §6.2 contract 4); otherwise every run, with the box fields left null as
    /// GetTextRuns always has.
    /// </summary>
    private List<(int Index, PdfTextRun Run)> ReadRuns(IntPtr text, bool boxesOnly)
    {
        var count = (int)CoreNative.megapdf_text_run_count(text);
        var runs = new List<(int, PdfTextRun)>(count);
        for (var i = 0; i < count; i++)
        {
            CoreNative.megapdf_text_run_get(text, (nuint)i, out var r);
            if (boxesOnly && r.IsTextBox == 0)
                continue;
            var bounds = CropToView(r.Bounds.Left, r.Bounds.Bottom, r.Bounds.Right, r.Bounds.Top);
            var content = CoreNative.TextRunString(text, (nuint)i, CoreNative.TextRunText);
            var font = CoreNative.TextRunString(text, (nuint)i, CoreNative.TextRunFont);
            if (!boxesOnly)
            {
                runs.Add((i, new PdfTextRun(r.ObjectIndex, content, bounds, font, r.FontSize)));
                continue;
            }
            var id = CoreNative.TextRunString(text, (nuint)i, CoreNative.TextRunBoxId);
            var face = CoreNative.TextRunString(text, (nuint)i, CoreNative.TextRunBoxFont);
            runs.Add((i, new PdfTextRun(r.ObjectIndex, content, bounds, font, r.FontSize,
                id.Length == 0 ? null : id, face.Length == 0 ? StandardTextBoxFonts.Default : face)));
        }
        return runs;
    }

    public IReadOnlyList<PdfRect> DetectCheckboxSquares()
    {
        ThrowIfDisposed();
        // The heuristic (SDD §3.2: stroked-not-filled paths, 6–24 pt, square
        // within 25%) lives in the shared engine core and is no longer written
        // here — one implementation serves all platforms (ADR-003, #38). The
        // core hands back crop-space rects with PDF's bottom-left origin; page
        // space here is top-left, so each one flips through CropToView.
        var count = (int)CoreNative.megapdf_detect_checkbox_squares(_core, null, 0);
        if (count == 0)
            return [];
        var buffer = new CoreNative.Rect[count];
        var filled = (int)CoreNative.megapdf_detect_checkbox_squares(_core, buffer, (nuint)count);

        var squares = new List<PdfRect>(filled);
        for (var i = 0; i < filled; i++)
        {
            var r = buffer[i];
            squares.Add(CropToView(r.Left, r.Bottom, r.Right, r.Top));
        }
        return squares;
    }

    public string AddCheckMarkStamp(PdfRect squareBounds, string? stampId = null, CheckMarkStyle style = CheckMarkStyle.Cross)
    {
        ThrowIfDisposed();
        var id = stampId ?? "mark:" + Guid.NewGuid().ToString("N");
        // Geometry (10% inset, 0x202020 ink, stroke max(1.2, w × 0.11)) and the three
        // styles (SDD §3.2 / Appendix B #3) are drawn by the core (#108).
        var square = ViewToCrop(squareBounds);
        var coreStyle = style switch
        {
            CheckMarkStyle.Check => CoreNative.MarkCheck,
            CheckMarkStyle.FilledSquare => CoreNative.MarkFilledSquare,
            _ => CoreNative.MarkCross,
        };
        if (CoreNative.megapdf_add_check_mark(_core, ref square, coreStyle, id) != 0)
            throw new InvalidOperationException("Could not draw the mark.");
        return id;
    }

    public IReadOnlyList<StampInfo> GetStamps()
    {
        ThrowIfDisposed();
        return GetMegaPdfStamps().Select(s => new StampInfo(s.Id, s.Bounds)).ToList();
    }

    /// <summary>All MegaPDF-placed stamps on the page: (annotation index, id, bounds in top-left space).</summary>
    private List<(int AnnotIndex, string Id, PdfRect Bounds)> GetMegaPdfStamps()
    {
        var stamps = CoreNative.megapdf_stamps_load(_core);
        if (stamps == IntPtr.Zero)
            return [];
        try
        {
            var count = (int)CoreNative.megapdf_stamp_count(stamps);
            var result = new List<(int, string, PdfRect)>(count);
            for (var i = 0; i < count; i++)
            {
                CoreNative.megapdf_stamp_get(stamps, (nuint)i, out var s);
                result.Add((s.AnnotIndex, CoreNative.StampId(stamps, (nuint)i),
                    CropToView(s.Bounds.Left, s.Bounds.Bottom, s.Bounds.Right, s.Bounds.Top)));
            }
            return result;
        }
        finally
        {
            CoreNative.megapdf_stamps_free(stamps);
        }
    }

    public IReadOnlyList<PdfTextRun> GetTextRuns()
    {
        ThrowIfDisposed();
        // Contract 2 (#106): every text object with visible text, in object order,
        // read once in the core (including the FPDFTextObj_GetText length-in-bytes quirk).
        var text = CoreNative.megapdf_text_load(_core, CoreNative.TextAll);
        if (text == IntPtr.Zero)
            return [];
        try
        {
            return ReadRuns(text, boxesOnly: false).Select(r => r.Run).ToList();
        }
        finally
        {
            CoreNative.megapdf_text_free(text);
        }
    }

    public IReadOnlyList<PdfSearchMatch> FindText(string term)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(term))
            return [];

        // Contract 1 (#26): case-insensitive substring, one rect per line spanned,
        // matches with no rects dropped. The core does the search and returns a
        // packed stream — per match: rect count, then (left, bottom, right, top)
        // per rect, in crop space — which decodes here into page space.
        var total = (int)CoreNative.megapdf_search_page(_core, term, null, 0);
        if (total == 0)
            return [];
        var packed = new double[total];
        var filled = (int)CoreNative.megapdf_search_page(_core, term, packed, (nuint)total);
        var matches = new List<PdfSearchMatch>();
        var pos = 0;
        while (pos < filled)
        {
            var rectCount = (int)packed[pos++];
            var rects = new List<PdfRect>(rectCount);
            for (var r = 0; r < rectCount && pos + 4 <= filled; r++, pos += 4)
                rects.Add(CropToView(packed[pos], packed[pos + 1], packed[pos + 2], packed[pos + 3]));
            if (rects.Count > 0)
                matches.Add(new PdfSearchMatch(rects));
        }
        return matches;
    }
    public IReadOnlyList<PdfFormField> GetFormFields()
    {
        ThrowIfDisposed();
        // Contract 3 (#107): every widget on the page, read through the core's form
        // environment; kinds, names, values and checked state come from one place.
        var fields = CoreNative.megapdf_form_fields_load(_core);
        if (fields == IntPtr.Zero)
            return [];
        try
        {
            var count = (int)CoreNative.megapdf_form_field_count(fields);
            var result = new List<PdfFormField>(count);
            for (var i = 0; i < count; i++)
            {
                CoreNative.megapdf_form_field_get(fields, (nuint)i, out var f);
                var kind = f.Kind switch
                {
                    CoreNative.FieldText => FormFieldKind.Text,
                    CoreNative.FieldCheckbox => FormFieldKind.Checkbox,
                    CoreNative.FieldRadio => FormFieldKind.RadioButton,
                    _ => FormFieldKind.Other,
                };
                result.Add(new PdfFormField(
                    CoreNative.FormFieldString(fields, (nuint)i, CoreNative.FieldName), kind,
                    CropToView(f.Bounds.Left, f.Bounds.Bottom, f.Bounds.Right, f.Bounds.Top),
                    CoreNative.FormFieldString(fields, (nuint)i, CoreNative.FieldValue), f.IsChecked != 0));
            }
            return result;
        }
        finally
        {
            CoreNative.megapdf_form_fields_free(fields);
        }
    }

    public TextEditOutcome SetTextRunText(PdfTextRun run, string newText)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(newText))
            throw new ArgumentException("PDFium cannot set empty text on a text object.", nameof(newText));

        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, run.ObjectIndex);
            if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                throw new InvalidOperationException($"Object {run.ObjectIndex} is no longer a text object.");

            // Tier 2 (SDD §3.1): a subset-embedded font only contains the glyphs the
            // document already uses. Setting text with uncovered characters would
            // silently render notdef boxes, so substitute a standard font instead.
            if (!NeedsFontSubstitution(obj, newText))
            {
                if (PdfiumNative.FPDFText_SetText(obj, newText) != 0)
                {
                    GenerateContent();
                    return TextEditOutcome.EditedInPlace;
                }
                // In-place set failed outright — fall through to substitution.
            }

            SubstituteTextObject(obj, run.ObjectIndex, newText);
            GenerateContent();
            return TextEditOutcome.EditedWithSubstitutedFont;
        }
    }

    private void GenerateContent()
    {
        if (PdfiumNative.FPDFPage_GenerateContent(_handle) == 0)
            throw new InvalidOperationException("PDFium failed to regenerate the page content stream.");
    }

    public DetachedTextRun DetachTextRun(PdfTextRun run)
    {
        ThrowIfDisposed();
        if (CoreNative.megapdf_object_type(_core, run.ObjectIndex) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
            throw new InvalidOperationException($"Object {run.ObjectIndex} is no longer a text object.");
        return DetachObject(run.ObjectIndex);
    }

    public DetachedTextRun DetachObjectAt(int objectIndex)
    {
        ThrowIfDisposed();
        if (CoreNative.megapdf_object_type(_core, objectIndex) < 0)
            throw new InvalidOperationException($"No page object at index {objectIndex}.");
        return DetachObject(objectIndex);
    }

    private DetachedTextRun DetachObject(int objectIndex)
    {
        // The core removes the object and keeps it alive for a possible undo (#109);
        // a handle never restored is freed when the document closes.
        var handle = CoreNative.megapdf_detach_object(_core, objectIndex);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Could not remove the object.");
        return new DetachedTextRun(handle);
    }

    public int AppendWhiteout(PdfRect bounds)
    {
        ThrowIfDisposed();
        var rect = ViewToCrop(bounds);
        if (CoreNative.megapdf_add_whiteout(_core, ref rect, out var index) != 0)
            throw new InvalidOperationException("Could not place the whiteout.");
        return index;
    }

    public IReadOnlyList<(int ObjectIndex, PdfRect Bounds)> GetWhiteouts()
    {
        ThrowIfDisposed();
        var count = (int)CoreNative.megapdf_whiteouts(_core, null, 0);
        if (count == 0)
            return [];
        var buffer = new CoreNative.ObjectRect[count];
        var filled = (int)CoreNative.megapdf_whiteouts(_core, buffer, (nuint)count);
        var whiteouts = new List<(int, PdfRect)>(filled);
        for (var i = 0; i < filled; i++)
            whiteouts.Add((buffer[i].ObjectIndex, CropToView(buffer[i].Bounds.Left, buffer[i].Bounds.Bottom, buffer[i].Bounds.Right, buffer[i].Bounds.Top)));
        return whiteouts;
    }

    /// <summary>
    /// The `id` carried by the object's MegaPDFTextBox mark (SDD §6.2 contract 4),
    /// or null when it has none — boxes written before the param existed.
    /// </summary>
    internal static string? ReadTextBoxId(IntPtr obj) => ReadMarkParam(obj, TextBoxIdKey);

    private static bool HasMark(IntPtr obj, string markName)
    {
        var marks = PdfiumNative.FPDFPageObj_CountMarks(obj);
        for (var m = 0; m < marks; m++)
        {
            var mark = PdfiumNative.FPDFPageObj_GetMark(obj, m);
            if (mark == IntPtr.Zero)
                continue;
            PdfiumNative.FPDFPageObjMark_GetName(mark, null, 0, out var lengthInBytes);
            if (lengthInBytes <= 2)
                continue;
            var buffer = new byte[lengthInBytes];
            PdfiumNative.FPDFPageObjMark_GetName(mark, buffer, lengthInBytes, out _);
            if (System.Text.Encoding.Unicode.GetString(buffer, 0, (int)lengthInBytes - 2) == markName)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The face named by the box's `font` mark param (#43), or the default when it
    /// carries none — which is every box written before #43, and what they all are.
    /// </summary>
    internal static string ReadTextBoxFont(IntPtr obj)
        => ReadMarkParam(obj, TextBoxFontKey) ?? StandardTextBoxFonts.Default;

    private static string? ReadMarkParam(IntPtr obj, string key)
    {
        var marks = PdfiumNative.FPDFPageObj_CountMarks(obj);
        for (var m = 0; m < marks; m++)
        {
            var mark = PdfiumNative.FPDFPageObj_GetMark(obj, m);
            if (mark == IntPtr.Zero)
                continue;
            PdfiumNative.FPDFPageObjMark_GetParamStringValue(mark, key, null, 0, out var lengthInBytes);
            if (lengthInBytes <= 2)
                continue;
            var buffer = new byte[lengthInBytes];
            if (PdfiumNative.FPDFPageObjMark_GetParamStringValue(mark, key, buffer, lengthInBytes, out _) == 0)
                continue;
            return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)lengthInBytes - 2);
        }
        return null;
    }

    private const string TextBoxMarkName = "MegaPDFTextBox";
    private const string TextBoxIdKey = "id";
    private const string TextBoxFontKey = "font";

    public int AppendTextBox(string text, double fontSize, PdfPoint topLeft,
                             string fontName = StandardTextBoxFonts.Default)
    {
        ThrowIfDisposed();
        if (!StandardTextBoxFonts.IsSupported(fontName))
            throw new ArgumentOutOfRangeException(nameof(fontName), fontName,
                "Text boxes are limited to the three standard faces (#43).");
        // The baseline sits one font size below the top-left the caller gave; the
        // core writes the MegaPDFTextBox mark with the id and the face (#109).
        var id = $"text:{Guid.NewGuid()}";
        if (CoreNative.megapdf_add_text_box(_core, -1, text, fontName, fontSize,
                topLeft.X, Height - (topLeft.Y + fontSize), id, out var index) != 0)
            throw new InvalidOperationException("Could not place the text box.");
        return index;
    }

    /// <summary>MegaPDF-added text boxes on this page, as their underlying text runs.</summary>
    public IReadOnlyList<PdfTextRun> GetTextBoxes()
    {
        ThrowIfDisposed();
        // The runs that carry the MegaPDFTextBox mark, with their id and face — the
        // core reads the marks with the runs (#106), and skips everything else.
        var text = CoreNative.megapdf_text_load(_core, CoreNative.TextBoxesOnly);
        if (text == IntPtr.Zero)
            return [];
        try
        {
            return ReadRuns(text, boxesOnly: true).Select(r => r.Run).ToList();
        }
        finally
        {
            CoreNative.megapdf_text_free(text);
        }
    }

    public void MoveTextBox(int objectIndex, PdfRect newBounds)
    {
        ThrowIfDisposed();
        // Translate in place so the bounds' bottom-left lands on the target (the core
        // keeps scale and rotation); callers pass bounds of the box's own size.
        if (CoreNative.megapdf_move_text_box(_core, objectIndex, newBounds.X, Height - newBounds.Bottom) != 0)
            throw new InvalidOperationException($"Object {objectIndex} is no longer a text object.");
    }

    public void RestoreTextRun(DetachedTextRun detached, int objectIndex)
    {
        ThrowIfDisposed();
        if (CoreNative.megapdf_restore_object(_core, detached.Handle, objectIndex) != 0)
            throw new InvalidOperationException("Could not restore the text.");
    }

    public void InsertStyledTextBox(int objectIndex, string text, string fontName,
                                    double fontSize, PdfPoint anchor, string id)
    {
        ThrowIfDisposed();
        if (!StandardTextBoxFonts.IsSupported(fontName))
            throw new ArgumentOutOfRangeException(nameof(fontName), fontName,
                "Text boxes are limited to the three standard faces (#43).");
        // The id-preserving restyle (#45), in the core: insert at the index, then
        // normalise onto the bounds anchor so a 12pt → 18pt change grows upward.
        if (CoreNative.megapdf_restyle_text_box(_core, objectIndex, text, fontName, fontSize,
                anchor.X, Height - anchor.Y, id) != 0)
            throw new InvalidOperationException("The restyled text box could not be placed.");
    }

    public void InsertTextRun(int objectIndex, string text, string fontName, double fontSize, PdfRect bounds)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var font = PdfiumNative.FPDFText_LoadStandardFont(_document, MapToStandardFont(fontName));
            if (font == IntPtr.Zero)
                throw new InvalidOperationException("No substitute font could be loaded.");
            var obj = PdfiumNative.FPDFPageObj_CreateTextObj(_document, font, (float)fontSize);
            try
            {
                if (obj == IntPtr.Zero || PdfiumNative.FPDFText_SetText(obj, text) == 0)
                    throw new InvalidOperationException("Could not recreate the text.");
                var matrix = new PdfiumNative.FS_MATRIX
                {
                    A = 1, B = 0, C = 0, D = 1,
                    E = (float)UserX(bounds.X),
                    F = (float)UserY(bounds.Bottom),
                };
                PdfiumNative.FPDFPageObj_SetMatrix(obj, ref matrix);
                if (PdfiumNative.FPDFPage_InsertObjectAtIndex(_handle, obj, (nuint)objectIndex) == 0)
                    throw new InvalidOperationException("Could not insert the recreated text.");
                obj = IntPtr.Zero;
                GenerateContent();
            }
            finally
            {
                if (obj != IntPtr.Zero)
                    PdfiumNative.FPDFPageObj_Destroy(obj);
                PdfiumNative.FPDFFont_Close(font);
            }
        }
    }

    /// <summary>True when the object's font is a subset and the new text needs glyphs the document never used.</summary>
    private bool NeedsFontSubstitution(IntPtr obj, string newText)
    {
        var font = PdfiumNative.FPDFTextObj_GetFont(obj);
        if (font == IntPtr.Zero)
            return false;

        var baseName = ReadFontName(font, useBaseName: true);
        if (!IsSubsetFontName(baseName))
            return false;

        // Approximate the subset's glyph coverage by every character the page draws
        // with this same font.
        var coverage = new HashSet<char>();
        var textPage = PdfiumNative.FPDFText_LoadPage(_handle);
        try
        {
            var count = PdfiumNative.FPDFPage_CountObjects(_handle);
            for (var i = 0; i < count; i++)
            {
                var other = PdfiumNative.FPDFPage_GetObject(_handle, i);
                if (other == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(other) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                    continue;
                var otherFont = PdfiumNative.FPDFTextObj_GetFont(other);
                if (otherFont == IntPtr.Zero || ReadFontName(otherFont, useBaseName: true) != baseName)
                    continue;
                foreach (var c in ReadTextObjectText(other, textPage))
                    coverage.Add(c);
            }
        }
        finally
        {
            if (textPage != IntPtr.Zero)
                PdfiumNative.FPDFText_ClosePage(textPage);
        }

        return newText.Any(c => !coverage.Contains(c));
    }

    internal static bool IsSubsetFontName(string baseName) =>
        baseName.Length > 7 && baseName[6] == '+' && baseName.Take(6).All(char.IsUpper);

    /// <summary>Maps an original font name to the closest standard-14 face (SDD §3.1 tier 2).</summary>
    internal static string MapToStandardFont(string originalName)
    {
        var name = originalName.ToLowerInvariant();
        var bold = name.Contains("bold");
        var italic = name.Contains("italic") || name.Contains("oblique");

        if (name.Contains("courier") || name.Contains("mono"))
            return (bold, italic) switch
            {
                (true, true) => "Courier-BoldOblique",
                (true, false) => "Courier-Bold",
                (false, true) => "Courier-Oblique",
                _ => "Courier",
            };

        if (name.Contains("times") || (name.Contains("serif") && !name.Contains("sans")))
            return (bold, italic) switch
            {
                (true, true) => "Times-BoldItalic",
                (true, false) => "Times-Bold",
                (false, true) => "Times-Italic",
                _ => "Times-Roman",
            };

        return (bold, italic) switch
        {
            (true, true) => "Helvetica-BoldOblique",
            (true, false) => "Helvetica-Bold",
            (false, true) => "Helvetica-Oblique",
            _ => "Helvetica",
        };
    }

    /// <summary>Test hook: runs the tier-2 substitution path unconditionally.</summary>
    internal void ForceSubstituteForTest(PdfTextRun run, string newText)
    {
        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, run.ObjectIndex);
            SubstituteTextObject(obj, run.ObjectIndex, newText);
            GenerateContent();
        }
    }

    /// <summary>Replaces the text object with one using a standard font, preserving index, position, size, and color.</summary>
    internal void SubstituteTextObject(IntPtr oldObj, int objectIndex, string newText)
    {
        PdfiumNative.FPDFTextObj_GetFontSize(oldObj, out var fontSize);
        var oldFont = PdfiumNative.FPDFTextObj_GetFont(oldObj);
        var originalName = oldFont != IntPtr.Zero ? ReadFontName(oldFont, useBaseName: false) : "";

        var standardFont = PdfiumNative.FPDFText_LoadStandardFont(_document, MapToStandardFont(originalName));
        if (standardFont == IntPtr.Zero)
            throw new TextEditException(TextEditFailure.NoUsableFont, "No substitute font could be loaded.");

        var newObj = PdfiumNative.FPDFPageObj_CreateTextObj(_document, standardFont, fontSize);
        try
        {
            if (newObj == IntPtr.Zero)
                throw new TextEditException(TextEditFailure.NoUsableFont, "Could not create replacement text.");
            if (PdfiumNative.FPDFText_SetText(newObj, newText) == 0)
                throw new TextEditException(TextEditFailure.NoUsableFont,
                    "The substitute font could not render the new text.");

            if (PdfiumNative.FPDFPageObj_GetMatrix(oldObj, out var matrix) != 0)
                PdfiumNative.FPDFPageObj_SetMatrix(newObj, ref matrix);
            if (PdfiumNative.FPDFPageObj_GetFillColor(oldObj, out var r, out var g, out var b, out var a) != 0)
                PdfiumNative.FPDFPageObj_SetFillColor(newObj, r, g, b, a);

            // Read the box's identity off the OLD object while it still exists —
            // it is destroyed a few lines below, and the mark has to be rebuilt on
            // the replacement from these values (#45).
            var wasTextBox = HasMark(oldObj, TextBoxMarkName);
            var boxId = wasTextBox ? ReadTextBoxId(oldObj) : null;
            var boxFont = wasTextBox ? ReadTextBoxFont(oldObj) : null;

            if (PdfiumNative.FPDFPage_RemoveObject(_handle, oldObj) == 0)
                throw new InvalidOperationException("Could not remove the original text object.");
            PdfiumNative.FPDFPageObj_Destroy(oldObj);

            if (PdfiumNative.FPDFPage_InsertObjectAtIndex(_handle, newObj, (nuint)objectIndex) == 0)
                throw new InvalidOperationException("Could not insert the replacement text object.");
            newObj = IntPtr.Zero; // ownership transferred to the page

            // Re-tag AFTER insertion, off the object the page now owns — the same
            // order AppendTextBox uses, and the order the params actually stick in.
            //
            // Re-adding the mark alone is not enough, which is what #45 was: the
            // replacement read as a text box but carried no id, so SDD §6.2
            // contract 4's handle was gone and both phones refused to select it,
            // reporting it as written by an older version. It had been silently
            // downgraded by a desktop edit. The face went the same way, so the box
            // also reverted to reading as Helvetica.
            if (wasTextBox)
            {
                var inserted = PdfiumNative.FPDFPage_GetObject(_handle, objectIndex);
                if (inserted != IntPtr.Zero)
                {
                    var mark = PdfiumNative.FPDFPageObj_AddMark(inserted, TextBoxMarkName);
                    if (mark != IntPtr.Zero)
                    {
                        // A box written before the id param existed has none to carry;
                        // it stays untagged rather than gaining a fabricated identity,
                        // because a new id would not match what any phone recorded.
                        if (boxId is not null)
                            PdfiumNative.FPDFPageObjMark_SetStringParam(
                                _document, inserted, mark, TextBoxIdKey, boxId);
                        if (boxFont is not null)
                            PdfiumNative.FPDFPageObjMark_SetStringParam(
                                _document, inserted, mark, TextBoxFontKey, boxFont);
                    }
                }
            }
        }
        finally
        {
            if (newObj != IntPtr.Zero)
                PdfiumNative.FPDFPageObj_Destroy(newObj);
            PdfiumNative.FPDFFont_Close(standardFont);
        }
    }

    private static string ReadTextObjectText(IntPtr obj, IntPtr textPage)
    {
        // Despite the header saying FPDF_WCHARs, the returned length is in BYTES
        // (including the UTF-16 NUL terminator) — verified against pdfium 152.
        var lengthInBytes = PdfiumNative.FPDFTextObj_GetText(obj, textPage, null, 0);
        if (lengthInBytes <= 2)
            return "";
        var buffer = new byte[lengthInBytes];
        PdfiumNative.FPDFTextObj_GetText(obj, textPage, buffer, lengthInBytes);
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)lengthInBytes - 2);
    }

    private static string ReadFontName(IntPtr font, bool useBaseName)
    {
        var lengthInBytes = useBaseName
            ? PdfiumNative.FPDFFont_GetBaseFontName(font, null, 0)
            : PdfiumNative.FPDFFont_GetFamilyName(font, null, 0);
        if (lengthInBytes <= 1)
            return "";
        var buffer = new byte[lengthInBytes];
        if (useBaseName)
            PdfiumNative.FPDFFont_GetBaseFontName(font, buffer, lengthInBytes);
        else
            PdfiumNative.FPDFFont_GetFamilyName(font, buffer, lengthInBytes);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)lengthInBytes - 1);
    }
    public void SetFormFieldValue(PdfFormField field, string value)
    {
        ThrowIfDisposed();
        // Click to focus, select all, replace, release — in the core (#107).
        var (x, y) = FieldCentreInCropSpace(field);
        CoreNative.megapdf_form_set_text(_core, x, y, value);
    }

    public void ToggleCheckbox(PdfFormField field)
    {
        ThrowIfDisposed();
        // A simulated click is the same path the Chrome PDF viewer uses:
        // PDFium updates /V, /AS, and radio-group siblings consistently.
        var (x, y) = FieldCentreInCropSpace(field);
        CoreNative.megapdf_form_click(_core, x, y);
    }

    /// <summary>The field's centre in the core's crop space (bottom-left origin).</summary>
    private (double X, double Y) FieldCentreInCropSpace(PdfFormField field)
    {
        var centre = field.Bounds.Center;
        return (centre.X, Height - centre.Y);
    }

    public string AddImageStamp(ReadOnlyMemory<byte> bgra, int pixelWidth, int pixelHeight, PdfRect bounds, string? stampId = null)
    {
        ThrowIfDisposed();
        if (bgra.Length != pixelWidth * pixelHeight * 4)
            throw new ArgumentException("BGRA buffer size must be width*height*4.", nameof(bgra));

        var id = stampId ?? "sig:" + Guid.NewGuid().ToString("N");
        var rect = ViewToCrop(bounds);
        int status;
        unsafe
        {
            fixed (byte* pixels = bgra.Span)
                status = CoreNative.megapdf_add_image_stamp(_core, pixels, pixelWidth, pixelHeight, ref rect, id);
        }
        if (status != 0)
            throw new InvalidOperationException("Could not place the signature.");
        return id;
    }

    public StampImage? GetStampImage(string annotationId)
    {
        ThrowIfDisposed();
        var stamp = GetMegaPdfStamps().FirstOrDefault(s => s.Id == annotationId);
        if (stamp.Id is null)
            return null;
        // Native pixel size, not placement size, so repeated move cycles never lose
        // resolution — the core's rule.
        var image = CoreNative.megapdf_stamp_image_load(_core, stamp.AnnotIndex);
        if (image == IntPtr.Zero)
            return null;
        try
        {
            var width = CoreNative.megapdf_image_width(image);
            var height = CoreNative.megapdf_image_height(image);
            var pixels = new byte[(int)CoreNative.megapdf_image_pixels(image, null, 0)];
            CoreNative.megapdf_image_pixels(image, pixels, (nuint)pixels.Length);
            return new StampImage(pixels, width, height);
        }
        finally
        {
            CoreNative.megapdf_image_free(image);
        }
    }

    public void MoveStampAnnotation(string annotationId, PdfRect newBounds)
    {
        ThrowIfDisposed();
        // Extract at native resolution, remove, re-add under the same id — in the core,
        // because updating the annotation in place wipes its appearance stream.
        var rect = ViewToCrop(newBounds);
        if (CoreNative.megapdf_move_image_stamp(_core, annotationId, ref rect) != 0)
            throw new InvalidOperationException("Only image stamps (signatures) can be moved.");
    }

    public void RemoveStampAnnotation(string annotationId)
    {
        ThrowIfDisposed();
        var status = CoreNative.megapdf_remove_stamp(_core, annotationId);
        if (status == -1)
            throw new KeyNotFoundException($"No MegaPDF stamp with id {annotationId} on page {Index}.");
        if (status != 0)
            throw new InvalidOperationException("Could not remove the mark.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (PdfiumLibrary.Lock)
        {
            // FORM_OnBeforeClosePage + FPDF_ClosePage, in the core.
            CoreNative.megapdf_close_page(_core);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
