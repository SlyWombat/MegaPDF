using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// A stored signature and its preview. You pick a signature by how it looks, so the
/// list needs the image, not just the name.
/// </summary>
public sealed record SignatureItem(SignatureEntry Entry, global::Avalonia.Media.Imaging.Bitmap? Thumbnail)
{
    public string Name => Entry.Name;
}

/// <summary>
/// The document shell: open, view, check, save.
///
/// The editing behaviour is not reimplemented here — MegaPDF.Core's reversible
/// operations (CheckboxToggleOperation, AddMarkOperation, RemoveMarkOperation) and
/// UndoStack are the same ones the WinUI app drives, which is the whole argument
/// for ADR-002 Option B. What this class adds is the platform-facing half: which
/// page to re-render, what the status line says, and when the document is dirty.
///
/// File dialogs stay in the view. Avalonia reaches them through the TopLevel's
/// IStorageProvider, so a path or a stream comes in and this stays UI-free.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly double[] ZoomStops =
        [0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    private readonly IPdfEngine _engine = new PdfiumEngine();
    private readonly UndoStack _undoStack = new();
    private readonly ISignatureLibrary _signatures = new SignatureLibrary();
    private IPdfDocument? _document;

    public ObservableCollection<PageViewModel> Pages { get; } = [];

    [ObservableProperty]
    private string? _documentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? _documentName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDocumentOpen;

    /// <summary>
    /// The edited-marker convention macOS and Windows share: the title carries the
    /// document name, and unsaved work is a bullet rather than an asterisk.
    /// </summary>
    public string WindowTitle => DocumentName is null
        ? "MegaPDF"
        : $"{(IsDirty ? "• " : "")}{DocumentName} — MegaPDF";

    [ObservableProperty]
    private string _status = "Open a PDF to get started.";

    /// <summary>
    /// Display scale of the monitor the window is on. Set by the view; feeding it into
    /// the raster is what keeps a page sharp on a retina Mac instead of upscaled.
    /// </summary>
    [ObservableProperty]
    private double _dpiScale = 1.0;

    [ObservableProperty]
    private double _zoom = 1.0;

    public bool CanUndo => _undoStack.CanUndo;
    public bool CanRedo => _undoStack.CanRedo;

    partial void OnZoomChanged(double value)
    {
        // Push zoom down so each page reports its layout size; the scroll extent has
        // to change even for pages that are not realised.
        foreach (var page in Pages)
            page.Zoom = value;
        RerenderRealisedPages();
    }

    partial void OnDpiScaleChanged(double value) => RerenderRealisedPages();

    public void Open(string path)
    {
        CloseDocument();

        IPdfDocument document;
        try
        {
            document = _engine.Open(path);
        }
        catch (PdfLoadException ex)
        {
            // Engine messages are already written for the person holding the document,
            // not the developer (SDD §2.2) — surface as-is.
            Status = ex.Message;
            return;
        }

        _document = document;
        _matches.Clear();
        MatchCount = 0;
        CurrentMatchIndex = -1;
        LoadSignatures();
        for (var i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            Pages.Add(new PageViewModel(document, i, page.Width, page.Height) { Zoom = Zoom });
        }

        DocumentPath = path;
        DocumentName = Path.GetFileName(path);
        IsDocumentOpen = true;
        IsDirty = false;
        Status = $"{DocumentName} — {document.PageCount} page{(document.PageCount == 1 ? "" : "s")}. "
                 + "Click a checkbox to tick it.";
    }

    // --- Interaction (SDD §3.2) ---

    public PageHit HitTest(int pageIndex, PdfPoint point)
    {
        if (_document is null)
            return new PageHit(PageHitKind.None);
        using var page = _document.GetPage(pageIndex);
        return page.HitTest(point);
    }

    /// <summary>
    /// Routes a click on the page. Mirrors the WinUI app's routing so a document
    /// behaves the same on both desktops: form checkboxes toggle, drawn squares take
    /// a ✗ stamp, and clicking an existing mark clears it.
    /// </summary>
    public void HandlePageClick(int pageIndex, PdfPoint point)
    {
        if (_document is null)
            return;

        // Placement mode wins over everything: the click is choosing a spot, not
        // asking what is under it.
        if (PendingSignature is { } pending)
        {
            PlacePendingSignature(pageIndex, point, pending);
            return;
        }

        var hit = HitTest(pageIndex, point);
        switch (hit.Kind)
        {
            case PageHitKind.FormCheckbox:
                Apply(new CheckboxToggleOperation(_document, pageIndex, hit.Field!), "Checkbox toggled.");
                break;

            case PageHitKind.DrawnCheckbox:
                Apply(new AddMarkOperation(_document, pageIndex, hit.Bounds!.Value), "Checked.");
                break;

            case PageHitKind.StampAnnotation when !hit.AnnotationId!.StartsWith("sig:", StringComparison.Ordinal):
                // Check marks are click-to-toggle: clicking one clears it (SDD §3.2).
                Apply(new RemoveMarkOperation(_document, pageIndex, hit.AnnotationId, hit.Bounds!.Value),
                      "Unchecked.");
                break;

            case PageHitKind.StampAnnotation:
                // A placed signature. Move/resize chrome is a later increment; until
                // then clicking one removes it, which at least makes a misplaced
                // signature recoverable without reaching for undo.
                Apply(new RemoveSignatureOperation(_document, pageIndex, hit.AnnotationId!, hit.Bounds!.Value),
                      "Signature removed.");
                break;

            default:
                // Text boxes and whiteout are later increments. Saying nothing is
                // better than pretending something happened.
                break;
        }
    }

    private void Apply(IPageEditOperation operation, string doneMessage)
    {
        _undoStack.Do(operation);
        AfterEdit(operation.PageIndex, doneMessage);
    }

    private void AfterEdit(int pageIndex, string message)
    {
        IsDirty = true;
        Status = message;
        RerenderPage(pageIndex);
        RaiseUndoRedo();
    }

    [RelayCommand]
    private void Undo()
    {
        if (!_undoStack.CanUndo)
            return;
        var op = _undoStack.PeekUndo as IPageEditOperation;
        _undoStack.Undo();
        AfterEdit(op?.PageIndex ?? 0, "Undone.");
    }

    [RelayCommand]
    private void Redo()
    {
        if (!_undoStack.CanRedo)
            return;
        var op = _undoStack.PeekRedo as IPageEditOperation;
        _undoStack.Redo();
        AfterEdit(op?.PageIndex ?? 0, "Redone.");
    }

    private void RaiseUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // --- Find in document (SDD §3.6 / F6) ---

    /// <summary>One hit: which page it is on and the rectangles covering it.</summary>
    private sealed record Match(int PageIndex, IReadOnlyList<PdfRect> Rects);

    private readonly List<Match> _matches = [];

    [ObservableProperty]
    private string _searchTerm = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private int _currentMatchIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private int _matchCount;

    [ObservableProperty]
    private bool _isFindOpen;

    /// <summary>
    /// What the find bar reads. Deliberately words rather than a bare "0/0": an empty
    /// box is not the same as a term that genuinely is not in the document, and the
    /// person filling in a form should not have to infer which they are looking at.
    /// </summary>
    public string MatchSummary => string.IsNullOrEmpty(SearchTerm)
        ? ""
        : MatchCount == 0
            ? "Not found"
            : $"{CurrentMatchIndex + 1} of {MatchCount}";

    /// <summary>Raised when the view should bring a page rectangle into view.</summary>
    public event Action<int, PdfRect>? ScrollToRequested;

    public void Search(string term)
    {
        SearchTerm = term;
        _matches.Clear();
        CurrentMatchIndex = -1;

        if (_document is not null && !string.IsNullOrWhiteSpace(term))
        {
            for (var i = 0; i < Pages.Count; i++)
            {
                using var page = _document.GetPage(i);
                foreach (var hit in page.FindText(term))
                    _matches.Add(new Match(i, hit.Rects));
            }
        }

        MatchCount = _matches.Count;
        ApplyHighlights();

        if (MatchCount > 0)
            GoToMatch(0);
    }

    [RelayCommand]
    private void FindNext()
    {
        if (MatchCount == 0)
            return;
        GoToMatch((CurrentMatchIndex + 1) % MatchCount);
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (MatchCount == 0)
            return;
        GoToMatch((CurrentMatchIndex - 1 + MatchCount) % MatchCount);
    }

    public void CloseFind()
    {
        IsFindOpen = false;
        SearchTerm = "";
        _matches.Clear();
        MatchCount = 0;
        CurrentMatchIndex = -1;
        ApplyHighlights();
    }

    private void GoToMatch(int index)
    {
        CurrentMatchIndex = index;
        ApplyHighlights();

        var match = _matches[index];
        if (match.Rects.Count > 0)
            ScrollToRequested?.Invoke(match.PageIndex, match.Rects[0]);
    }

    private void ApplyHighlights()
    {
        foreach (var page in Pages)
        {
            var rects = new List<PdfRect>();
            var current = -1;
            for (var i = 0; i < _matches.Count; i++)
            {
                if (_matches[i].PageIndex != page.Index)
                    continue;
                if (i == CurrentMatchIndex)
                    current = rects.Count;
                rects.AddRange(_matches[i].Rects);
            }
            page.SetMatches(rects, current);
        }
    }

    // --- Signatures (SDD §3.3) ---

    /// <summary>The stored signature library, newest first.</summary>
    public ObservableCollection<SignatureItem> Signatures { get; } = [];

    /// <summary>
    /// The signature awaiting a click on the page. While this is set the next page
    /// click places it rather than routing to a checkbox — the same modal placement
    /// the WinUI app uses, and the reason HandlePageClick checks it first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlacingSignature))]
    private SignatureEntry? _pendingSignature;

    public bool IsPlacingSignature => PendingSignature is not null;

    public void LoadSignatures()
    {
        foreach (var existing in Signatures)
            existing.Thumbnail?.Dispose();
        Signatures.Clear();

        foreach (var entry in _signatures.All.OrderByDescending(e => e.CreatedUtc))
            Signatures.Add(new SignatureItem(entry, Rendering.SignatureImages.LoadThumbnail(entry.PngPath)));

        HasSignatures = Signatures.Count > 0;
    }

    [ObservableProperty]
    private bool _hasSignatures;

    public SignatureEntry AddSignature(string name, byte[] png)
    {
        var entry = _signatures.Add(name, png);
        LoadSignatures();
        Status = $"Saved the signature \"{entry.Name}\". Click where it should go.";
        return entry;
    }

    public void RemoveSignature(Guid id)
    {
        _signatures.Remove(id);
        LoadSignatures();
        Status = "Signature deleted.";
    }

    public void BeginPlacing(SignatureItem item) => BeginPlacing(item.Entry);

    public void BeginPlacing(SignatureEntry entry)
    {
        PendingSignature = entry;
        Status = $"Click where \"{entry.Name}\" should go.";
    }

    public void CancelPlacing()
    {
        if (PendingSignature is null)
            return;
        PendingSignature = null;
        Status = "Placement cancelled.";
    }

    /// <summary>
    /// Places the pending signature centred on the click, 180pt wide with the aspect
    /// ratio preserved and clamped inside the page — the same geometry the WinUI app
    /// uses, so a document signed on one desktop looks the same on the other.
    /// </summary>
    private void PlacePendingSignature(int pageIndex, PdfPoint point, SignatureEntry pending)
    {
        PendingSignature = null;

        SignatureBitmap image;
        try
        {
            image = Rendering.SignatureImages.LoadBgra(pending.PngPath);
        }
        catch (Exception ex)
        {
            Status = $"Could not read that signature: {ex.Message}";
            return;
        }

        PlaceSignature(pageIndex, point, image, $"Placed \"{pending.Name}\".");
    }

    /// <summary>
    /// The placement geometry, separated from loading a PNG so it can be exercised
    /// without a file — and without an initialised graphics stack — by --self-test.
    /// </summary>
    internal void PlaceSignature(int pageIndex, PdfPoint point, SignatureBitmap image, string doneMessage)
    {
        if (_document is null)
            return;

        const double defaultWidthPoints = 180;
        var width = defaultWidthPoints;
        var height = width * image.Height / image.Width;

        var page = Pages[pageIndex];
        var x = Math.Clamp(point.X - (width / 2), 0, Math.Max(0, page.PointWidth - width));
        var y = Math.Clamp(point.Y - (height / 2), 0, Math.Max(0, page.PointHeight - height));

        Apply(new AddSignatureOperation(
                  _document, pageIndex, image.Bgra, image.Width, image.Height,
                  new PdfRect(x, y, width, height)),
              doneMessage);
    }

    // --- Saving (SDD §3.4) ---

    /// <summary>
    /// Writes the document to a stream the host already holds open.
    ///
    /// Under the macOS App Sandbox this is the only way to save: the app is granted
    /// the file the user picked, not its folder, so AtomicFileWriter's write-a-sibling
    /// -and-swap protocol is denied outright. <see cref="StagedStreamWriter"/> keeps
    /// as much of that protocol's safety as the sandbox allows — the serialise happens
    /// against a temp file, and the user's bytes are only touched once it has
    /// succeeded.
    /// </summary>
    public void SaveTo(Stream destination)
    {
        if (_document is null)
            return;

        StagedStreamWriter.Write(destination, stream => _document.Save(stream));
        IsDirty = false;
        Status = $"Saved {DocumentName}.";
    }

    /// <summary>
    /// Writes to a real path, which Windows can do with the stronger guarantee:
    /// AtomicFileWriter's swap means the destination is never seen half-written
    /// (SDD §3.4). Used when the host has a usable local path and no sandbox in the
    /// way.
    /// </summary>
    public void SaveToPath(string path)
    {
        if (_document is null)
            return;

        AtomicFileWriter.Write(path, stream => _document.Save(stream));
        DocumentPath = path;
        DocumentName = Path.GetFileName(path);
        IsDirty = false;
        Status = $"Saved {DocumentName}.";
    }

    private bool CanSave() => IsDocumentOpen && IsDirty;

    /// <summary>Raised when the view should perform a save; the view owns the file handle.</summary>
    public event Action? SaveRequested;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => SaveRequested?.Invoke();

    public void ReportSaveFailure(Exception ex) =>
        Status = $"Could not save: {ex.Message}";

    // --- Zoom ---

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomIn() => Zoom = NextStop(Zoom, forward: true);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomOut() => Zoom = NextStop(Zoom, forward: false);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomReset() => Zoom = 1.0;

    private static double NextStop(double current, bool forward)
    {
        if (forward)
        {
            foreach (var stop in ZoomStops)
                if (stop > current + 0.001)
                    return stop;
            return ZoomStops[^1];
        }

        for (var i = ZoomStops.Length - 1; i >= 0; i--)
            if (ZoomStops[i] < current - 0.001)
                return ZoomStops[i];
        return ZoomStops[0];
    }

    private void RerenderPage(int pageIndex)
    {
        var page = Pages.FirstOrDefault(p => p.Index == pageIndex);
        // Only if the view has it on screen; an off-screen page re-renders when it
        // scrolls back in, and will pick up the edit then.
        if (page is { IsRealised: true })
            page.Rerender(DpiScale);
    }

    private void RerenderRealisedPages()
    {
        foreach (var page in Pages)
            if (page.IsRealised)
                page.EnsureRendered(DpiScale);
    }

    private void CloseDocument()
    {
        foreach (var page in Pages)
            page.Dispose();
        Pages.Clear();

        _undoStack.Clear();
        RaiseUndoRedo();

        _document?.Dispose();
        _document = null;
        DocumentPath = null;
        DocumentName = null;
        IsDocumentOpen = false;
        IsDirty = false;
    }

    public void Dispose()
    {
        CloseDocument();
        _engine.Dispose();
    }
}
