using System.Runtime.InteropServices;

namespace MegaPDF.Core.Engine.Pdfium;

/// <summary>Thrown when a document cannot be opened.</summary>
public sealed class PdfLoadException(string path, uint errorCode) : Exception(MessageFor(path, errorCode))
{
    public uint ErrorCode { get; } = errorCode;

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
    public IPdfDocument Open(string filePath)
    {
        PdfiumLibrary.EnsureInitialized();
        var bytes = File.ReadAllBytes(filePath);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        lock (PdfiumLibrary.Lock)
        {
            var handle = PdfiumNative.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, null);
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

        foreach (var square in DetectCheckboxSquares())
        {
            if (square.Contains(point))
                return new PageHit(PageHitKind.DrawnCheckbox, Bounds: square);
        }

        foreach (var run in GetTextRuns())
        {
            if (run.Bounds.Contains(point))
                return new PageHit(PageHitKind.TextRun, TextRun: run);
        }
        return new PageHit(PageHitKind.None);
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

    public string AddCheckMarkStamp(PdfRect squareBounds, string? stampId = null)
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

                // The ✗ mark: two strokes across the square (default style, SDD §3.2).
                var path = PdfiumNative.FPDFPageObj_CreateNewPath(left, bottom);
                PdfiumNative.FPDFPath_LineTo(path, right, top);
                PdfiumNative.FPDFPath_MoveTo(path, left, top);
                PdfiumNative.FPDFPath_LineTo(path, right, bottom);
                PdfiumNative.FPDFPageObj_SetStrokeColor(path, 0x20, 0x20, 0x20, 0xFF);
                PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(1.2, squareBounds.Width * 0.11));
                PdfiumNative.FPDFPath_SetDrawMode(path, 0, stroke: 1);

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
            if (PdfiumNative.FPDFPage_RemoveObject(_handle, obj) == 0)
                throw new InvalidOperationException("Could not remove the text.");
            GenerateContent();
            // Ownership transferred to us; kept alive for a possible undo.
            return new DetachedTextRun(obj);
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
                    // GetRenderedBitmap applies the alpha mask and always yields BGRA.
                    var bitmap = PdfiumNative.FPDFImageObj_GetRenderedBitmap(_document, _handle, obj);
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
