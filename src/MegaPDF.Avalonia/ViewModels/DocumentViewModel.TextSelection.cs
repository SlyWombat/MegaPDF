using Avalonia.Threading;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// The document's half of selecting and copying its text (#710), the same model the Windows app
/// holds (MegaPDF.Core's <see cref="SelectableTextCache"/> and <see cref="TextSelectionState"/>):
/// each page's text, read once and dropped when the page changes, the selection, its highlight on
/// every page it covers, and whether this document lets it be copied. The gestures and the
/// clipboard are the window's (<c>MainWindow.TextSelection.cs</c>).
///
/// The snapshots go stale at the same places a page's scan reading does (#676): an edit to a page,
/// a page operation, a redaction, a replay, a flatten. A selection on a page that changed is
/// dropped, because its carets index the old snapshot.
/// </summary>
public sealed partial class DocumentViewModel
{
    private SelectableTextCache? _selectableText;

    /// <summary>This document's page text, for selecting; null while nothing is open.</summary>
    internal SelectableTextCache? SelectableText => _selectableText;

    /// <summary>What is selected. Kept across zoom and scrolling; dropped when its pages change.</summary>
    internal TextSelectionState TextSelection { get; } = new();

    private bool _textSelectionWired;

    /// <summary>Whether any text is selected.</summary>
    public bool HasTextSelection => !TextSelection.IsEmpty;

    /// <summary>Whether Copy may put this document's text on the clipboard (the copy permission).</summary>
    public bool CanCopyText => IsDocumentOpen && Capabilities.CanCopyText;

    /// <summary>Copy, as the Edit menu offers it: something selected, and a document that lets it be copied.</summary>
    public bool CanCopySelection => HasTextSelection && CanCopyText;

    private void OpenSelectableText(IPdfDocument document)
    {
        CloseSelectableText();
        WireTextSelection();
        _selectableText = new SelectableTextCache(document);
        _selectableText.Invalidated += OnSelectableTextInvalidated;
    }

    private void CloseSelectableText()
    {
        if (_selectableText is { } cache)
        {
            cache.Invalidated -= OnSelectableTextInvalidated;
            cache.Dispose();
        }
        _selectableText = null;
        TextSelection.Clear();
    }

    private void WireTextSelection()
    {
        if (_textSelectionWired)
            return;
        _textSelectionWired = true;
        TextSelection.Changed += (before, after) =>
        {
            foreach (var range in new[] { before, after })
            {
                if (range is not { } r)
                    continue;
                for (var i = r.First; i <= r.Last; i++)
                    ShowTextSelectionOn(i);
            }
            OnPropertyChanged(nameof(HasTextSelection));
            OnPropertyChanged(nameof(CanCopySelection));
            // Said once, where the person is looking, as a selection starts on a document that
            // asked not to be copied from: Copy is off, and this is why (#710, #558's wording).
            if (before is null && after is not null && IsDocumentOpen && !Capabilities.CanCopyText)
                Status = Strings.CopyWithheld;
        };
    }

    /// <summary>Called from the places the document's own caches are dropped, on the UI thread.</summary>
    private void OnSelectableTextInvalidated(int? page)
    {
        if (TextSelection.Pages is not { } pages)
            return;
        if (page is null || (page >= pages.First && page <= pages.Last))
            TextSelection.Clear();
    }

    /// <summary>A page's share of the selection, handed to its view model in page points.</summary>
    internal void ShowTextSelectionOn(int pageIndex)
    {
        if (Pages.FirstOrDefault(p => p.Index == pageIndex) is not { } page)
            return;
        if (_selectableText is not { } cache || TextSelection.Pages is not { } pages
            || pageIndex < pages.First || pageIndex > pages.Last)
        {
            page.SetTextSelection([]);
            return;
        }
        if (cache.Peek(pageIndex) is { } text)
        {
            page.SetTextSelection(TextSelection.RangeOn(pageIndex, text.Length) is { } range ? text.GetRects(range) : []);
            return;
        }
        if (!page.IsRealised)
            return; // drawn when it comes on screen: the window asks again as it realises the page
        if (Inline)
        {
            try
            {
                cache.Get(pageIndex);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            ShowTextSelectionOn(pageIndex);
            return;
        }
        _ = cache.LoadAsync(pageIndex).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(cache, _selectableText))
                ShowTextSelectionOn(pageIndex);
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// The selected text as the core gives it (LF line ends), read off the UI thread when there is
    /// a window, because a selection across many pages loads each of them. Null when nothing is
    /// selected or the document closed first.
    /// </summary>
    internal async Task<string?> ReadSelectedTextAsync()
    {
        if (_selectableText is not { } cache || TextSelection.IsEmpty)
            return null;
        var anchor = TextSelection.Anchor!.Value;
        var focus = TextSelection.Focus!.Value;
        try
        {
            return await OffUiThread(() => PdfTextSelection.GetText(anchor, focus, cache.Get));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether Copy may go ahead; when the document asked not to be copied from, the status line
    /// says so instead and nothing is copied.
    /// </summary>
    internal bool MayCopySelection()
    {
        if (TextSelection.IsEmpty)
            return false;
        if (CanCopyText)
            return true;
        Status = Strings.CopyWithheld;
        return false;
    }
}
