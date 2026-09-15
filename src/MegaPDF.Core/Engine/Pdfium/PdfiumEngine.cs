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

    /// <summary>
    /// True when the document uses a security handler PDFium cannot open: not corrupt,
    /// and not a wrong password either (ADR-004 §8).
    /// </summary>
    public bool IsSecurityError => ErrorCode == PdfiumNative.FPDF_ERR_SECURITY;

    private static string MessageFor(string path, uint code) => code switch
    {
        PdfiumNative.FPDF_ERR_FILE => $"The file could not be read: {path}",
        PdfiumNative.FPDF_ERR_FORMAT => $"The file is not a valid PDF: {path}",
        PdfiumNative.FPDF_ERR_PASSWORD => $"The PDF is password-protected: {path}",
        PdfiumNative.FPDF_ERR_SECURITY => $"The PDF uses an unsupported security handler: {path}",
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
        // The core initialises PDFium on its first open.
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

    public IPdfDocument OpenLike(IPdfDocument like, string filePath)
    {
        if (like is not PdfiumDocument source)
            throw new ArgumentException("The document was not opened by this engine.", nameof(like));
        var bytes = File.ReadAllBytes(filePath);
        lock (PdfiumLibrary.Lock)
        {
            IntPtr core;
            unsafe
            {
                fixed (byte* p = bytes)
                    core = CoreNative.megapdf_open_like(source.Core, p, (nuint)bytes.Length);
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
    private bool _disposed;

    internal PdfiumDocument(IntPtr core) => _core = core;

    /// <summary>The core handle, for opening a saved copy like this document (#132).</summary>
    internal IntPtr Core
    {
        get
        {
            ThrowIfDisposed();
            return _core;
        }
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
            return new PdfiumPage(page, pageIndex);
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
        ThroughCore(target, write => CoreNative.megapdf_save(_core, write, IntPtr.Zero));
    }

    public PdfSecurity Security
    {
        get
        {
            ThrowIfDisposed();
            CoreNative.megapdf_security_info(_core, out var s);
            return new PdfSecurity(s.encrypted != 0, s.revision, (PdfPermissions)s.permissions, s.full_access != 0);
        }
    }

    public void SaveWithSecurity(Stream target, string userPassword, string? ownerPassword, PdfPermissions permissions)
    {
        ThrowIfDisposed();
        ThroughCore(target, write => CoreNative.megapdf_save_with_security(
            _core, userPassword, ownerPassword, (uint)permissions, write, IntPtr.Zero));
    }

    public void SaveWithoutSecurity(Stream target)
    {
        ThrowIfDisposed();
        ThroughCore(target, write => CoreNative.megapdf_save_without_security(_core, write, IntPtr.Zero));
    }

    /// <summary>Runs one of the core's saves, streaming its blocks to <paramref name="target"/>.</summary>
    private static void ThroughCore(Stream target, Func<CoreNative.WriteDelegate, int> save)
    {
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
        var status = save(callback);
        GC.KeepAlive(callback);

        if (writeError is not null)
            throw new IOException("Writing the PDF failed.", writeError);
        if (status == MegapdfErrRestricted)
            throw new DocumentRestrictedException();
        if (status != 0)
            throw new IOException("PDFium could not serialize the document.");
    }

    /// <summary>MEGAPDF_ERR_RESTRICTED (#131).</summary>
    private const int MegapdfErrRestricted = -6;

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
    /// <summary>The core's page handle; the form-fill hooks were applied on load.</summary>
    private readonly IntPtr _core;
    private bool _disposed;

    internal PdfiumPage(IntPtr core, int index)
    {
        _core = core;
        Index = index;
        Width = CoreNative.megapdf_page_width(core);
        Height = CoreNative.megapdf_page_height(core);
        // pdfium reports page *content* in user space, whose origin is the
        // MediaBox — but it renders, and sizes, the CropBox. When the two differ
        // (imposed pages, trimmed scans) every coordinate handed to the UI is out
        // by the difference (#28). The core owns that origin and every contract
        // returns crop space; this class only flips bottom-left to top-left.
    }

    public int Index { get; }
    public double Width { get; }
    public double Height { get; }


    /// <summary>The core's crop space (bottom-left, crop origin) to view space (top-left).</summary>
    private PdfRect CropToView(double left, double bottom, double right, double top) =>
        new(left, Height - top, right - left, top - bottom);

    /// <summary>View space (top-left) to the core's crop space (bottom-left).</summary>
    private CoreNative.Rect ViewToCrop(PdfRect r) =>
        new() { Left = r.X, Bottom = Height - r.Bottom, Right = r.Right, Top = Height - r.Y };

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
        // The tiers (SDD §3.1) are the core's (#112): the run's own font when it can
        // carry the new text, otherwise the closest standard face at the same index.
        var outcome = ApplyTextEdit(run.ObjectIndex, newText, forceSubstitute: false, out var replaced);
        CoreNative.megapdf_discard_detached(replaced);   // nobody will undo this one
        return outcome;
    }

    public TextEditOutcome SetTextRunText(PdfTextRun run, string newText, out DetachedTextRun original)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(newText))
            throw new ArgumentException("PDFium cannot set empty text on a text object.", nameof(newText));
        var outcome = ApplyTextEdit(run.ObjectIndex, newText, forceSubstitute: false, out var replaced);
        original = Wrap(replaced);
        return outcome;
    }

    public void RestoreOriginalTextRun(DetachedTextRun original, int objectIndex)
    {
        ThrowIfDisposed();
        // The edited run is a separate object at the same index (#117). The core takes it off
        // and puts the untouched original back exactly where it was, with any hidden copy of
        // the run the edit took along (#136).
        if (CoreNative.megapdf_object_type(_core, objectIndex) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
            throw new InvalidOperationException($"No edited text at object {objectIndex} to take back.");
        RestoreDetached(original);
    }

    public TextEditOutcome SetLineText(IReadOnlyList<PdfTextRun> runs, string newText, out DetachedTextRun originals)
    {
        ThrowIfDisposed();
        if (runs.Count == 0)
            throw new ArgumentException("A line has at least one run.", nameof(runs));
        if (string.IsNullOrEmpty(newText))
            throw new ArgumentException("PDFium cannot set empty text on a text object.", nameof(newText));
        var indices = runs.Select(r => r.ObjectIndex).ToArray();
        var status = CoreNative.megapdf_set_line_text(_core, indices, (nuint)indices.Length, newText, 0, out var outcome, out var replaced);
        ThrowForEditStatus(status, indices[0]);
        originals = Wrap(replaced);
        return outcome == CoreNative.EditSubstituted ? TextEditOutcome.EditedWithSubstitutedFont : TextEditOutcome.EditedInPlace;
    }

    public DetachedTextRun DetachTextRuns(IReadOnlyList<PdfTextRun> runs)
    {
        ThrowIfDisposed();
        if (runs.Count == 0)
            throw new ArgumentException("A line has at least one run.", nameof(runs));
        foreach (var run in runs)
        {
            if (CoreNative.megapdf_object_type(_core, run.ObjectIndex) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                throw new InvalidOperationException($"Object {run.ObjectIndex} is no longer a text object.");
            // Deleting body text rewrites its stream just as editing does (#118).
            if (run.TextBoxId is null && CoreNative.megapdf_text_editable_reason(_core, run.ObjectIndex, out var verdict) == 0)
                throw new TextEditException(TextEditFailure.LayoutWouldChange,
                    "Removing this text would change how the rest of the page looks.", ToVerdict(verdict));
        }
        var indices = runs.Select(r => r.ObjectIndex).ToArray();
        // The runs and the hidden copies drawn under them leave together (#136).
        var handle = CoreNative.megapdf_detach_text_runs(_core, indices, (nuint)indices.Length);
        if (handle == IntPtr.Zero)
        {
            // The line judged together can still be refused (#128): the core says so on this thread.
            if (CoreNative.megapdf_last_layout_verdict(out var refused) == 0 && refused.editable == 0)
                throw new TextEditException(TextEditFailure.LayoutWouldChange,
                    "Removing this text would change how the rest of the page looks.", ToVerdict(refused));
            throw new InvalidOperationException("Could not remove the text.");
        }
        return Wrap(handle);
    }

    public void RestoreDetached(DetachedTextRun detached)
    {
        ThrowIfDisposed();
        if (CoreNative.megapdf_restore_detached(_core, detached.Handle) != 0)
            throw new InvalidOperationException("Could not restore the text.");
    }

    /// <summary>A core handle with the parts it holds, read while the handle is alive.</summary>
    private static DetachedTextRun Wrap(IntPtr handle)
    {
        var count = (int)CoreNative.megapdf_detached_count(handle);
        var parts = new List<DetachedPart>(count);
        for (var i = 0; i < count; i++)
        {
            if (CoreNative.megapdf_detached_get(handle, (nuint)i, out var part) == 0)
                parts.Add(new DetachedPart(part.ObjectIndex, part.CopyOf));
        }
        return new DetachedTextRun(handle, parts);
    }

    public bool IsTextEditable(int objectIndex)
    {
        ThrowIfDisposed();
        return CoreNative.megapdf_text_editable(_core, objectIndex) == 1;
    }

    public LayoutVerdict? GetLayoutVerdict(int objectIndex)
    {
        ThrowIfDisposed();
        return CoreNative.megapdf_text_editable_reason(_core, objectIndex, out var verdict) < 0 ? null : ToVerdict(verdict);
    }

    public LayoutVerdict GetPageRegenerationVerdict()
    {
        ThrowIfDisposed();
        if (CoreNative.megapdf_page_regeneration_verdict(_core, out var verdict) < 0)
            throw new InvalidOperationException("The page could not be judged.");
        return ToVerdict(verdict);
    }

    private static LayoutVerdict ToVerdict(CoreNative.megapdf_layout_verdict v) =>
        new(v.editable != 0, (LayoutCause)v.cause, (LayoutArea)v.where, v.changed_pixels, v.total_pixels, v.max_shift_pt);

    private TextEditOutcome ApplyTextEdit(int objectIndex, string newText, bool forceSubstitute, out IntPtr replaced)
    {
        var status = CoreNative.megapdf_set_text(_core, objectIndex, newText,
            forceSubstitute ? CoreNative.SetTextForceSubstitute : 0, out var outcome, out replaced);
        ThrowForEditStatus(status, objectIndex);
        return outcome == CoreNative.EditSubstituted ? TextEditOutcome.EditedWithSubstitutedFont : TextEditOutcome.EditedInPlace;
    }

    private static void ThrowForEditStatus(int status, int objectIndex)
    {
        if (status == CoreNative.ErrLayout)
        {
            // Read on this thread, straight after the refused call (#128).
            CoreNative.megapdf_last_layout_verdict(out var verdict);
            throw new TextEditException(TextEditFailure.LayoutWouldChange, CoreNative.LastErrorMessage(), ToVerdict(verdict));
        }
        if (status == CoreNative.ErrNoFont)
            throw new TextEditException(TextEditFailure.NoUsableFont, CoreNative.LastErrorMessage());
        if (status != 0)
            throw new InvalidOperationException($"Object {objectIndex} is no longer a text object.");
    }

    // One run is a line of one: the hidden copies drawn under it leave with it (#136).
    public DetachedTextRun DetachTextRun(PdfTextRun run) => DetachTextRuns([run]);

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
        return Wrap(handle);
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
        // A run taken with its hidden copies (#136) goes back as it was taken: each object at its own index.
        if (detached.Parts.Count > 1)
        {
            RestoreDetached(detached);
            return;
        }
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
        // Crash-recovery replay: bounds.Bottom is the baseline, the face the closest
        // standard one to the journalled name (#112).
        var status = CoreNative.megapdf_insert_text_run(_core, objectIndex, text, fontName, fontSize, bounds.X, Height - bounds.Bottom);
        if (status == CoreNative.ErrNoFont)
            throw new InvalidOperationException("No substitute font could be loaded.");
        if (status != 0)
            throw new InvalidOperationException("Could not insert the recreated text.");
    }

    internal static bool IsSubsetFontName(string baseName) => CoreNative.megapdf_is_subset_font_name(baseName) != 0;

    /// <summary>Maps an original font name to the closest standard-14 face (SDD §3.1 tier 2), as the core does.</summary>
    internal static string MapToStandardFont(string originalName)
    {
        var buffer = new byte[64];
        var length = (int)CoreNative.megapdf_map_to_standard_font(originalName, buffer, (nuint)buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>Test hook: runs the tier-2 substitution path unconditionally.</summary>
    internal void ForceSubstituteForTest(PdfTextRun run, string newText)
    {
        ApplyTextEdit(run.ObjectIndex, newText, forceSubstitute: true, out var replaced);
        CoreNative.megapdf_discard_detached(replaced);
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
