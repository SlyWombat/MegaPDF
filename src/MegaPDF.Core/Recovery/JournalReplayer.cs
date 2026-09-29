using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Recovery;

/// <summary>Replays a recovered journal onto a freshly opened document (SDD §3.4 restore).</summary>
public static class JournalReplayer
{
    /// <summary>Applies entries front-to-back. Returns how many were applied; entries that no
    /// longer resolve (e.g. a renamed field) are skipped rather than failing the whole restore.
    ///
    /// <paramref name="documentPath"/> is the file the document was opened from, which the undo
    /// of a page delete needs: contract 10 replays it by importing that page back out of the file
    /// on disk, because a journal cannot carry a page (#174). Without it such an entry is skipped.
    /// </summary>
    public static int Replay(IPdfDocument document, IEnumerable<JournalEntry> entries, string? documentPath = null)
    {
        var applied = 0;
        foreach (var entry in entries)
        {
            // Page operations first, before any page is loaded: these act on the document and
            // their index may be the page count itself (an insert that appends), which is not a
            // page to load (#174).
            if (ReplayPageOperation(document, entry, documentPath) is { } handled)
            {
                if (handled)
                    applied++;
                continue;
            }

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
                    // As the edit was made: one call, which takes the hidden copies too (#136).
                    page.SetLineText([Stub(lineEdit.FirstIndex), .. lineEdit.DetachIndexes.Select(Stub)], lineEdit.NewText, out _);
                    applied++;
                    break;

                case LineDeleteEntry lineDelete:
                    page.DetachTextRuns(lineDelete.DetachIndexes.Select(Stub).ToArray());
                    applied++;
                    break;

                // #173: a mark is not content and puts nothing on the page, so replaying one
                // restores where the user was, not what they had removed. A journal can
                // never replay redacted text, because it never recorded any.
                case RedactionMarkAddEntry mark:
                    page.MarkForRedaction(new PdfRect(mark.X, mark.Y, mark.Width, mark.Height));
                    applied++;
                    break;

                case RedactionMarkRemoveEntry unmark:
                {
                    var target = new PdfRect(unmark.X, unmark.Y, unmark.Width, unmark.Height);
                    var match = page.GetRedactionMarks()
                        .Where(m => Math.Abs(m.Bounds.X - target.X) < 1 && Math.Abs(m.Bounds.Y - target.Y) < 1
                                 && Math.Abs(m.Bounds.Width - target.Width) < 2
                                 && Math.Abs(m.Bounds.Height - target.Height) < 2)
                        .Select(m => (RedactionMark?)m)
                        .FirstOrDefault();
                    if (match is { } found)
                    {
                        page.RemoveRedactionMark(found.MarkId);
                        applied++;
                    }
                    break;
                }

                case RedactionMarkMoveEntry moved:
                {
                    var from = new PdfRect(moved.FromX, moved.FromY, moved.FromWidth, moved.FromHeight);
                    var match = page.GetRedactionMarks()
                        .Where(m => Math.Abs(m.Bounds.X - from.X) < 1 && Math.Abs(m.Bounds.Y - from.Y) < 1)
                        .Select(m => (RedactionMark?)m)
                        .FirstOrDefault();
                    if (match is { } found)
                    {
                        page.MoveRedactionMark(found.MarkId,
                            new PdfRect(moved.ToX, moved.ToY, moved.ToWidth, moved.ToHeight));
                        applied++;
                    }
                    break;
                }

                // #329: a clear drops every mark on the document, so it is applied to the
                // document and not to the entry's page. What is left is the rectangles the
                // entry carries, which are for an undo rather than for a replay.
                case RedactionMarkClearEntry:
                    document.ClearRedactionMarks();
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
                    if (lineRestore is { FirstIndex: >= 0, FirstText: null })
                    {
                        // The edit took hidden copies of its first run (#136): take the edited run
                        // off where it stands while the rest are away, then the whole line comes
                        // back below, each copy recreated exactly like its run.
                        var standing = lineRestore.FirstIndex - lineRestore.Restores.Count(r => r.Index < lineRestore.FirstIndex);
                        page.DetachObjectAt(standing);
                    }
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

                case TextBoxAddEntry textBox:
                    page.AppendTextBox(textBox.Text, textBox.FontSize,
                        new PdfPoint(textBox.X, textBox.Y), textBox.FontName);
                    applied++;
                    break;

                case TextBoxRestyleEntry restyle:
                    page.DetachObjectAt(restyle.ObjectIndex);
                    page.InsertStyledTextBox(restyle.ObjectIndex, restyle.Text, restyle.FontName,
                        restyle.FontSize, new PdfPoint(restyle.AnchorX, restyle.AnchorY),
                        restyle.Id);
                    applied++;
                    break;

                case MoveTextBoxEntry moveText:
                {
                    var target = new PdfRect(moveText.FromX, moveText.FromY, moveText.FromWidth, moveText.FromHeight);
                    var match = page.GetTextBoxes()
                        .Where(b => Math.Abs(b.Bounds.X - target.X) < 1 && Math.Abs(b.Bounds.Y - target.Y) < 1
                                 && Math.Abs(b.Bounds.Width - target.Width) < 2 && Math.Abs(b.Bounds.Height - target.Height) < 2)
                        .OrderByDescending(b => b.ObjectIndex)
                        .Select(b => (int?)b.ObjectIndex)
                        .FirstOrDefault();
                    if (match is { } index)
                    {
                        page.MoveTextBox(index, new PdfRect(moveText.ToX, moveText.ToY, moveText.ToWidth, moveText.ToHeight));
                        applied++;
                    }
                    break;
                }

                case MoveWhiteoutEntry moveWhiteout:
                {
                    // Same resolution as MoveTextBoxEntry, for the same reason: a move
                    // detaches and re-appends, so the object index is never stable (#3).
                    var target = new PdfRect(moveWhiteout.FromX, moveWhiteout.FromY,
                        moveWhiteout.FromWidth, moveWhiteout.FromHeight);
                    var match = page.GetWhiteouts()
                        .Where(w => Math.Abs(w.Bounds.X - target.X) < 1 && Math.Abs(w.Bounds.Y - target.Y) < 1
                                 && Math.Abs(w.Bounds.Width - target.Width) < 2 && Math.Abs(w.Bounds.Height - target.Height) < 2)
                        .OrderByDescending(w => w.ObjectIndex)
                        .Select(w => ((int Index, PdfRect Bounds)?)w)
                        .FirstOrDefault();
                    if (match is { } found)
                    {
                        page.DetachObjectAt(found.Index);
                        page.AppendWhiteout(new PdfRect(moveWhiteout.ToX, moveWhiteout.ToY,
                            moveWhiteout.ToWidth, moveWhiteout.ToHeight));
                        applied++;
                    }
                    break;
                }

                case TextBoxesAddEntry many:
                {
                    // Top to bottom, exactly as AddTextBoxesOperation's first Apply placed
                    // them (#4): each line stands one line-height below the last.
                    for (var i = 0; i < many.Lines.Length; i++)
                    {
                        var lineTop = new PdfPoint(many.X,
                            many.Y + (i * many.FontSize * AddTextBoxesOperation.LineHeightFactor));
                        page.AppendTextBox(many.Lines[i], many.FontSize, lineTop, many.FontName);
                    }
                    applied++;
                    break;
                }

                case TextBoxesDeleteEntry manyGone:
                    // Highest index first, same as AddTextBoxesOperation.Revert — recorded
                    // indexes are only valid all together and in this order.
                    foreach (var index in manyGone.ObjectIndexes.OrderByDescending(i => i))
                        page.DetachObjectAt(index);
                    applied++;
                    break;
            }
        }
        return applied;
    }

    /// <summary>
    /// The page operations (#174, contract 10). Null when <paramref name="entry"/> is not one of
    /// them; true when it was applied; false when it was skipped — a page operation the document
    /// can no longer take (the file it needs is gone, a source that needs a password, a page that
    /// is not there any more) is skipped like any other entry that no longer resolves, rather than
    /// failing a whole restore.
    /// </summary>
    private static bool? ReplayPageOperation(IPdfDocument document, JournalEntry entry, string? documentPath)
    {
        try
        {
            switch (entry)
            {
                case PagesRotateEntry rotate:
                    foreach (var page in rotate.Pages)
                        document.RotatePage(page, rotate.QuarterTurns);
                    return true;

                case PagesDeleteEntry delete:
                    // Highest first, so the next index has not moved — the order the operation
                    // itself deleted in. The removed pages are discarded: a replay rebuilds the
                    // state at crash time and has no undo stack to hold them for.
                    foreach (var page in delete.Pages.OrderDescending())
                        document.DiscardRemovedPage(document.DeletePage(page));
                    return true;

                case PagesRestoreEntry restore:
                    if (documentPath is null || !File.Exists(documentPath))
                        return false;
                    // Ascending, each page back at the index it came from, out of the file on
                    // disk. Best effort, as the entry's own doc comment says.
                    foreach (var page in restore.Pages.Order())
                        document.ImportPages(documentPath, null, [page], page);
                    return true;

                case PageMoveEntry move:
                    document.MovePage(move.From, move.To);
                    return true;

                case PageInsertBlankEntry blank:
                    document.InsertBlankPage(blank.PageIndex, blank.WidthPoints, blank.HeightPoints);
                    return true;

                case PagesImportEntry import:
                    if (!File.Exists(import.SourcePath))
                        return false;
                    document.ImportPages(import.SourcePath, null, import.Pages, import.PageIndex);
                    return true;

                default:
                    return null;
            }
        }
        catch (PageToolException)
        {
            // Restricted, out of range, a refused field hierarchy, an unreadable source: this one
            // entry cannot be replayed, and the rest of the restore still can.
            return false;
        }
    }

    /// <summary>A run known only by its object index, which is all replay records.</summary>
    private static PdfTextRun Stub(int objectIndex) => new(objectIndex, "", default, "", 0);

    private static PdfFormField? FindField(IPdfPage page, string name) =>
        page.GetFormFields().FirstOrDefault(f => f.Name == name);
}
