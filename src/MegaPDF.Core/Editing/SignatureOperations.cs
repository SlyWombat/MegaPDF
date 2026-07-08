using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Editing;

/// <summary>Reversible signature placement (SDD §3.3). Ids are re-captured across undo/redo.</summary>
public sealed class AddSignatureOperation(
    IPdfDocument document, int pageIndex, byte[] bgra, int pixelWidth, int pixelHeight, PdfRect bounds) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "place signature";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        // Reusing the id across undo/redo keeps other operations' references valid.
        CurrentId = page.AddImageStamp(bgra, pixelWidth, pixelHeight, bounds, CurrentId);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RemoveStampAnnotation(CurrentId!);
    }

    internal string? CurrentId { get; private set; }
}

/// <summary>
/// Reversible signature removal. The pixels are read back from the document on Apply,
/// so even signatures placed in an earlier session can be removed and restored.
/// </summary>
public sealed class RemoveSignatureOperation(IPdfDocument document, int pageIndex, string annotationId, PdfRect bounds) : IPageEditOperation
{
    private StampImage? _image;

    public int PageIndex { get; } = pageIndex;

    public string Description => "remove signature";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _image ??= page.GetStampImage(annotationId)
            ?? throw new InvalidOperationException("The signature image could not be read back.");
        page.RemoveStampAnnotation(annotationId);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        // Restore under the original id so earlier operations still resolve it.
        page.AddImageStamp(_image!.Bgra, _image.PixelWidth, _image.PixelHeight, bounds, annotationId);
    }
}
