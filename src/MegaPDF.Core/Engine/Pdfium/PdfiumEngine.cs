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
        lock (PdfiumLibrary.Lock)
        {
            // Commit any in-progress form-field editing before serializing.
            PdfiumNative.FORM_ForceToKillFocus(_forms);

            // Always a full rewrite, on purpose (#97). PDFium's FPDF_INCREMENTAL does
            // not track which objects changed: it copies the original file and then
            // appends every indirect object the document has loaded. The apps load
            // every page at open for the size pass, so that appendix is the whole
            // document again — measured over 4,263 corpus files it made the saved file
            // 1.97x the original at the median (3.36x worst) against 1.00x (2.51x worst)
            // for this rewrite. A real append-only update needs a writer with change
            // tracking, which PDFium does not offer.
            WriteWith(target, PdfiumNative.SAVE_DEFAULT);
        }
    }

    /// <summary>One FPDF_SaveAsCopy pass into <paramref name="target"/>.</summary>
    private void WriteWith(Stream target, uint flags)
    {
        Exception? writeError = null;

        int WriteBlock(IntPtr self, IntPtr data, uint size)
        {
            try
            {
                var buffer = new byte[size];
                Marshal.Copy(data, buffer, 0, (int)size);
                target.Write(buffer, 0, buffer.Length);
                return 1;
            }
            catch (Exception ex)
            {
                writeError = ex;
                return 0;
            }
        }

        var callback = new PdfiumNative.WriteBlockDelegate(WriteBlock);
        var fileWrite = new PdfiumNative.FPDF_FILEWRITE
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback),
        };

        var ok = PdfiumNative.FPDF_SaveAsCopy(_handle, ref fileWrite, flags);
        GC.KeepAlive(callback);

        if (writeError is not null)
            throw new IOException("Writing the PDF failed.", writeError);
        if (ok == 0)
            throw new IOException("PDFium could not serialize the document.");
    }

    public void FlattenAllPages()
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            // Commit any in-progress form editing, then bake every page.
            PdfiumNative.FORM_ForceToKillFocus(_forms);
            var pageCount = PdfiumNative.FPDF_GetPageCount(_handle);
            for (var i = 0; i < pageCount; i++)
            {
                using var page = (PdfiumPage)GetPage(i);
                page.FlattenInternal();
            }
        }
    }

    public IReadOnlyList<PdfImageInfo> GetImages()
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var images = new List<PdfImageInfo>();
            var pageCount = PdfiumNative.FPDF_GetPageCount(_handle);
            for (var p = 0; p < pageCount; p++)
            {
                using var page = (PdfiumPage)GetPage(p);
                images.AddRange(page.GetImagesInternal(p));
            }
            return images;
        }
    }

    public StampImage RenderImageAt(PdfImageInfo image, int targetWidth, int targetHeight)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            using var page = (PdfiumPage)GetPage(image.PageIndex);
            return page.RenderImageAtInternal(image.ObjectIndex, targetWidth, targetHeight);
        }
    }

    public void ReplaceImageWithJpeg(PdfImageInfo image, byte[] jpegBytes)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            using var page = (PdfiumPage)GetPage(image.PageIndex);
            page.ReplaceImageWithJpegInternal(image.ObjectIndex, jpegBytes);
        }
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

    /// <summary>View space back to PDF user space.</summary>
    private double UserX(double viewX) => viewX + _cropLeft;
    private double UserY(double viewY) => _cropTop - viewY;

    public RenderedPage Render(int pixelWidth, int pixelHeight)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var bitmap = PdfiumNative.FPDFBitmap_Create(pixelWidth, pixelHeight, alpha: 1);
            if (bitmap == IntPtr.Zero)
                throw new OutOfMemoryException($"Could not allocate a {pixelWidth}x{pixelHeight} render bitmap.");
            try
            {
                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                PdfiumNative.FPDF_RenderPageBitmap(
                    bitmap, _handle, 0, 0, pixelWidth, pixelHeight, rotate: 0,
                    PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT);
                // Draw live form-field content (values typed via the form-fill env).
                PdfiumNative.FPDF_FFLDraw(
                    _forms, bitmap, _handle, 0, 0, pixelWidth, pixelHeight, 0,
                    PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT);

                var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
                var buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[pixelWidth * pixelHeight * 4];
                for (var row = 0; row < pixelHeight; row++)
                    Marshal.Copy(buffer + row * stride, pixels, row * pixelWidth * 4, pixelWidth * 4);

                return new RenderedPage(pixelWidth, pixelHeight, pixels);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    public PageHit HitTest(PdfPoint point)
    {
        // Our own stamps sit on top of everything (clicking one removes/selects it).
        foreach (var (id, bounds) in GetMegaPdfStamps())
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

    private const string StampIdKey = "MegaPDF_Id";

    public string AddCheckMarkStamp(PdfRect squareBounds, string? stampId = null, CheckMarkStyle style = CheckMarkStyle.Cross)
    {
        ThrowIfDisposed();
        var id = stampId ?? "mark:" + Guid.NewGuid().ToString("N");

        // Mark at ~80% of the square, centered (SDD §3.2), in PDF page coordinates.
        var inset = Math.Max(squareBounds.Width, squareBounds.Height) * 0.10;
        var left = (float)UserX(squareBounds.X + inset);
        var right = (float)UserX(squareBounds.Right - inset);
        var top = (float)(UserY(squareBounds.Y + inset));
        var bottom = (float)(UserY(squareBounds.Bottom - inset));

        lock (PdfiumLibrary.Lock)
        {
            var annot = PdfiumNative.FPDFPage_CreateAnnot(_handle, PdfiumNative.FPDF_ANNOT_SUBTYPE_STAMP);
            if (annot == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the mark annotation.");
            try
            {
                var rect = new PdfiumNative.FS_RECTF { Left = left, Top = top, Right = right, Bottom = bottom };
                PdfiumNative.FPDFAnnot_SetRect(annot, ref rect);

                // Mark styles per SDD §3.2 / Appendix B #3: ✗ (default), ✓, filled ■.
                IntPtr path;
                var fill = 0;
                var stroke = 1;
                switch (style)
                {
                    case CheckMarkStyle.Check:
                    {
                        var width = right - left;
                        var height = top - bottom;
                        path = PdfiumNative.FPDFPageObj_CreateNewPath(left, (float)(bottom + height * 0.45));
                        PdfiumNative.FPDFPath_LineTo(path, (float)(left + width * 0.38), bottom);
                        PdfiumNative.FPDFPath_LineTo(path, right, top);
                        break;
                    }
                    case CheckMarkStyle.FilledSquare:
                        path = PdfiumNative.FPDFPageObj_CreateNewPath(left, bottom);
                        PdfiumNative.FPDFPath_LineTo(path, right, bottom);
                        PdfiumNative.FPDFPath_LineTo(path, right, top);
                        PdfiumNative.FPDFPath_LineTo(path, left, top);
                        PdfiumNative.FPDFPath_LineTo(path, left, bottom);
                        PdfiumNative.FPDFPageObj_SetFillColor(path, 0x20, 0x20, 0x20, 0xFF);
                        fill = 1; // alternate fill mode
                        stroke = 0;
                        break;
                    default: // Cross
                        path = PdfiumNative.FPDFPageObj_CreateNewPath(left, bottom);
                        PdfiumNative.FPDFPath_LineTo(path, right, top);
                        PdfiumNative.FPDFPath_MoveTo(path, left, top);
                        PdfiumNative.FPDFPath_LineTo(path, right, bottom);
                        break;
                }
                PdfiumNative.FPDFPageObj_SetStrokeColor(path, 0x20, 0x20, 0x20, 0xFF);
                PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(1.2, squareBounds.Width * 0.11));
                PdfiumNative.FPDFPath_SetDrawMode(path, fill, stroke);

                if (PdfiumNative.FPDFAnnot_AppendObject(annot, path) == 0)
                {
                    PdfiumNative.FPDFPageObj_Destroy(path);
                    throw new InvalidOperationException("Could not draw the mark.");
                }

                PdfiumNative.FPDFAnnot_SetStringValue(annot, StampIdKey, id);
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }
        return id;
    }

    public IReadOnlyList<StampInfo> GetStamps()
    {
        ThrowIfDisposed();
        return GetMegaPdfStamps().Select(s => new StampInfo(s.Id, s.Bounds)).ToList();
    }

    /// <summary>All MegaPDF-placed stamps on the page: (id, bounds in top-left space).</summary>
    private List<(string Id, PdfRect Bounds)> GetMegaPdfStamps()
    {
        lock (PdfiumLibrary.Lock)
        {
            var stamps = new List<(string, PdfRect)>();
            var count = PdfiumNative.FPDFPage_GetAnnotCount(_handle);
            for (var i = 0; i < count; i++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(_handle, i);
                if (annot == IntPtr.Zero)
                    continue;
                try
                {
                    var id = ReadStampId(annot);
                    if (id.Length == 0 || PdfiumNative.FPDFAnnot_GetRect(annot, out var rect) == 0)
                        continue;
                    stamps.Add((id, new PdfRect(ViewX(rect.Left), ViewY(rect.Top), rect.Right - rect.Left, rect.Top - rect.Bottom)));
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }
            return stamps;
        }
    }

    private static string ReadStampId(IntPtr annot) =>
        ReadUtf16ByteLengthString((buffer, length) => PdfiumNative.FPDFAnnot_GetStringValue(annot, StampIdKey, buffer, length));

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
        lock (PdfiumLibrary.Lock)
        {
            var fields = new List<PdfFormField>();
            var count = PdfiumNative.FPDFPage_GetAnnotCount(_handle);
            for (var i = 0; i < count; i++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(_handle, i);
                if (annot == IntPtr.Zero)
                    continue;
                try
                {
                    if (PdfiumNative.FPDFAnnot_GetSubtype(annot) != PdfiumNative.FPDF_ANNOT_SUBTYPE_WIDGET)
                        continue;

                    var kind = PdfiumNative.FPDFAnnot_GetFormFieldType(_forms, annot) switch
                    {
                        PdfiumNative.FPDF_FORMFIELD_TEXTFIELD => FormFieldKind.Text,
                        PdfiumNative.FPDF_FORMFIELD_CHECKBOX => FormFieldKind.Checkbox,
                        PdfiumNative.FPDF_FORMFIELD_RADIOBUTTON => FormFieldKind.RadioButton,
                        _ => FormFieldKind.Other,
                    };

                    if (PdfiumNative.FPDFAnnot_GetRect(annot, out var rect) == 0)
                        continue;
                    // PDF rect (bottom-left origin) → our top-left page space.
                    var bounds = new PdfRect(ViewX(rect.Left), ViewY(rect.Top), rect.Right - rect.Left, rect.Top - rect.Bottom);

                    var name = ReadUtf16ByteLengthString(
                        (buffer, length) => PdfiumNative.FPDFAnnot_GetFormFieldName(_forms, annot, buffer, length));
                    var value = ReadUtf16ByteLengthString(
                        (buffer, length) => PdfiumNative.FPDFAnnot_GetFormFieldValue(_forms, annot, buffer, length));
                    var isChecked = kind is FormFieldKind.Checkbox or FormFieldKind.RadioButton
                        && PdfiumNative.FPDFAnnot_IsChecked(_forms, annot) != 0;

                    fields.Add(new PdfFormField(name, kind, bounds, value, isChecked));
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }
            return fields;
        }
    }

    private static string ReadUtf16ByteLengthString(Func<byte[]?, uint, uint> read)
    {
        var lengthInBytes = read(null, 0);
        if (lengthInBytes <= 2)
            return "";
        var buffer = new byte[lengthInBytes];
        read(buffer, lengthInBytes);
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)lengthInBytes - 2);
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
        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, run.ObjectIndex);
            if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                throw new InvalidOperationException($"Object {run.ObjectIndex} is no longer a text object.");
            return DetachObject(obj);
        }
    }

    public DetachedTextRun DetachObjectAt(int objectIndex)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, objectIndex);
            if (obj == IntPtr.Zero)
                throw new InvalidOperationException($"No page object at index {objectIndex}.");
            return DetachObject(obj);
        }
    }

    private DetachedTextRun DetachObject(IntPtr obj)
    {
        if (PdfiumNative.FPDFPage_RemoveObject(_handle, obj) == 0)
            throw new InvalidOperationException("Could not remove the object.");
        GenerateContent();
        // Ownership transferred to us; kept alive for a possible undo.
        return new DetachedTextRun(obj);
    }

    private const string WhiteoutMarkName = "MegaPDFWhiteout";

    public int AppendWhiteout(PdfRect bounds)
    {
        ThrowIfDisposed();
        var left = (float)UserX(bounds.X);
        var right = (float)UserX(bounds.Right);
        var top = (float)UserY(bounds.Y);
        var bottom = (float)UserY(bounds.Bottom);

        lock (PdfiumLibrary.Lock)
        {
            var path = PdfiumNative.FPDFPageObj_CreateNewPath(left, bottom);
            PdfiumNative.FPDFPath_LineTo(path, right, bottom);
            PdfiumNative.FPDFPath_LineTo(path, right, top);
            PdfiumNative.FPDFPath_LineTo(path, left, top);
            PdfiumNative.FPDFPath_LineTo(path, left, bottom);
            PdfiumNative.FPDFPageObj_SetFillColor(path, 0xFF, 0xFF, 0xFF, 0xFF);
            PdfiumNative.FPDFPath_SetDrawMode(path, fillMode: 1, stroke: 0);
            PdfiumNative.FPDFPageObj_AddMark(path, WhiteoutMarkName);

            var index = PdfiumNative.FPDFPage_CountObjects(_handle);
            if (PdfiumNative.FPDFPage_InsertObjectAtIndex(_handle, path, (nuint)index) == 0)
            {
                PdfiumNative.FPDFPageObj_Destroy(path);
                throw new InvalidOperationException("Could not place the whiteout.");
            }
            GenerateContent();
            return index;
        }
    }

    public IReadOnlyList<(int ObjectIndex, PdfRect Bounds)> GetWhiteouts()
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var whiteouts = new List<(int, PdfRect)>();
            var count = PdfiumNative.FPDFPage_CountObjects(_handle);
            for (var i = 0; i < count; i++)
            {
                var obj = PdfiumNative.FPDFPage_GetObject(_handle, i);
                if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_PATH)
                    continue;
                if (!HasWhiteoutMark(obj))
                    continue;
                if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top) == 0)
                    continue;
                whiteouts.Add((i, new PdfRect(ViewX(left), ViewY(top), right - left, top - bottom)));
            }
            return whiteouts;
        }
    }

    private static bool HasWhiteoutMark(IntPtr obj) => HasMark(obj, WhiteoutMarkName);

    /// <summary>True when the object carries a MegaPDF page-object mark with the given name.</summary>
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
        lock (PdfiumLibrary.Lock)
        {
            var index = PdfiumNative.FPDFPage_CountObjects(_handle);
            InsertTextRun(index, text, fontName, fontSize,
                new PdfRect(topLeft.X, topLeft.Y, 0, fontSize));
            // Tag it so it reads as a movable MegaPDF text box, not ordinary body text.
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, index);
            if (obj != IntPtr.Zero)
            {
                var mark = PdfiumNative.FPDFPageObj_AddMark(obj, TextBoxMarkName);
                // SDD §6.2 contract 4: the id is how the mobile apps address a box,
                // since page-object indices shift. Windows still works by index
                // internally; this is written so a box created here is addressable
                // on a phone.
                if (mark != IntPtr.Zero)
                {
                    PdfiumNative.FPDFPageObjMark_SetStringParam(
                        _document, obj, mark, TextBoxIdKey, $"text:{Guid.NewGuid()}");
                    // The face the user picked, recorded rather than inferred: pdfium
                    // is free to normalise a standard font's reported name, and the
                    // cross-platform contract has to be exactly what was chosen (#43).
                    PdfiumNative.FPDFPageObjMark_SetStringParam(
                        _document, obj, mark, TextBoxFontKey, fontName);
                }
                GenerateContent();
            }
            return index;
        }
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
        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, objectIndex);
            if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                throw new InvalidOperationException($"Object {objectIndex} is no longer a text object.");
            if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var _, out var _, out var top) == 0)
                throw new InvalidOperationException("Could not read the text box bounds.");

            // Translate in place: page space is top-left, PDF space bottom-left, so a
            // downward move (larger Y) is a smaller F. Keeps scale/rotation untouched.
            var currentX = left;
            var currentY = ViewY(top);
            if (PdfiumNative.FPDFPageObj_GetMatrix(obj, out var matrix) == 0)
                throw new InvalidOperationException("Could not read the text box matrix.");
            matrix.E += (float)(newBounds.X - currentX);
            matrix.F -= (float)(newBounds.Y - currentY);
            PdfiumNative.FPDFPageObj_SetMatrix(obj, ref matrix);
            GenerateContent();
        }
    }

    public void RestoreTextRun(DetachedTextRun detached, int objectIndex)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            if (PdfiumNative.FPDFPage_InsertObjectAtIndex(_handle, detached.Handle, (nuint)objectIndex) == 0)
                throw new InvalidOperationException("Could not restore the text.");
            GenerateContent();
        }
    }

    public void InsertStyledTextBox(int objectIndex, string text, string fontName,
                                    double fontSize, PdfPoint anchor, string id)
    {
        ThrowIfDisposed();
        if (!StandardTextBoxFonts.IsSupported(fontName))
            throw new ArgumentOutOfRangeException(nameof(fontName), fontName,
                "Text boxes are limited to the three standard faces (#43).");

        // Monitor is reentrant, so the nested engine calls below are safe.
        lock (PdfiumLibrary.Lock)
        {
            // InsertTextRun reads bounds.Bottom as the baseline, the way AppendTextBox
            // uses it; the true bounds are normalised below.
            InsertTextRun(objectIndex, text, fontName, fontSize,
                new PdfRect(anchor.X, anchor.Y - fontSize, 0, fontSize));

            var obj = PdfiumNative.FPDFPage_GetObject(_handle, objectIndex);
            if (obj == IntPtr.Zero)
                throw new InvalidOperationException("The restyled text box went missing.");

            var mark = PdfiumNative.FPDFPageObj_AddMark(obj, TextBoxMarkName);
            if (mark != IntPtr.Zero)
            {
                PdfiumNative.FPDFPageObjMark_SetStringParam(_document, obj, mark, TextBoxIdKey, id);
                PdfiumNative.FPDFPageObjMark_SetStringParam(
                    _document, obj, mark, TextBoxFontKey, fontName);
            }
            GenerateContent();

            // Normalise onto the bounds anchor: InsertTextRun placed the baseline, and
            // GetTextBoxes/MoveTextBox both speak bounds. Without this a 12pt → 18pt
            // restyle drops by the extra descender depth.
            if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom,
                                                   out var right, out var top) != 0)
            {
                var height = top - bottom;
                MoveTextBox(objectIndex,
                    new PdfRect(anchor.X, anchor.Y - height, right - left, height));
            }
        }
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

    internal List<PdfImageInfo> GetImagesInternal(int pageIndex)
    {
        var images = new List<PdfImageInfo>();
        lock (PdfiumLibrary.Lock)
        {
            var count = PdfiumNative.FPDFPage_CountObjects(_handle);
            for (var i = 0; i < count; i++)
            {
                var obj = PdfiumNative.FPDFPage_GetObject(_handle, i);
                if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                    continue;
                if (PdfiumNative.FPDFImageObj_GetImagePixelSize(obj, out var pxWidth, out var pxHeight) == 0)
                    continue;
                PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top);
                var stored = (long)PdfiumNative.FPDFImageObj_GetImageDataRaw(obj, null, 0);
                images.Add(new PdfImageInfo(pageIndex, i, (int)pxWidth, (int)pxHeight,
                    right - left, top - bottom, stored));
            }
        }
        return images;
    }

    internal StampImage RenderImageAtInternal(int objectIndex, int targetWidth, int targetHeight)
    {
        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, objectIndex);
            if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                throw new InvalidOperationException($"Object {objectIndex} is not an image.");

            // Same trick as stamp extraction: render through a temporary matrix
            // sized to the target pixels, then restore the placement.
            PdfiumNative.FPDFPageObj_GetMatrix(obj, out var placement);
            var renderMatrix = new PdfiumNative.FS_MATRIX { A = targetWidth, B = 0, C = 0, D = targetHeight, E = 0, F = 0 };
            PdfiumNative.FPDFPageObj_SetMatrix(obj, ref renderMatrix);
            var bitmap = PdfiumNative.FPDFImageObj_GetRenderedBitmap(_document, _handle, obj);
            PdfiumNative.FPDFPageObj_SetMatrix(obj, ref placement);
            if (bitmap == IntPtr.Zero)
                throw new InvalidOperationException("The image could not be rendered.");
            try
            {
                var width = PdfiumNative.FPDFBitmap_GetWidth(bitmap);
                var height = PdfiumNative.FPDFBitmap_GetHeight(bitmap);
                var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
                var buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                    Marshal.Copy(buffer + row * stride, pixels, row * width * 4, width * 4);
                return new StampImage(pixels, width, height);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    internal void ReplaceImageWithJpegInternal(int objectIndex, byte[] jpegBytes)
    {
        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, objectIndex);
            if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                throw new InvalidOperationException($"Object {objectIndex} is not an image.");

            var pin = GCHandle.Alloc(jpegBytes, GCHandleType.Pinned);
            try
            {
                int GetBlock(IntPtr _, uint position, IntPtr buffer, uint size)
                {
                    if (position + size > jpegBytes.Length)
                        return 0;
                    Marshal.Copy(jpegBytes, (int)position, buffer, (int)size);
                    return 1;
                }

                var callback = new PdfiumNative.GetBlockDelegate(GetBlock);
                var access = new PdfiumNative.FPDF_FILEACCESS
                {
                    FileLen = (uint)jpegBytes.Length,
                    GetBlock = Marshal.GetFunctionPointerForDelegate(callback),
                    Param = IntPtr.Zero,
                };
                // Inline: pdfium consumes the data during the call.
                var ok = PdfiumNative.FPDFImageObj_LoadJpegFileInline([_handle], 1, obj, ref access);
                GC.KeepAlive(callback);
                if (ok == 0)
                    throw new InvalidOperationException("The compressed image could not be applied.");
                GenerateContent();
            }
            finally
            {
                pin.Free();
            }
        }
    }

    /// <summary>Bakes this page's annotations/fields into its content stream.</summary>
    internal void FlattenInternal()
    {
        lock (PdfiumLibrary.Lock)
        {
            if (PdfiumNative.FPDFPage_Flatten(_handle, PdfiumNative.FLAT_NORMALDISPLAY) == 0)
                throw new InvalidOperationException($"Flattening page {Index} failed.");
            GenerateContent();
        }
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
        lock (PdfiumLibrary.Lock)
        {
            ClickField(field);
            PdfiumNative.FORM_SelectAllText(_forms, _handle);
            PdfiumNative.FORM_ReplaceSelection(_forms, _handle, value);
            PdfiumNative.FORM_ForceToKillFocus(_forms);
        }
    }

    public void ToggleCheckbox(PdfFormField field)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            // A simulated click is the same path the Chrome PDF viewer uses:
            // PDFium updates /V, /AS, and radio-group siblings consistently.
            ClickField(field);
            PdfiumNative.FORM_ForceToKillFocus(_forms);
        }
    }

    /// <summary>Simulates a primary-button click at the field's center, in PDF user space.</summary>
    private void ClickField(PdfFormField field)
    {
        var center = field.Bounds.Center;
        // Back to PDF user space, through the crop origin on both axes.
        var pdfX = UserX(center.X);
        var pdfY = UserY(center.Y);
        PdfiumNative.FORM_OnLButtonDown(_forms, _handle, 0, pdfX, pdfY);
        PdfiumNative.FORM_OnLButtonUp(_forms, _handle, 0, pdfX, pdfY);
    }
    public string AddImageStamp(ReadOnlyMemory<byte> bgra, int pixelWidth, int pixelHeight, PdfRect bounds, string? stampId = null)
    {
        ThrowIfDisposed();
        if (bgra.Length != pixelWidth * pixelHeight * 4)
            throw new ArgumentException("BGRA buffer size must be width*height*4.", nameof(bgra));

        var id = stampId ?? "sig:" + Guid.NewGuid().ToString("N");
        var left = (float)UserX(bounds.X);
        var right = (float)UserX(bounds.Right);
        var top = (float)UserY(bounds.Y);
        var bottom = (float)UserY(bounds.Bottom);

        lock (PdfiumLibrary.Lock)
        {
            using var pixels = bgra.Pin();
            IntPtr bitmap;
            unsafe
            {
                bitmap = PdfiumNative.FPDFBitmap_CreateEx(
                    pixelWidth, pixelHeight, PdfiumNative.FPDFBitmap_BGRA, (IntPtr)pixels.Pointer, pixelWidth * 4);
            }
            if (bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Could not wrap the signature image.");

            var annot = IntPtr.Zero;
            var imageObj = IntPtr.Zero;
            try
            {
                annot = PdfiumNative.FPDFPage_CreateAnnot(_handle, PdfiumNative.FPDF_ANNOT_SUBTYPE_STAMP);
                if (annot == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the signature annotation.");

                var rect = new PdfiumNative.FS_RECTF { Left = left, Top = top, Right = right, Bottom = bottom };
                PdfiumNative.FPDFAnnot_SetRect(annot, ref rect);

                imageObj = PdfiumNative.FPDFPageObj_NewImageObj(_document);
                if (imageObj == IntPtr.Zero
                    || PdfiumNative.FPDFImageObj_SetBitmap([_handle], 1, imageObj, bitmap) == 0)
                    throw new InvalidOperationException("Could not attach the signature image.");

                // An image object is a unit square; the matrix scales/places it (PDF coords).
                var matrix = new PdfiumNative.FS_MATRIX
                {
                    A = right - left, B = 0, C = 0, D = top - bottom, E = left, F = bottom,
                };
                PdfiumNative.FPDFPageObj_SetMatrix(imageObj, ref matrix);

                if (PdfiumNative.FPDFAnnot_AppendObject(annot, imageObj) == 0)
                    throw new InvalidOperationException("Could not place the signature.");
                imageObj = IntPtr.Zero; // ownership transferred to the annotation

                PdfiumNative.FPDFAnnot_SetStringValue(annot, StampIdKey, id);
            }
            finally
            {
                if (imageObj != IntPtr.Zero)
                    PdfiumNative.FPDFPageObj_Destroy(imageObj);
                if (annot != IntPtr.Zero)
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
        return id;
    }

    public StampImage? GetStampImage(string annotationId)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var count = PdfiumNative.FPDFPage_GetAnnotCount(_handle);
            for (var i = 0; i < count; i++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(_handle, i);
                if (annot == IntPtr.Zero)
                    continue;
                try
                {
                    if (ReadStampId(annot) != annotationId)
                        continue;
                    var obj = PdfiumNative.FPDFAnnot_GetObject(annot, 0);
                    if (obj == IntPtr.Zero)
                        return null;

                    // Render at the image's NATIVE pixel size (not its placement size) so
                    // repeated remove/re-add cycles never lose resolution: temporarily set a
                    // 1pt-per-pixel matrix, render, then restore the placement matrix.
                    PdfiumNative.FPDFPageObj_GetMatrix(obj, out var placement);
                    var nativeMatrix = placement;
                    if (PdfiumNative.FPDFImageObj_GetImagePixelSize(obj, out var pxWidth, out var pxHeight) != 0
                        && pxWidth > 0 && pxHeight > 0)
                    {
                        nativeMatrix = new PdfiumNative.FS_MATRIX { A = pxWidth, B = 0, C = 0, D = pxHeight, E = 0, F = 0 };
                    }
                    PdfiumNative.FPDFPageObj_SetMatrix(obj, ref nativeMatrix);
                    var bitmap = PdfiumNative.FPDFImageObj_GetRenderedBitmap(_document, _handle, obj);
                    PdfiumNative.FPDFPageObj_SetMatrix(obj, ref placement);
                    if (bitmap == IntPtr.Zero)
                        return null;
                    try
                    {
                        var width = PdfiumNative.FPDFBitmap_GetWidth(bitmap);
                        var height = PdfiumNative.FPDFBitmap_GetHeight(bitmap);
                        var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
                        var buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
                        var pixels = new byte[width * height * 4];
                        for (var row = 0; row < height; row++)
                            Marshal.Copy(buffer + row * stride, pixels, row * width * 4, width * 4);
                        return new StampImage(pixels, width, height);
                    }
                    finally
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bitmap);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }
            return null;
        }
    }

    public void MoveStampAnnotation(string annotationId, PdfRect newBounds)
    {
        ThrowIfDisposed();
        // In-place FPDFAnnot_UpdateObject after SetRect wipes the appearance stream
        // (verified empirically), so a move is: extract native-resolution pixels,
        // remove, re-add at the new bounds under the same stable id.
        lock (PdfiumLibrary.Lock)
        {
            var image = GetStampImage(annotationId)
                ?? throw new InvalidOperationException("Only image stamps (signatures) can be moved.");
            RemoveStampAnnotation(annotationId);
            AddImageStamp(image.Bgra, image.PixelWidth, image.PixelHeight, newBounds, annotationId);
        }
    }

    public void RemoveStampAnnotation(string annotationId)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var count = PdfiumNative.FPDFPage_GetAnnotCount(_handle);
            for (var i = 0; i < count; i++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(_handle, i);
                if (annot == IntPtr.Zero)
                    continue;
                var matches = ReadStampId(annot) == annotationId;
                PdfiumNative.FPDFPage_CloseAnnot(annot);
                if (!matches)
                    continue;
                if (PdfiumNative.FPDFPage_RemoveAnnot(_handle, i) == 0)
                    throw new InvalidOperationException("Could not remove the mark.");
                return;
            }
            throw new KeyNotFoundException($"No MegaPDF stamp with id {annotationId} on page {Index}.");
        }
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
