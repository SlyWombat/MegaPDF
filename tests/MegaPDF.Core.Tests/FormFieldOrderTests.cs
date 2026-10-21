using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>#709: Tab in an open form field steps field to field, never onto a printed label.</summary>
public class FormFieldOrderTests
{
    private static PdfFormField Field(string name, FormFieldKind kind, double y, bool readOnly = false) =>
        new(name, kind, new PdfRect(100, y, 200, 20), "", IsReadOnly: readOnly);

    private static readonly IReadOnlyList<PdfFormField> Page0 =
    [
        Field("first", FormFieldKind.Text, 70),
        Field("last", FormFieldKind.Text, 110),
        Field("locked", FormFieldKind.Text, 150, readOnly: true),
        Field("choice", FormFieldKind.Other, 190),
        Field("agree", FormFieldKind.Checkbox, 230),
        Field("plan", FormFieldKind.RadioButton, 270),
        Field("plan", FormFieldKind.RadioButton, 290),
        Field("plan", FormFieldKind.RadioButton, 310),
    ];

    private static readonly IReadOnlyList<PdfFormField> Page1 = [Field("second", FormFieldKind.Text, 70)];
    private static readonly IReadOnlyList<PdfFormField> Empty = [];

    private static IReadOnlyList<PdfFormField> On(int page) => page switch { 0 => Page0, 1 => Empty, 2 => Page1, _ => Empty };

    private static string? Step(int page, int index, bool forward, int pages = 3)
    {
        var from = page == 0 ? Page0[index] : Page1[index];
        var stop = FormFieldOrder.Step(pages, On, page, from, forward);
        return stop is { } s ? $"{s.PageIndex}:{s.Field.Name}@{s.Field.Bounds.Y}" : null;
    }

    [Fact]
    public void Forward_GoesFieldToField_SkippingReadOnlyAndUnfillableKinds()
    {
        Assert.Equal("0:last@110", Step(0, 0, forward: true));
        // "locked" is read-only and "choice" is a list the editor cannot fill: both skipped.
        Assert.Equal("0:agree@230", Step(0, 1, forward: true));
        Assert.Equal("0:plan@270", Step(0, 4, forward: true));
    }

    [Fact]
    public void ARadioGroup_IsOneStop_EnteredAtItsFirstWidget()
    {
        // Leaving the group skips its other widgets, and there is nothing after them but page 3's field.
        Assert.Equal("2:second@70", Step(0, 5, forward: true));
        Assert.Equal("2:second@70", Step(0, 7, forward: true));
        // Into the group from the page after lands on its first widget, not its last.
        Assert.Equal("0:plan@270", Step(2, 0, forward: false));
    }

    [Fact]
    public void CrossesPages_AndSkipsAPageWithNoFields()
    {
        Assert.Equal("2:second@70", Step(0, 7, forward: true));
        Assert.Equal("0:plan@270", Step(2, 0, forward: false));
    }

    [Fact]
    public void Backward_GoesToThePreviousField()
    {
        Assert.Equal("0:first@70", Step(0, 1, forward: false));
        Assert.Equal("0:last@110", Step(0, 4, forward: false));
    }

    [Fact]
    public void PastTheLastField_AndBeforeTheFirst_IsNull_WithoutWrapping()
    {
        Assert.Null(Step(2, 0, forward: true));
        Assert.Null(Step(0, 0, forward: false));
    }

    [Fact]
    public void AFieldThatNoLongerExists_StillEndsSomewhereSensible()
    {
        var gone = Field("gone", FormFieldKind.Text, 999);
        Assert.Equal("first", FormFieldOrder.Step(3, On, 0, gone, forward: true)!.Value.Field.Name);
        var stop = FormFieldOrder.Step(3, On, 0, gone, forward: false);
        Assert.Equal("plan", stop!.Value.Field.Name);
    }

    [Fact]
    public void IsTabStop_FillableKindsOnly()
    {
        Assert.True(FormFieldOrder.IsTabStop(Field("a", FormFieldKind.Text, 0)));
        Assert.True(FormFieldOrder.IsTabStop(Field("a", FormFieldKind.Checkbox, 0)));
        Assert.False(FormFieldOrder.IsTabStop(Field("a", FormFieldKind.Text, 0, readOnly: true)));
        Assert.False(FormFieldOrder.IsTabStop(Field("a", FormFieldKind.Other, 0)));
    }
}
