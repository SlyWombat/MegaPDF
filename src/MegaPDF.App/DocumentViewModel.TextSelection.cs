using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;

namespace MegaPDF.App;

/// <summary>
/// The document's half of selecting and copying its text (#710): each page's selectable text,
/// the selection itself, and whether this document lets it be copied. The gestures, the
/// highlight and the clipboard are the view's (<c>DocumentView.TextSelection.cs</c>).
///
/// The snapshots go stale on exactly the events that already make a page's scan reading stale
/// (#676) — an edit, a page operation, a redaction, a replay — and are told at the same places.
/// A selection on a page that changed is dropped, because its carets index the old snapshot.
/// </summary>
public sealed partial class DocumentViewModel
{
    private SelectableTextCache? _selectableText;

    /// <summary>This document's page text, for selecting; null while nothing is open.</summary>
    internal SelectableTextCache? SelectableText => _selectableText;

    /// <summary>What is selected. Kept across zoom and scrolling; dropped when its pages change.</summary>
    internal TextSelectionState TextSelection { get; } = new();

    /// <summary>
    /// Copy was asked for on a document whose author asked that nothing be copied out of it
    /// (#710, #558's wording): the notice says so. Selecting still works.
    /// </summary>
    [ObservableProperty]
    private bool _isCopyWithheldNoticeOpen;

    /// <summary>Whether Copy may put this document's text on the clipboard (the copy permission).</summary>
    internal bool CanCopyText => IsDocumentOpen && Capabilities.CanCopyText;

    private void OpenSelectableText(IPdfDocument document)
    {
        CloseSelectableText();
        _selectableText = new SelectableTextCache(document);
        _selectableText.Invalidated += OnSelectableTextInvalidated;
        IsCopyWithheldNoticeOpen = false;
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

    /// <summary>Called on the UI thread, from the same places the view model's own caches are dropped.</summary>
    private void OnSelectableTextInvalidated(int? page)
    {
        if (TextSelection.Pages is not { } pages)
            return;
        if (page is null || (page >= pages.First && page <= pages.Last))
            TextSelection.Clear();
    }

    /// <summary>
    /// The selected text as the core gives it (LF line ends), read off the UI thread because a
    /// selection across many pages loads each of them. Null when nothing is selected or the
    /// document closed first.
    /// </summary>
    internal async Task<string?> ReadSelectedTextAsync()
    {
        if (_selectableText is not { } cache || TextSelection.IsEmpty)
            return null;
        var anchor = TextSelection.Anchor!.Value;
        var focus = TextSelection.Focus!.Value;
        try
        {
            return await Task.Run(() => PdfTextSelection.GetText(anchor, focus, cache.Get));
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
