using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>An edit scoped to one page — lets the UI re-render just that page after undo/redo.</summary>
public interface IPageEditOperation : IEditOperation
{
    int PageIndex { get; }

    /// <summary>
    /// The crash-recovery record for this operation (SDD §3.4). Called after Apply
    /// (forward) or after Revert (<paramref name="inverse"/> = true, i.e. undo).
    /// </summary>
    JournalEntry ToJournalEntry(bool inverse);
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

    public JournalEntry ToJournalEntry(bool inverse) =>
        new FormTextEntry(PageIndex, field.Name, inverse ? field.Value : newValue);
}

/// <summary>Reversible AcroForm checkbox/radio toggle (SDD §3.2). A toggle is its own inverse.</summary>
public sealed class CheckboxToggleOperation(IPdfDocument document, int pageIndex, PdfFormField field) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => $"toggle {field.Name}";

    public void Apply() => Toggle();
    public void Revert() => Toggle();

    public JournalEntry ToJournalEntry(bool inverse) => new CheckToggleEntry(PageIndex, field.Name);

    private void Toggle()
    {
        using var page = document.GetPage(PageIndex);
        page.ToggleCheckbox(field);
    }
}
