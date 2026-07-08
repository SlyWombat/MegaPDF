using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class AcroFormTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-acroform-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GetFormFields_FindsTextFieldAndCheckbox()
    {
        using var doc = _engine.Open(WriteFormPdf());
        using var page = doc.GetPage(0);

        var fields = page.GetFormFields();

        Assert.Equal(2, fields.Count);
        var text = Assert.Single(fields, f => f.Kind == FormFieldKind.Text);
        Assert.Equal("FullName", text.Name);
        // Rect [100 600 300 630] → top-left origin y = 792-630 = 162.
        Assert.Equal(100, text.Bounds.X, 1);
        Assert.Equal(162, text.Bounds.Y, 1);
        Assert.Equal(200, text.Bounds.Width, 1);
        Assert.Equal(30, text.Bounds.Height, 1);

        var checkbox = Assert.Single(fields, f => f.Kind == FormFieldKind.Checkbox);
        Assert.Equal("Agree", checkbox.Name);
        Assert.False(checkbox.IsChecked);
    }

    [Fact]
    public void HitTest_RoutesToFormFields_BeforeBodyText()
    {
        using var doc = _engine.Open(WriteFormPdf());
        using var page = doc.GetPage(0);

        var textHit = page.HitTest(new PdfPoint(200, 177));   // inside FullName
        Assert.Equal(PageHitKind.FormTextField, textHit.Kind);
        Assert.Equal("FullName", textHit.Field!.Name);

        var checkHit = page.HitTest(new PdfPoint(109, 283));  // inside Agree
        Assert.Equal(PageHitKind.FormCheckbox, checkHit.Kind);

        var miss = page.HitTest(new PdfPoint(500, 400));
        Assert.Equal(PageHitKind.None, miss.Kind);
    }

    [Fact]
    public void SetFormFieldValue_PersistsThroughSaveAndReopen()
    {
        var savedPath = Path.Combine(_dir, "filled.pdf");
        using (var doc = _engine.Open(WriteFormPdf()))
        {
            using (var page = doc.GetPage(0))
            {
                var field = page.GetFormFields().Single(f => f.Kind == FormFieldKind.Text);
                page.SetFormFieldValue(field, "Pat Q. Administrator");
                Assert.Equal("Pat Q. Administrator",
                    page.GetFormFields().Single(f => f.Kind == FormFieldKind.Text).Value);
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal("Pat Q. Administrator",
            reopenedPage.GetFormFields().Single(f => f.Kind == FormFieldKind.Text).Value);
    }

    [Fact]
    public void ToggleCheckbox_Checks_Persists_AndUnchecksAgain()
    {
        var savedPath = Path.Combine(_dir, "checked.pdf");
        using (var doc = _engine.Open(WriteFormPdf()))
        {
            using (var page = doc.GetPage(0))
            {
                var box = page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox);
                page.ToggleCheckbox(box);
                Assert.True(page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox).IsChecked);
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using (var page = reopened.GetPage(0))
        {
            var box = page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox);
            Assert.True(box.IsChecked);

            page.ToggleCheckbox(box);
            Assert.False(page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox).IsChecked);
        }
    }

    [Fact]
    public void FormOperations_UndoRestoresPriorState()
    {
        using var doc = _engine.Open(WriteFormPdf());
        var stack = new UndoStack();

        PdfFormField text, box;
        using (var page = doc.GetPage(0))
        {
            text = page.GetFormFields().Single(f => f.Kind == FormFieldKind.Text);
            box = page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox);
        }

        stack.Do(new FormTextEditOperation(doc, 0, text, "First value"));
        stack.Do(new CheckboxToggleOperation(doc, 0, box));

        using (var page = doc.GetPage(0))
        {
            Assert.Equal("First value", page.GetFormFields().Single(f => f.Kind == FormFieldKind.Text).Value);
            Assert.True(page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox).IsChecked);
        }

        stack.Undo(); // un-toggle
        stack.Undo(); // restore empty text
        using (var page = doc.GetPage(0))
        {
            Assert.Equal("", page.GetFormFields().Single(f => f.Kind == FormFieldKind.Text).Value);
            Assert.False(page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox).IsChecked);
        }
    }

    private string WriteFormPdf()
    {
        var path = Path.Combine(_dir, $"form-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithForm());
        return path;
    }
}
