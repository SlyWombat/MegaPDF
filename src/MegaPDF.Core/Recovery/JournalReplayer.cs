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
                    page.AddCheckMarkStamp(new PdfRect(mark.X, mark.Y, mark.Width, mark.Height), mark.StampId);
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
