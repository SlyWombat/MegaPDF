using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Editing;

/// <summary>An edit scoped to one page — lets the UI re-render just that page after undo/redo.</summary>
public interface IPageEditOperation : IEditOperation
{
    int PageIndex { get; }
}

/// <summary>Reversible AcroForm text-field value change (SDD §3.1 form path).</summary>
public sealed class FormTextEditOperation(IPdfDocument document, int pageIndex, PdfFormField field, string newValue) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => $"fill {field.Name}";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.SetFormFieldValue(field, newValue);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.SetFormFieldValue(field, field.Value);
    }
}

/// <summary>Reversible AcroForm checkbox/radio toggle (SDD §3.2). A toggle is its own inverse.</summary>
public sealed class CheckboxToggleOperation(IPdfDocument document, int pageIndex, PdfFormField field) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => $"toggle {field.Name}";

    public void Apply() => Toggle();
    public void Revert() => Toggle();

    private void Toggle()
    {
        using var page = document.GetPage(PageIndex);
        page.ToggleCheckbox(field);
    }
}
