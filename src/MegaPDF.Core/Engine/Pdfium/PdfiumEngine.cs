using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Pdfium;

/// <summary>Thrown when a document cannot be opened.</summary>
public sealed class PdfLoadException(string path, uint errorCode) : Exception(MessageFor(path, errorCode))
{
    public uint ErrorCode { get; } = errorCode;

    /// <summary>True when a password is required or the supplied one was wrong.</summary>
    public bool IsPasswordError => ErrorCode == PdfiumNative.FPDF_ERR_PASSWORD;

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
/// and cloud-synced files are never locked.
/// </summary>
public sealed class PdfiumEngine : IPdfEngine
{
    public IPdfDocument Open(string filePath, string? password = null)
    {
        PdfiumLibrary.EnsureInitialized();
        var bytes = File.ReadAllBytes(filePath);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        lock (PdfiumLibrary.Lock)
        {
            var handle = PdfiumNative.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
            if (handle == IntPtr.Zero)
            {
                var error = PdfiumNative.FPDF_GetLastError();
                pin.Free();
                throw new PdfLoadException(filePath, error);
            }
            return new PdfiumDocument(handle, pin);
        }
    }

    public void Dispose()
    {
        // PDFium itself stays initialized for the process lifetime (PdfiumLibrary).
    }
}

internal sealed class PdfiumDocument : IPdfDocument
{
    private readonly IntPtr _handle;
    private readonly PdfiumFormEnvironment _forms;
    private GCHandle _pin;
    private bool _disposed;

    internal PdfiumDocument(IntPtr handle, GCHandle pin)
    {
        _handle = handle;
        _pin = pin;
        _forms = new PdfiumFormEnvironment(handle);
    }

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            lock (PdfiumLibrary.Lock)
                return PdfiumNative.FPDF_GetPageCount(_handle);
        }
    }

    public IPdfPage GetPage(int pageIndex)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var page = PdfiumNative.FPDF_LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page {pageIndex} could not be loaded.");
            return new PdfiumPage(_handle, _forms.Handle, page, pageIndex);
        }
    }

    public void Save(Stream target)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            // Commit any in-progress form-field editing before serializing.
            PdfiumNative.FORM_ForceToKillFocus(_forms.Handle);

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

            var ok = PdfiumNative.FPDF_SaveAsCopy(_handle, ref fileWrite, PdfiumNative.SAVE_DEFAULT);
            GC.KeepAlive(callback);

            if (writeError is not null)
                throw new IOException("Writing the PDF failed.", writeError);
            if (ok == 0)
                throw new IOException("PDFium could not serialize the document.");
        }
    }

    public void FlattenAllPages()
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            // Commit any in-progress form editing, then bake every page.
            PdfiumNative.FORM_ForceToKillFocus(_forms.Handle);
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
            _forms.Dispose();
            PdfiumNative.FPDF_CloseDocument(_handle);
        }
        _pin.Free();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class PdfiumPage : IPdfPage
{
    private readonly IntPtr _document;
    private readonly IntPtr _forms;
    private readonly IntPtr _handle;
    private bool _disposed;

    internal PdfiumPage(IntPtr document, IntPtr forms, IntPtr handle, int index)
    {
        _document = document;
        _forms = forms;
        _handle = handle;
        Index = index;
        // Caller (PdfiumDocument.GetPage) holds the lock; Monitor is reentrant.
        lock (PdfiumLibrary.Lock)
        {
            PdfiumNative.FORM_OnAfterLoadPage(handle, forms);
            Width = PdfiumNative.FPDF_GetPageWidthF(handle);
            Height = PdfiumNative.FPDF_GetPageHeightF(handle);
        }
    }

    public int Index { get; }
    public double Width { get; }
    public double Height { get; }

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
        var runs = GetTextRuns();
        var lines = new List<PdfTextLine>();
        var used = new bool[runs.Count];

        for (var i = 0; i < runs.Count; i++)
        {
            if (used[i])
                continue;

            // Gather everything sharing this run's baseline (vertical-center tolerance).
            var members = new List<PdfTextRun> { runs[i] };
            used[i] = true;
            for (var j = i + 1; j < runs.Count; j++)
            {
                if (used[j])
                    continue;
                var a = runs[i].Bounds;
                var b = runs[j].Bounds;
                var tolerance = Math.Max(a.Height, b.Height) * 0.5;
                if (Math.Abs(a.Center.Y - b.Center.Y) <= tolerance)
                {
                    members.Add(runs[j]);
                    used[j] = true;
                }
            }

            // Left-to-right, then split where a gap is too wide to be the same line
            // (columns, page-number gutters).
            members.Sort((x, y) => x.Bounds.X.CompareTo(y.Bounds.X));
            var current = new List<PdfTextRun> { members[0] };
            for (var k = 1; k < members.Count; k++)
            {
                var gap = members[k].Bounds.X - current[^1].Bounds.Right;
                if (gap > Math.Max(current[^1].FontSize, members[k].FontSize) * 2)
                {
                    lines.Add(MakeLine(current));
                    current = [];
                }
                current.Add(members[k]);
            }
            lines.Add(MakeLine(current));
        }

        lines.Sort((x, y) => x.Bounds.Y.CompareTo(y.Bounds.Y));
        return lines;
    }

    private static PdfTextLine MakeLine(List<PdfTextRun> runs)
    {
        var text = string.Concat(runs.Select(r => r.Text));
        var x = runs.Min(r => r.Bounds.X);
        var y = runs.Min(r => r.Bounds.Y);
        var right = runs.Max(r => r.Bounds.Right);
        var bottom = runs.Max(r => r.Bounds.Bottom);
        return new PdfTextLine(runs, text, new PdfRect(x, y, right - x, bottom - y), runs[0].FontName, runs[0].FontSize);
    }

    /// <summary>SDD §3.2 size window for a drawn checkbox, in points.</summary>
    private const double MinSquareSize = 6;
    private const double MaxSquareSize = 24;

    public IReadOnlyList<PdfRect> DetectCheckboxSquares()
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var squares = new List<PdfRect>();
            var count = PdfiumNative.FPDFPage_CountObjects(_handle);
            for (var i = 0; i < count; i++)
            {
                var obj = PdfiumNative.FPDFPage_GetObject(_handle, i);
                if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_PATH)
                    continue;
                if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top) == 0)
                    continue;

                double width = right - left, height = top - bottom;
                // Small, roughly square (bounds include stroke width, so allow slack).
                if (width is < MinSquareSize or > MaxSquareSize || height is < MinSquareSize or > MaxSquareSize)
                    continue;
                if (Math.Abs(width - height) > Math.Max(width, height) * 0.25)
                    continue;
                // Checkbox outlines are stroked, not filled — filled squares are
                // usually decoration (bullets, table shading), per SDD §3.2.
                if (PdfiumNative.FPDFPath_GetDrawMode(obj, out var fillMode, out var stroke) == 0
                    || stroke == 0 || fillMode != 0)
                    continue;

                squares.Add(new PdfRect(left, Height - top, width, height));
            }
            return squares;
        }
    }

    private const string StampIdKey = "MegaPDF_Id";

    public string AddCheckMarkStamp(PdfRect squareBounds, string? stampId = null, CheckMarkStyle style = CheckMarkStyle.Cross)
    {
        ThrowIfDisposed();
        var id = stampId ?? "mark:" + Guid.NewGuid().ToString("N");

        // Mark at ~80% of the square, centered (SDD §3.2), in PDF page coordinates.
        var inset = Math.Max(squareBounds.Width, squareBounds.Height) * 0.10;
        var left = (float)(squareBounds.X + inset);
        var right = (float)(squareBounds.Right - inset);
        var top = (float)(Height - squareBounds.Y - inset);
        var bottom = (float)(Height - squareBounds.Bottom + inset);

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
                    stamps.Add((id, new PdfRect(rect.Left, Height - rect.Top, rect.Right - rect.Left, rect.Top - rect.Bottom)));
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
        lock (PdfiumLibrary.Lock)
        {
            var textPage = PdfiumNative.FPDFText_LoadPage(_handle);
            try
            {
                var runs = new List<PdfTextRun>();
                var count = PdfiumNative.FPDFPage_CountObjects(_handle);
                for (var i = 0; i < count; i++)
                {
                    var obj = PdfiumNative.FPDFPage_GetObject(_handle, i);
                    if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                        continue;
                    if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top) == 0)
                        continue;

                    var text = ReadTextObjectText(obj, textPage);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    PdfiumNative.FPDFTextObj_GetFontSize(obj, out var fontSize);

                    // PDF coords are bottom-left origin; our page space is top-left (Geometry.cs).
                    var bounds = new PdfRect(left, Height - top, right - left, top - bottom);
                    runs.Add(new PdfTextRun(i, text, bounds, ReadFontFamily(obj), fontSize));
                }
                return runs;
            }
            finally
            {
                if (textPage != IntPtr.Zero)
                    PdfiumNative.FPDFText_ClosePage(textPage);
            }
        }
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
                    var bounds = new PdfRect(rect.Left, Height - rect.Top, rect.Right - rect.Left, rect.Top - rect.Bottom);

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
        var left = (float)bounds.X;
        var right = (float)bounds.Right;
        var top = (float)(Height - bounds.Y);
        var bottom = (float)(Height - bounds.Bottom);

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
                whiteouts.Add((i, new PdfRect(left, Height - top, right - left, top - bottom)));
            }
            return whiteouts;
        }
    }

    private static bool HasWhiteoutMark(IntPtr obj) => HasMark(obj, WhiteoutMarkName);

    /// <summary>True when the object carries a MegaPDF page-object mark with the given name.</summary>
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

    private const string TextBoxMarkName = "MegaPDFTextBox";

    public int AppendTextBox(string text, double fontSize, PdfPoint topLeft)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var index = PdfiumNative.FPDFPage_CountObjects(_handle);
            InsertTextRun(index, text, "Helvetica", fontSize,
                new PdfRect(topLeft.X, topLeft.Y, 0, fontSize));
            // Tag it so it reads as a movable MegaPDF text box, not ordinary body text.
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, index);
            if (obj != IntPtr.Zero)
            {
                PdfiumNative.FPDFPageObj_AddMark(obj, TextBoxMarkName);
                GenerateContent();
            }
            return index;
        }
    }

    /// <summary>MegaPDF-added text boxes on this page, as their underlying text runs.</summary>
    public IReadOnlyList<PdfTextRun> GetTextBoxes()
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
        {
            var textPage = PdfiumNative.FPDFText_LoadPage(_handle);
            try
            {
                var boxes = new List<PdfTextRun>();
                var count = PdfiumNative.FPDFPage_CountObjects(_handle);
                for (var i = 0; i < count; i++)
                {
                    var obj = PdfiumNative.FPDFPage_GetObject(_handle, i);
                    if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                        continue;
                    if (!HasMark(obj, TextBoxMarkName))
                        continue;
                    if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top) == 0)
                        continue;

                    var text = ReadTextObjectText(obj, textPage);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    PdfiumNative.FPDFTextObj_GetFontSize(obj, out var fontSize);
                    var bounds = new PdfRect(left, Height - top, right - left, top - bottom);
                    boxes.Add(new PdfTextRun(i, text, bounds, ReadFontFamily(obj), fontSize));
                }
                return boxes;
            }
            finally
            {
                if (textPage != IntPtr.Zero)
                    PdfiumNative.FPDFText_ClosePage(textPage);
            }
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
            var currentY = Height - top;
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
                    E = (float)bounds.X,
                    F = (float)(Height - bounds.Bottom),
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
            // Preserve the text-box tag so an edited text box stays movable (font
            // substitution rebuilds the object, which would otherwise drop the mark).
            if (HasMark(oldObj, TextBoxMarkName))
                PdfiumNative.FPDFPageObj_AddMark(newObj, TextBoxMarkName);

            if (PdfiumNative.FPDFPage_RemoveObject(_handle, oldObj) == 0)
                throw new InvalidOperationException("Could not remove the original text object.");
            PdfiumNative.FPDFPageObj_Destroy(oldObj);

            if (PdfiumNative.FPDFPage_InsertObjectAtIndex(_handle, newObj, (nuint)objectIndex) == 0)
                throw new InvalidOperationException("Could not insert the replacement text object.");
            newObj = IntPtr.Zero; // ownership transferred to the page
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

    private static string ReadFontFamily(IntPtr obj)
    {
        var font = PdfiumNative.FPDFTextObj_GetFont(obj);
        return font == IntPtr.Zero ? "" : ReadFontName(font, useBaseName: false);
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
        var pdfY = Height - center.Y; // back to PDF bottom-left origin
        PdfiumNative.FORM_OnLButtonDown(_forms, _handle, 0, center.X, pdfY);
        PdfiumNative.FORM_OnLButtonUp(_forms, _handle, 0, center.X, pdfY);
    }
    public string AddImageStamp(ReadOnlyMemory<byte> bgra, int pixelWidth, int pixelHeight, PdfRect bounds, string? stampId = null)
    {
        ThrowIfDisposed();
        if (bgra.Length != pixelWidth * pixelHeight * 4)
            throw new ArgumentException("BGRA buffer size must be width*height*4.", nameof(bgra));

        var id = stampId ?? "sig:" + Guid.NewGuid().ToString("N");
        var left = (float)bounds.X;
        var right = (float)bounds.Right;
        var top = (float)(Height - bounds.Y);
        var bottom = (float)(Height - bounds.Bottom);

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
            PdfiumNative.FORM_OnBeforeClosePage(_handle, _forms);
            PdfiumNative.FPDF_ClosePage(_handle);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
