using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Viewing;

/// <summary>
/// The order Tab walks the form's own fields in while one is open for typing (#709).
///
/// This is not <see cref="PageReadingOrder"/>. That walks every interactive region,
/// including editable text lines, added boxes and signatures, so on a real form Tab from
/// "First name" would stop on the printed label "Last name:" before it reached the field.
/// Filling a form is type, Tab, type, field to field, which is what Acrobat and Preview do.
///
/// The order is the page's widget order (the order of its /Annots), then on to the next
/// page's first field. A page's /Tabs entry (row, column or structure order) is NOT read:
/// PDFium's public API does not expose it, and the core would need a new accessor over
/// the page dictionary to do so. A radio group is one stop (entered at its first widget),
/// and read-only fields and kinds the editor cannot fill (choice lists, signatures) are skipped.
///
/// Shared because both desktops need exactly the same answer; pure, so the order is pinned
/// by tests rather than by someone tabbing through a form on a real screen.
/// </summary>
public static class FormFieldOrder
{
    /// <summary>A form field and the page it is on.</summary>
    public readonly record struct Stop(int PageIndex, PdfFormField Field);

    /// <summary>Whether Tab may land on this field at all.</summary>
    public static bool IsTabStop(PdfFormField field) =>
        field.Kind is FormFieldKind.Text or FormFieldKind.Checkbox or FormFieldKind.RadioButton
        && !field.IsReadOnly;

    /// <summary>
    /// The next (or previous) form field after <paramref name="current"/> on
    /// <paramref name="currentPage"/>, crossing pages, or null past the last (first) field.
    /// It does not wrap: running off the end hands Tab on to the next control, the way the
    /// page's own Tab does on Windows.
    /// </summary>
    /// <param name="pageCount">Pages in the document.</param>
    /// <param name="fieldsOn">A page's widgets in widget order; asked for only when the walk reaches the page.</param>
    /// <param name="currentPage">The page of the open field.</param>
    /// <param name="current">The open field; located on its page by its bounds.</param>
    /// <param name="forward">Tab (true) or Shift+Tab (false).</param>
    public static Stop? Step(int pageCount, Func<int, IReadOnlyList<PdfFormField>> fieldsOn,
                             int currentPage, PdfFormField current, bool forward)
    {
        if (pageCount <= 0 || currentPage < 0 || currentPage >= pageCount)
            return null;

        var here = fieldsOn(currentPage);
        var at = IndexOf(here, current);
        // A field that is gone (the page changed under the editor) counts as the page's start going
        // forward and its end going back, so the walk still ends somewhere sensible.
        if (at < 0)
            at = forward ? -1 : here.Count;

        for (var pageIndex = currentPage; pageIndex >= 0 && pageIndex < pageCount; pageIndex += forward ? 1 : -1)
        {
            var fields = pageIndex == currentPage ? here : fieldsOn(pageIndex);
            var from = pageIndex == currentPage ? at : forward ? -1 : fields.Count;
            var step = forward ? 1 : -1;
            for (var i = from + step; i >= 0 && i < fields.Count; i += step)
            {
                var candidate = fields[i];
                if (!IsTabStop(candidate))
                    continue;
                // Going on from a radio, the rest of its group is the same stop.
                if (pageIndex == currentPage && SameRadioGroup(candidate, current))
                    continue;
                // Entering a radio group backwards, land on its first widget, not its last.
                if (!forward && candidate.Kind == FormFieldKind.RadioButton)
                    i = FirstOfGroup(fields, i);
                return new Stop(pageIndex, fields[i]);
            }
        }
        return null;
    }

    private static bool SameRadioGroup(PdfFormField a, PdfFormField b) =>
        a.Kind == FormFieldKind.RadioButton && b.Kind == FormFieldKind.RadioButton
        && a.Name.Length > 0 && a.Name == b.Name;

    private static int FirstOfGroup(IReadOnlyList<PdfFormField> fields, int index)
    {
        var first = index;
        while (first > 0 && SameRadioGroup(fields[first - 1], fields[index]))
            first--;
        return first;
    }

    /// <summary>Exact bounds first (the field came from this list), then any field containing its centre.</summary>
    private static int IndexOf(IReadOnlyList<PdfFormField> fields, PdfFormField field)
    {
        for (var i = 0; i < fields.Count; i++)
            if (fields[i].Bounds == field.Bounds && fields[i].Name == field.Name)
                return i;
        for (var i = 0; i < fields.Count; i++)
            if (fields[i].Bounds.Contains(field.Bounds.Center))
                return i;
        return -1;
    }
}
