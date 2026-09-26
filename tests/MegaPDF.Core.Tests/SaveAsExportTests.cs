using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #386 (Windows): the Save As picker offers a Markdown file-type choice alongside PDF, and the
/// two must be handled differently once the picker returns. These cover the two things pulled
/// out of <c>DocumentViewModel.SaveAsAsync</c> so they can be tested without a real
/// <c>FileSavePicker</c>/WinUI window: which file-type choice a picked path means
/// (<see cref="SaveAsExport.KindForPath"/>), and what finishing each one is allowed to do to the
/// document's saved state (<see cref="SaveAsExport.ClearsUnsavedChanges"/>). The view model itself
/// isn't unit-testable here — <c>MegaPDF.App</c> targets net8.0-windows/WinUI and only builds on
/// Windows — so this is the "at minimum" test the issue's own instructions call for.
/// </summary>
public class SaveAsExportTests
{
    [Theory]
    [InlineData(@"C:\Users\dave\Documents\report.md", SaveAsExportKind.Markdown)]
    [InlineData(@"C:\Users\dave\Documents\report.MD", SaveAsExportKind.Markdown)]
    [InlineData(@"C:\Users\dave\Documents\report.Md", SaveAsExportKind.Markdown)]
    [InlineData(@"C:\Users\dave\Documents\report.pdf", SaveAsExportKind.Pdf)]
    [InlineData(@"C:\Users\dave\Documents\report.PDF", SaveAsExportKind.Pdf)]
    // The picker's own two FileTypeChoices are PDF and Markdown; anything else falls back to
    // the PDF path rather than silently exporting text, matching KindForPath's own doc comment.
    [InlineData(@"C:\Users\dave\Documents\report", SaveAsExportKind.Pdf)]
    [InlineData(@"C:\Users\dave\Documents\report.txt", SaveAsExportKind.Pdf)]
    public void KindForPath_ClassifiesByExtension(string path, SaveAsExportKind expected) =>
        Assert.Equal(expected, SaveAsExport.KindForPath(path));

    [Fact]
    public void KindForPath_UsesTheSameExtensionThePickerIsGivenForMarkdown() =>
        Assert.Equal(".md", SaveAsExport.MarkdownExtension);

    [Fact]
    public void ClearsUnsavedChanges_TrueOnlyForPdf()
    {
        Assert.True(SaveAsExport.ClearsUnsavedChanges(SaveAsExportKind.Pdf));

        // #386's core requirement: a Markdown export can never hold the edits a re-open would
        // need, so finishing one must never make the "Unsaved changes" close prompt go quiet
        // about a PDF that was never actually saved.
        Assert.False(SaveAsExport.ClearsUnsavedChanges(SaveAsExportKind.Markdown));
    }
}
