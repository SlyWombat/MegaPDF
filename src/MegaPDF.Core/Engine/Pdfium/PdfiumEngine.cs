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
    private GCHandle _pin;
    private bool _disposed;

    internal PdfiumDocument(IntPtr handle, GCHandle pin)
    {
        _handle = handle;
        _pin = pin;
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
            return new PdfiumPage(page, pageIndex);
        }
    }

    public void Save(Stream target)
    {
        ThrowIfDisposed();
        lock (PdfiumLibrary.Lock)
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
            PdfiumNative.FPDF_CloseDocument(_handle);
        _pin.Free();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class PdfiumPage : IPdfPage
{
    private readonly IntPtr _handle;
    private bool _disposed;

    internal PdfiumPage(IntPtr handle, int index)
    {
        _handle = handle;
        Index = index;
        // Caller (PdfiumDocument.GetPage) holds the lock; Monitor is reentrant.
        lock (PdfiumLibrary.Lock)
        {
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
        foreach (var run in GetTextRuns())
        {
            if (run.Bounds.Contains(point))
                return new PageHit(PageHitKind.TextRun, TextRun: run);
        }
        return new PageHit(PageHitKind.None);
    }

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

    public IReadOnlyList<PdfFormField> GetFormFields() => [];

    public void SetTextRunText(PdfTextRun run, string newText)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(newText))
            throw new ArgumentException("PDFium cannot set empty text on a text object.", nameof(newText));

        lock (PdfiumLibrary.Lock)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(_handle, run.ObjectIndex);
            if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumNative.FPDF_PAGEOBJ_TEXT)
                throw new InvalidOperationException($"Object {run.ObjectIndex} is no longer a text object.");

            if (PdfiumNative.FPDFText_SetText(obj, newText) == 0)
                throw new TextEditException(TextEditFailure.NoUsableFont,
                    "The document's font could not render the new text.");

            if (PdfiumNative.FPDFPage_GenerateContent(_handle) == 0)
                throw new InvalidOperationException("PDFium failed to regenerate the page content stream.");
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
        if (font == IntPtr.Zero)
            return "";
        var lengthInBytes = PdfiumNative.FPDFFont_GetFamilyName(font, null, 0);
        if (lengthInBytes <= 1)
            return "";
        var buffer = new byte[lengthInBytes];
        PdfiumNative.FPDFFont_GetFamilyName(font, buffer, lengthInBytes);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)lengthInBytes - 1);
    }
    public void SetFormFieldValue(PdfFormField field, string value) =>
        throw new NotSupportedException("Form filling arrives with the edit milestone.");
    public void ToggleCheckbox(PdfFormField field) =>
        throw new NotSupportedException("Checkbox toggling arrives with the edit milestone.");
    public string AddStampAnnotation(ReadOnlyMemory<byte> pngBytes, PdfRect bounds) =>
        throw new NotSupportedException("Stamps arrive with the signature milestone.");
    public void MoveStampAnnotation(string annotationId, PdfRect newBounds) =>
        throw new NotSupportedException("Stamps arrive with the signature milestone.");
    public void RemoveStampAnnotation(string annotationId) =>
        throw new NotSupportedException("Stamps arrive with the signature milestone.");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (PdfiumLibrary.Lock)
            PdfiumNative.FPDF_ClosePage(_handle);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
