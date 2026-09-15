using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Editing;

/// <summary>
/// The once-per-page warning before a change the layout guard never judges (#139).
/// Whiteouts and text boxes make PDFium regenerate their page's content just as a body-text
/// edit does, and on a page its writer cannot write back faithfully that changes parts of the
/// page the person never touched. Those changes are never refused: the app asks this before
/// applying one, and when it says so, warns with Continue and Cancel. One instance per open
/// document; a page is asked about at most once per open, however many changes follow.
/// </summary>
public sealed class PageRegenerationWarnings
{
    private readonly HashSet<int> _settled = [];
    private readonly object _gate = new();

    /// <summary>
    /// Whether <paramref name="operation"/> regenerates its page's content without the text
    /// guard judging it. Body-text edits and deletes are judged (and refused) on their own;
    /// signatures, check marks and form values are annotations and leave the content alone.
    /// </summary>
    public static bool RegeneratesUnjudged(IPageEditOperation operation) => operation is
        AddWhiteoutOperation or RemoveWhiteoutOperation or
        AddTextBoxOperation or MoveTextBoxOperation or RestyleTextBoxOperation or RemoveTextBoxOperation;

    /// <summary>
    /// True when the person should be warned before <paramref name="operation"/>: it regenerates
    /// unjudged content, its page has not been settled in this document, and regenerating the page
    /// would change how it looks. A page that keeps its look is settled here. Runs the core's dry
    /// run the first time for a page, which can take seconds on a heavy page: call it off the UI thread.
    /// </summary>
    public bool ShouldWarn(IPdfDocument document, IPageEditOperation operation) =>
        RegeneratesUnjudged(operation) && ShouldWarn(document, operation.PageIndex);

    /// <inheritdoc cref="ShouldWarn(IPdfDocument, IPageEditOperation)"/>
    public bool ShouldWarn(IPdfDocument document, int pageIndex)
    {
        lock (_gate)
        {
            if (_settled.Contains(pageIndex))
                return false;
        }

        LayoutVerdict verdict;
        try
        {
            using var page = document.GetPage(pageIndex);
            verdict = page.GetPageRegenerationVerdict();
        }
        catch (InvalidOperationException)
        {
            // A page the core cannot judge is no reason to stand in the way of the change.
            return false;
        }

        if (verdict.Editable)
            Settle(pageIndex);
        return !verdict.Editable;
    }

    /// <summary>Whether the page needs no more asking: it keeps its look, or the person chose Continue.</summary>
    public bool IsSettled(int pageIndex)
    {
        lock (_gate)
            return _settled.Contains(pageIndex);
    }

    /// <summary>The person chose Continue: no more warnings for this page in this document.</summary>
    public void Settle(int pageIndex)
    {
        lock (_gate)
            _settled.Add(pageIndex);
    }

    /// <summary>Forgets every page: call when another document is opened.</summary>
    public void Reset()
    {
        lock (_gate)
            _settled.Clear();
    }
}
