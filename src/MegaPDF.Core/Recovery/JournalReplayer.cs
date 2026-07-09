using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Recovery;

/// <summary>Replays a recovered journal onto a freshly opened document (SDD §3.4 restore).</summary>
public static class JournalReplayer
{
    /// <summary>Applies entries front-to-back. Returns how many were applied; entries that no
    /// longer resolve (e.g. a renamed field) are skipped rather than failing the whole restore.</summary>
    public static int Replay(IPdfDocument document, IEnumerable<JournalEntry> entries)
    {
        var applied = 0;
        foreach (var entry in entries)
        {
            using var page = document.GetPage(entry.PageIndex);
            switch (entry)
            {
                case TextEditEntry text:
                    page.SetTextRunText(new PdfTextRun(text.ObjectIndex, "", default, "", 0), text.NewText);
                    applied++;
                    break;

                case TextDeleteEntry delete:
                    page.DetachTextRun(new PdfTextRun(delete.ObjectIndex, "", default, "", 0));
                    applied++;
                    break;

                case TextRestoreEntry restore:
                    page.InsertTextRun(restore.ObjectIndex, restore.Text, restore.FontName, restore.FontSize,
                        new PdfRect(restore.X, restore.Y, restore.Width, restore.Height));
                    applied++;
                    break;

                case LineEditEntry lineEdit:
                    page.SetTextRunText(new PdfTextRun(lineEdit.FirstIndex, "", default, "", 0), lineEdit.NewText);
                    foreach (var index in lineEdit.DetachIndexes) // recorded descending
                        page.DetachTextRun(new PdfTextRun(index, "", default, "", 0));
                    applied++;
                    break;

                case LineDeleteEntry lineDelete:
                    foreach (var index in lineDelete.DetachIndexes) // recorded descending
                        page.DetachTextRun(new PdfTextRun(index, "", default, "", 0));
                    applied++;
                    break;

                case WhiteoutAddEntry whiteout:
                    page.AppendWhiteout(new PdfRect(whiteout.X, whiteout.Y, whiteout.Width, whiteout.Height));
                    applied++;
                    break;

                case WhiteoutRemoveEntry removal:
                {
                    var target = new PdfRect(removal.X, removal.Y, removal.Width, removal.Height);
                    var match = page.GetWhiteouts()
                        .Where(w => Math.Abs(w.Bounds.X - target.X) < 1 && Math.Abs(w.Bounds.Y - target.Y) < 1
                                 && Math.Abs(w.Bounds.Width - target.Width) < 2 && Math.Abs(w.Bounds.Height - target.Height) < 2)
                        .OrderByDescending(w => w.ObjectIndex)
                        .Select(w => ((int Index, PdfRect Bounds)?)w)
                        .FirstOrDefault();
                    if (match is { } found)
                    {
                        page.DetachObjectAt(found.Index);
                        applied++;
                    }
                    break;
                }

                case LineRestoreEntry lineRestore:
                    foreach (var run in lineRestore.Restores) // recorded ascending
                        page.InsertTextRun(run.Index, run.Text, run.FontName, run.FontSize,
                            new PdfRect(run.X, run.Y, run.Width, run.Height));
                    if (lineRestore is { FirstIndex: >= 0, FirstText: not null })
                        page.SetTextRunText(new PdfTextRun(lineRestore.FirstIndex, "", default, "", 0), lineRestore.FirstText);
                    applied++;
                    break;

                case FormTextEntry formText:
                    if (FindField(page, formText.FieldName) is { } field)
                    {
                        page.SetFormFieldValue(field, formText.NewValue);
                        applied++;
                    }
                    break;

                case CheckToggleEntry toggle:
                    if (FindField(page, toggle.FieldName) is { } box)
                    {
                        page.ToggleCheckbox(box);
                        applied++;
                    }
                    break;

                case AddMarkEntry mark:
                    Enum.TryParse<CheckMarkStyle>(mark.Style, out var style);
                    page.AddCheckMarkStamp(new PdfRect(mark.X, mark.Y, mark.Width, mark.Height), mark.StampId, style);
                    applied++;
                    break;

                case RemoveStampEntry remove:
                    try
                    {
                        page.RemoveStampAnnotation(remove.StampId);
                        applied++;
                    }
                    catch (KeyNotFoundException)
                    {
                        // Stamp already gone — harmless during replay.
                    }
                    break;

                case MoveStampEntry move:
                    page.MoveStampAnnotation(move.StampId, new PdfRect(move.X, move.Y, move.Width, move.Height));
                    applied++;
                    break;

                case AddSignatureEntry sig:
                    page.AddImageStamp(
                        JournalBlob.Unpack(sig.PixelsDeflated), sig.PixelWidth, sig.PixelHeight,
                        new PdfRect(sig.X, sig.Y, sig.Width, sig.Height), sig.StampId);
                    applied++;
                    break;
            }
        }
        return applied;
    }

    private static PdfFormField? FindField(IPdfPage page, string name) =>
        page.GetFormFields().FirstOrDefault(f => f.Name == name);
}
