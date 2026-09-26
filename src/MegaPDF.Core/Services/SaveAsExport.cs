namespace MegaPDF.Core.Services;

/// <summary>
/// What a Save As file-type choice picks between, and what completing it means for the
/// document's own saved state (#386). A PDF choice is a normal save: the new file becomes the
/// document's path, and its unsaved-changes flag clears. A Markdown choice is a one-way text
/// export (contract 9's <c>megapdf_write_text</c>, via <see cref="Engine.IPdfDocument.WriteMarkdown"/>)
/// — the file cannot hold the edits a re-open would need, so finishing it must never make the
/// document look saved. Shared so a desktop app's Save As only has to branch on this once, and
/// so the semantics match #386's iOS implementation (PR #390: "Export as Markdown", not "Save a
/// copy") rather than being re-decided per platform.
/// </summary>
public enum SaveAsExportKind
{
    Pdf,
    Markdown,
}

public static class SaveAsExport
{
    /// <summary>The Markdown file-type choice's own extension, matching what a Save As picker
    /// should offer alongside <c>.pdf</c> (#386).</summary>
    public const string MarkdownExtension = ".md";

    /// <summary>
    /// Classifies a Save As picker's result by the picked path's extension. Anything other than
    /// <see cref="MarkdownExtension"/> (case-insensitive) is the PDF choice — the picker offers
    /// exactly those two <c>FileTypeChoices</c>, so there is no third case to fall through from.
    /// </summary>
    public static SaveAsExportKind KindForPath(string path) =>
        path.EndsWith(MarkdownExtension, StringComparison.OrdinalIgnoreCase)
            ? SaveAsExportKind.Markdown
            : SaveAsExportKind.Pdf;

    /// <summary>
    /// Whether finishing a Save As of this kind should clear the document's unsaved-changes flag
    /// and adopt the picked path as the document's own. True only for
    /// <see cref="SaveAsExportKind.Pdf"/> — see the type's own doc comment for why a Markdown
    /// export never does this.
    /// </summary>
    public static bool ClearsUnsavedChanges(SaveAsExportKind kind) => kind == SaveAsExportKind.Pdf;
}
