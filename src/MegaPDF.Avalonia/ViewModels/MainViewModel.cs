using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Recovery;
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
    private readonly ISignatureLibrary _signatures;
    private readonly RecentFiles _recents;
    private readonly AppSettings _settings;
    private readonly RecoveryJournal _journal;
    private IPdfDocument? _document;

    /// <param name="stateDirectory">
    /// Where settings, recents, signatures and the recovery journal live. Null means
    /// the real per-user locations, which is what the app uses.
    ///
    /// It exists because --self-test used to run against those real locations: it
    /// wrote to the user's signature library and recent files, and — worse — left
    /// FlattenOnSave switched on, which then broke the *next* run's checks. A test
    /// that mutates the state of the machine it runs on is not a test.
    /// </param>
    public MainViewModel(string? stateDirectory = null)
    {
        if (stateDirectory is null)
        {
            _settings = new AppSettings();
            _recents = new RecentFiles();
            _signatures = new SignatureLibrary();
            _journal = new RecoveryJournal();
        }
        else
        {
            Directory.CreateDirectory(stateDirectory);
            _settings = new AppSettings(Path.Combine(stateDirectory, "settings.json"));
            _recents = new RecentFiles(Path.Combine(stateDirectory, "recent.json"));
            _signatures = new SignatureLibrary(Path.Combine(stateDirectory, "Signatures"));
            _journal = new RecoveryJournal(Path.Combine(stateDirectory, "Recovery"));
        }
    }

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

    /// <summary>The welcome panel shows until there is something to look at.</summary>
    public bool ShowEmptyState => !IsDocumentOpen;

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

    /// <summary>Raised when a document needs a password before it can be opened.</summary>
    public event Func<string, Task<string?>>? PasswordRequested;

    public void Open(string path) => Open(path, password: null);

    public void Open(string path, string? password)
    {
        CloseDocument();

        IPdfDocument document;
        try
        {
            document = _engine.Open(path, password);
        }
        catch (PdfLoadException ex)
        {
            if (ex.IsPasswordError && PasswordRequested is { } ask)
            {
                // Ask, then retry. Deliberately not a loop here — the view keeps
                // asking, so a wrong password re-prompts with the reason showing
                // rather than dumping the user back to an empty window.
                Status = password is null
                    ? "This PDF is password-protected."
                    : "That password did not work.";
                PendingPasswordPath = path;
                _ = RetryWithPasswordAsync(path, ask);
                return;
            }

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
        CurrentPage = 1;
        // Starting a session truncates any previous journal for this document, which
        // is why it happens after a successful open and not before.
        _journal.BeginSession(path);
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(ShowEmptyState));
        IsDirty = false;
        Status = $"{DocumentName} — {document.PageCount} page{(document.PageCount == 1 ? "" : "s")}. "
                 + "Click a checkbox to tick it.";
    }

    /// <summary>The document waiting on a password, if any.</summary>
    public string? PendingPasswordPath { get; private set; }

    private async Task RetryWithPasswordAsync(string path, Func<string, Task<string?>> ask)
    {
        var password = await ask(Path.GetFileName(path));
        PendingPasswordPath = null;

        if (string.IsNullOrEmpty(password))
        {
            Status = "Opening cancelled.";
            return;
        }

        Open(path, password);
    }

    /// <summary>MegaPDF-added text boxes on a page — what restyle and move address.</summary>
    internal IReadOnlyList<PdfTextRun> BoxesOn(int pageIndex)
    {
        if (_document is null)
            return [];
        using var page = _document.GetPage(pageIndex);
        return page.GetTextBoxes();
    }

    /// <summary>Visual lines of body text on a page — what F1 edits (SDD §3.1).</summary>
    internal IReadOnlyList<PdfTextLine> LinesOn(int pageIndex)
    {
        if (_document is null)
            return [];
        using var page = _document.GetPage(pageIndex);
        return page.GetTextLines();
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

        // Placement modes win over everything: the click is choosing a spot, not
        // asking what is under it.
        if (PendingSignature is { } pending)
        {
            PlacePendingSignature(pageIndex, point, pending);
            return;
        }

        if (Mode is PageMode.AddText or PageMode.Whiteout)
            return;   // the view drives these — an editor and a drag respectively

        var hit = HitTest(pageIndex, point);
        switch (hit.Kind)
        {
            case PageHitKind.FormCheckbox:
                Apply(new CheckboxToggleOperation(_document, pageIndex, hit.Field!), "Checkbox toggled.");
                break;

            case PageHitKind.DrawnCheckbox:
                Apply(new AddMarkOperation(_document, pageIndex, hit.Bounds!.Value, MarkStyle), "Checked.");
                break;

            case PageHitKind.StampAnnotation when !hit.AnnotationId!.StartsWith("sig:", StringComparison.Ordinal):
                // Check marks stay click-to-toggle: one size, one place, so there is
                // nothing to select them for (SDD §3.2).
                Apply(new RemoveMarkOperation(_document, pageIndex, hit.AnnotationId, hit.Bounds!.Value, MarkStyle),
                      "Unchecked.");
                break;

            case PageHitKind.StampAnnotation:
                // A placed signature selects for move, resize and delete (SDD §3.3).
                Select(new PageSelection(pageIndex, SelectionKind.Signature, hit.Bounds!.Value,
                                         AnnotationId: hit.AnnotationId));
                break;

            case PageHitKind.Whiteout:
                // Remove-only chrome: a cover is redrawn rather than nudged, which is
                // simpler and is how the Windows app behaves.
                Select(new PageSelection(pageIndex, SelectionKind.Whiteout, hit.Bounds!.Value,
                                         ObjectIndex: hit.ObjectIndex!.Value));
                break;

            case PageHitKind.FormTextField when hit.Field is { } field:
                EditFieldRequested?.Invoke(pageIndex, field);
                break;

            case PageHitKind.TextRun when hit.TextLine is { } line:
                // The view opens an editor over the line; the commit comes back
                // through EditLine.
                EditLineRequested?.Invoke(pageIndex, line);
                break;

            case PageHitKind.TextBox when hit.TextRun is { } run:
                // Added text moves and can be restyled, but not resized — its size is
                // a font size, not a rectangle.
                Select(new PageSelection(pageIndex, SelectionKind.TextBox, run.Bounds, Run: run));
                break;

            default:
                break;
        }
    }

    private void Apply(IPageEditOperation operation, string doneMessage)
    {
        _undoStack.Do(operation);
        // Journalled after Apply, because an operation's entry can only be written
        // once it knows what it did — a placed stamp's id, for instance.
        _journal.Record(operation.ToJournalEntry(inverse: false));
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
        // An undo is journalled as its own inverse entry: replaying the journal
        // after a crash must reproduce what was on screen, not what was ever done.
        if (op is not null)
            _journal.Record(op.ToJournalEntry(inverse: true));
        AfterEdit(op?.PageIndex ?? 0, "Undone.");
    }

    [RelayCommand]
    private void Redo()
    {
        if (!_undoStack.CanRedo)
            return;
        var op = _undoStack.PeekRedo as IPageEditOperation;
        _undoStack.Redo();
        if (op is not null)
            _journal.Record(op.ToJournalEntry(inverse: false));
        AfterEdit(op?.PageIndex ?? 0, "Redone.");
    }

    private void RaiseUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // --- Printing (SDD §3.5) and shrink-for-email (SDD §3.7) ---

    /// <summary>
    /// Writes the LIVE document — unsaved edits included — to a temp file and hands
    /// it to the platform printer. Printing what is on screen rather than what is on
    /// disk is the behaviour the Windows app documents, and the one people expect:
    /// you tick the boxes, then print.
    ///
    /// The temp file goes in <see cref="Path.GetTempPath"/>, which under the App
    /// Sandbox is inside the container, and is deleted as soon as the operation
    /// returns.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void Print()
    {
        if (_document is null)
            return;

        if (!OperatingSystem.IsMacOS())
        {
            // Deliberately not implemented for Avalonia-on-Windows: MegaPDF.App is
            // the Windows product and already prints. A second, half-working
            // implementation would be a liability for a case that does not exist.
            Status = "Printing from this build is available on macOS only.";
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"megapdf-print-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var file = File.Create(temp))
                _document.Save(file);

            var outcome = Platform.MacPrinter.Print(temp);
            Status = outcome.Message;
        }
        catch (Exception ex)
        {
            Status = $"Could not print: {ex.Message}";
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>
    /// Re-encodes oversized images so the document can be emailed (SDD §3.7). Works
    /// on a fresh copy from disk so the open document is never degraded, which is
    /// also why it insists on a saved file first.
    /// </summary>
    public ImageShrinker.Result? ShrinkForEmail(Stream destination)
    {
        if (DocumentPath is null)
            return null;

        using var copy = _engine.Open(DocumentPath);
        var result = ImageShrinker.Shrink(copy, Platform.SkiaJpeg.Encode);
        if (result.ImagesReplaced == 0)
            return result;

        VerifiedSave.ToStream(_engine, copy, destination);
        return result;
    }

    public bool CanShrink => IsDocumentOpen && !IsDirty;

    // --- Recent documents (SDD §2.2 empty state) ---

    public ObservableCollection<RecentEntry> Recents { get; } = [];

    public bool HasRecents => Recents.Count > 0;

    public void LoadRecents()
    {
        Recents.Clear();
        foreach (var entry in _recents.Entries)
            Recents.Add(entry);
        OnPropertyChanged(nameof(HasRecents));
    }

    /// <summary>
    /// Records a document as recently opened. The bookmark is what lets macOS
    /// reopen it in a later session at all — under the sandbox a stored path is not
    /// a key to anything.
    /// </summary>
    public void RememberRecent(string path, string? bookmark)
    {
        _recents.Add(path, bookmark);
        LoadRecents();
    }

    // --- Crash recovery (SDD §3.4) ---

    /// <summary>
    /// Documents that were being edited when the app last stopped without saving.
    /// Empty in the ordinary case, which is why the view only asks about it once.
    /// </summary>
    public IReadOnlyList<RecoverableSession> FindRecoverableSessions() =>
        _journal.FindRecoverableSessions();

    /// <summary>
    /// Reopens a document and replays the edits that were never saved.
    ///
    /// The entries are read BEFORE opening, because opening begins a new session and
    /// that truncates this very journal — a detail the Windows implementation calls
    /// out too, and one that silently loses the recovery if it is got wrong.
    /// </summary>
    public void RestoreSession(RecoverableSession session)
    {
        var entries = RecoveryJournal.LoadEntries(session.JournalPath);

        Open(session.DocumentPath);
        if (_document is null || entries.Count == 0)
            return;

        var applied = JournalReplayer.Replay(_document, entries);

        // Re-journal the restored edits, so a second crash before the first save is
        // still covered.
        foreach (var entry in entries)
            _journal.Record(entry);

        IsDirty = applied > 0;
        // Every rendered page predates the replay.
        foreach (var page in Pages)
            if (page.IsRealised)
                page.Rerender(DpiScale);

        Status = applied > 0
            ? $"Recovered {applied} unsaved change{(applied == 1 ? "" : "s")} to {DocumentName}."
            : $"Reopened {DocumentName}; there was nothing left to recover.";
    }

    public void DiscardSession(RecoverableSession session)
    {
        RecoveryJournal.Discard(session.JournalPath);
        Status = "Discarded the unsaved changes.";
    }

    // --- Selection (SDD §3.3: place it, then adjust it) ---

    public enum SelectionKind { Signature, TextBox, Whiteout }

    /// <summary>
    /// Something placed on the page that the user has selected. One record for all
    /// three kinds because the chrome is one mechanism — what differs is which
    /// handles it offers and what committing a drag calls.
    /// </summary>
    public sealed record PageSelection(
        int PageIndex, SelectionKind Kind, PdfRect Bounds,
        string? AnnotationId = null, int ObjectIndex = -1, PdfTextRun? Run = null)
    {
        /// <summary>Only a signature has a rectangle worth resizing.</summary>
        public bool CanResize => Kind == SelectionKind.Signature;

        /// <summary>A cover is redrawn rather than nudged.</summary>
        public bool CanMove => Kind is SelectionKind.Signature or SelectionKind.TextBox;
    }

    [ObservableProperty]
    private PageSelection? _selection;

    private void Select(PageSelection selection)
    {
        Selection = selection;
        Status = selection.Kind switch
        {
            SelectionKind.Signature => "Drag to move, corners to resize, Delete to remove.",
            SelectionKind.TextBox => "Drag to move, double-click to edit, Delete to remove.",
            _ => "Press Delete to remove this cover.",
        };
    }

    public void ClearSelection() => Selection = null;

    /// <summary>Removes whatever is selected, whichever kind it is.</summary>
    public void DeleteSelection()
    {
        if (_document is null || Selection is not { } sel)
            return;

        IPageEditOperation op = sel.Kind switch
        {
            SelectionKind.Signature =>
                new RemoveSignatureOperation(_document, sel.PageIndex, sel.AnnotationId!, sel.Bounds),
            SelectionKind.Whiteout =>
                new RemoveWhiteoutOperation(_document, sel.PageIndex, sel.ObjectIndex, sel.Bounds),
            _ => new RemoveTextBoxOperation(_document, sel.PageIndex, sel.Run!.ObjectIndex, sel.Run),
        };

        Selection = null;
        Apply(op, sel.Kind switch
        {
            SelectionKind.Signature => "Signature removed.",
            SelectionKind.Whiteout => "Cover removed.",
            _ => "Text removed.",
        });
    }

    /// <summary>Commits a drag or resize of whatever is selected.</summary>
    public void CommitSelectionBounds(PdfRect newBounds)
    {
        if (Selection is not { } sel || newBounds == sel.Bounds)
            return;

        switch (sel.Kind)
        {
            case SelectionKind.Signature:
                MoveSignature(sel.PageIndex, sel.AnnotationId!, sel.Bounds, newBounds);
                break;
            case SelectionKind.TextBox:
                MoveTextBox(sel.PageIndex, sel.Run!, newBounds);
                break;
            default:
                return;
        }

        // Re-anchor the chrome; the id and object index survive a move, so the
        // selection is still valid afterwards.
        Selection = sel with { Bounds = newBounds };
    }

    // --- Preferences (SDD §3.2, §3.3, §4.4) ---

    /// <summary>
    /// Which mark a ticked box gets: ✗ by default, per the 2026-07-08 stakeholder
    /// decision (SDD Appendix B #3). ✓ and ■ exist because a tick means "yes" in
    /// some countries and "this one" in others, and a filled square is what some
    /// official forms ask for.
    /// </summary>
    public CheckMarkStyle MarkStyle
    {
        get => _settings.MarkStyle;
        set
        {
            if (_settings.MarkStyle == value)
                return;
            _settings.MarkStyle = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<CheckMarkStyle> MarkStyles { get; } =
        [CheckMarkStyle.Cross, CheckMarkStyle.Check, CheckMarkStyle.FilledSquare];

    /// <summary>
    /// Bakes marks, signatures and form values permanently into the page on save
    /// (SDD §3.3). Off by default: flattening is irreversible, and someone who
    /// ticks a box today may need to untick it tomorrow.
    /// </summary>
    public bool FlattenOnSave
    {
        get => _settings.FlattenOnSave;
        set
        {
            if (_settings.FlattenOnSave == value)
                return;
            _settings.FlattenOnSave = value;
            OnPropertyChanged();
        }
    }

    // --- Placement modes (SDD §3.1, §3.3) ---

    /// <summary>
    /// What the next click on the page will do. Only one can be armed at a time —
    /// arming one cancels the others, because a click cannot mean two things.
    /// </summary>
    public enum PageMode
    {
        Select,
        AddText,
        Whiteout,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddingText))]
    [NotifyPropertyChangedFor(nameof(IsWhiteoutMode))]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    [NotifyPropertyChangedFor(nameof(IsModeActive))]
    private PageMode _mode = PageMode.Select;

    public bool IsAddingText => Mode == PageMode.AddText;
    public bool IsWhiteoutMode => Mode == PageMode.Whiteout;
    public bool IsModeActive => Mode != PageMode.Select || IsPlacingSignature;

    /// <summary>
    /// A banner telling the user what the next click does, and how to get out. A
    /// mode with no visible affordance is a mode people get stuck in (SDD §2.2).
    /// </summary>
    public string ModeHint => Mode switch
    {
        PageMode.AddText => "Click where the new text should go — Esc cancels",
        PageMode.Whiteout => "Drag over what you want to cover — Esc cancels",
        _ => IsPlacingSignature ? "Click where the signature should go — Esc cancels" : "",
    };

    /// <summary>The face and size the next text box is written in (SDD §3.1: three faces).</summary>
    [ObservableProperty]
    private string _textFont = StandardTextBoxFonts.Default;

    [ObservableProperty]
    private double _textSize = 12;

    public IReadOnlyList<string> TextFonts { get; } = StandardTextBoxFonts.All;
    public IReadOnlyList<double> TextSizes { get; } = [8, 9, 10, 11, 12, 14, 16, 18, 24];

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ToggleAddText() => SetMode(Mode == PageMode.AddText ? PageMode.Select : PageMode.AddText);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ToggleWhiteout() => SetMode(Mode == PageMode.Whiteout ? PageMode.Select : PageMode.Whiteout);

    private void SetMode(PageMode mode)
    {
        Selection = null;
        // Arming one mode disarms everything else, signature placement included.
        if (PendingSignature is not null && mode != PageMode.Select)
            PendingSignature = null;

        Mode = mode;
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(IsModeActive));
        if (mode == PageMode.Select)
            Status = "Ready.";
    }

    public void CancelModes()
    {
        PendingSignature = null;
        SetMode(PageMode.Select);
    }

    /// <summary>
    /// Edits a line of the document's own body text (SDD §3.1 — F1, the feature the
    /// product is named for). Tiered: the document's own font is used where it can
    /// render the new text, a similar standard font where it cannot, and scanned
    /// text is refused outright rather than silently mangled.
    /// </summary>
    public void EditLine(int pageIndex, PdfTextLine line, string newText)
    {
        if (_document is null || newText == line.Text)
            return;

        var operation = new LineEditOperation(_document, pageIndex, line, newText);
        try
        {
            _undoStack.Do(operation);
        }
        catch (TextEditException ex)
        {
            // These are the two honest refusals, and the wording matters more than
            // the exception: the person is holding a form, not a stack trace.
            Status = ex.Reason switch
            {
                TextEditFailure.NotExtractable =>
                    "That text is part of a scanned image, so it cannot be edited. "
                    + "You can cover it and type over the top instead.",
                _ => "That text uses a font that cannot write those characters, "
                     + "and no close substitute was available.",
            };
            return;
        }

        var note = operation.LastOutcome == TextEditOutcome.EditedWithSubstitutedFont
            ? "Text edited — the original font could not write that, so a close match was used."
            : "Text edited.";
        AfterEdit(pageIndex, note);
    }

    /// <summary>Fills an AcroForm text field (SDD §3.1, the form path).</summary>
    public void SetFieldValue(int pageIndex, PdfFormField field, string value)
    {
        if (_document is null || value == field.Value)
            return;

        Apply(new FormTextEditOperation(_document, pageIndex, field, value),
              string.IsNullOrEmpty(value) ? "Field cleared." : "Field filled.");
    }

    /// <summary>
    /// Removes a line of the document's own text (SDD §3.1). Separate from an edit
    /// to empty string: Core detaches the runs and keeps them alive, so undo
    /// restores the original fragmentation and fonts byte-identical rather than
    /// leaving an empty run behind.
    /// </summary>
    public void DeleteLine(int pageIndex, PdfTextLine line)
    {
        if (_document is null)
            return;

        Apply(new DeleteLineOperation(_document, pageIndex, line), "Text deleted.");
    }

    /// <summary>
    /// Rewrites an added text box in a new face, size or wording (#43, SDD §6.2
    /// contract 4). The box keeps its id across the change, which is what lets the
    /// mobile apps still address it afterwards.
    /// </summary>
    public void RestyleTextBox(int pageIndex, PdfTextRun box, string newText, string fontName, double fontSize)
    {
        if (_document is null)
            return;

        if (!StandardTextBoxFonts.IsSupported(fontName))
        {
            // The engine rejects anything outside the three, and it should: a
            // substituted face would silently break the cross-platform contract.
            Status = $"\"{fontName}\" is not one of the three available faces.";
            return;
        }

        Apply(new RestyleTextBoxOperation(_document, pageIndex, box.ObjectIndex, box,
                                          newText, fontName, fontSize),
              "Text updated.");
    }

    /// <summary>Moves an added text box (SDD §3.3 drag/nudge).</summary>
    public void MoveTextBox(int pageIndex, PdfTextRun box, PdfRect newBounds)
    {
        if (_document is null || newBounds == box.Bounds)
            return;

        Apply(new MoveTextBoxOperation(_document, pageIndex, box.ObjectIndex, box.Bounds, newBounds),
              "Text moved.");
    }

    /// <summary>Moves or resizes a placed signature (SDD §3.3).</summary>
    public void MoveSignature(int pageIndex, string annotationId, PdfRect oldBounds, PdfRect newBounds)
    {
        if (_document is null || newBounds == oldBounds)
            return;

        Apply(new MoveSignatureOperation(_document, pageIndex, annotationId, oldBounds, newBounds),
              "Signature moved.");
    }

    /// <summary>Adds a text box with the current face and size (SDD §3.1).</summary>
    public void AddTextBox(int pageIndex, PdfPoint topLeft, string text)
    {
        if (_document is null || string.IsNullOrWhiteSpace(text))
            return;

        Apply(new AddTextBoxOperation(_document, pageIndex, text, TextSize, topLeft, TextFont),
              "Text added.");
        SetMode(PageMode.Select);
    }

    /// <summary>Covers page content with a white rectangle (SDD §3.3).</summary>
    public void AddWhiteout(int pageIndex, PdfRect bounds)
    {
        // A stray click while the tool is armed should not stamp an invisible speck.
        if (_document is null || bounds.Width < 2 || bounds.Height < 2)
        {
            SetMode(PageMode.Select);
            return;
        }

        Apply(new AddWhiteoutOperation(_document, pageIndex, bounds), "Covered.");
        SetMode(PageMode.Select);
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

    /// <summary>Raised when the view should open an editor over a line of body text.</summary>
    public event Action<int, PdfTextLine>? EditLineRequested;

    /// <summary>Raised when the view should open an editor over an AcroForm text field.</summary>
    public event Action<int, PdfFormField>? EditFieldRequested;

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
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    [NotifyPropertyChangedFor(nameof(IsModeActive))]
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

    /// <summary>
    /// Adds a signature from a photograph or scan (SDD §3.3). The cleanup is the
    /// §6.2 contract-3 pipeline: near-white becomes transparent, then trim to the
    /// ink — but only when the image does not already carry transparency. A drawn
    /// PNG that arrives here would be damaged by white-removal, since its
    /// background is already nothing.
    /// </summary>
    public SignatureEntry AddSignatureFromImage(string name, SignatureBitmap image, Func<SignatureBitmap, byte[]> encodePng)
    {
        var cleaned = SignatureCleanup.HasTransparency(image.Bgra)
            ? SignatureCleanup.TrimToInk(image.Bgra, image.Width, image.Height)
            : SignatureCleanup.Clean(image);

        return AddSignature(name, encodePng(cleaned));
    }

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
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(IsModeActive));
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

        // Flattening is irreversible, so it must not touch the document the user
        // still has open — only the bytes on their way out.
        if (FlattenOnSave)
            _document.FlattenAllPages();

        // Verified rather than merely staged (#56): the bytes are reopened with the
        // engine before the user's file is touched, so a save that produced an
        // unreadable document leaves the original alone.
        VerifiedSave.ToStream(_engine, _document, destination);
        if (DocumentPath is { } saved)
            _journal.MarkSaved(saved);
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

        if (FlattenOnSave)
            _document.FlattenAllPages();

        VerifiedSave.ToPath(_engine, _document, path);
        _journal.MarkSaved(path);
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

    /// <summary>After Save As, the copy becomes the document being edited.</summary>
    public void AdoptSavedAs(string fileName)
    {
        DocumentName = fileName;
        IsDirty = false;
        Status = $"Saved {fileName}.";
    }

    public void ReportSaveFailure(Exception ex) =>
        Status = $"Could not save: {ex.Message}";

    // --- Zoom ---

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomIn() => Zoom = NextStop(Zoom, forward: true);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomOut() => Zoom = NextStop(Zoom, forward: false);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomReset() => Zoom = 1.0;

    /// <summary>
    /// Viewport size in device-independent pixels, set by the view. Fit-to-width and
    /// fit-to-page are meaningless without it.
    /// </summary>
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>Widest page in the document — fit-to-width must suit all of them.</summary>
    private double WidestPagePoints => Pages.Count == 0 ? 0 : Pages.Max(p => p.PointWidth);
    private double TallestPagePoints => Pages.Count == 0 ? 0 : Pages.Max(p => p.PointHeight);

    /// <summary>Page margins in the item template, so a fitted page is not clipped.</summary>
    private const double FitPadding = 32;

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void FitWidth()
    {
        if (WidestPagePoints <= 0 || ViewportWidth <= 0)
            return;
        Zoom = Clamp((ViewportWidth - FitPadding) / (WidestPagePoints * Rendering.PageBitmap.PointsToPixels));
    }

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void FitPage()
    {
        if (TallestPagePoints <= 0 || ViewportHeight <= 0)
            return;
        Zoom = Clamp((ViewportHeight - FitPadding) / (TallestPagePoints * Rendering.PageBitmap.PointsToPixels));
    }

    /// <summary>Fitted zooms are free-form, but still bounded by the stops' range.</summary>
    private static double Clamp(double zoom) => Math.Clamp(zoom, ZoomStops[0], ZoomStops[^1]);

    /// <summary>Which page is in view, 1-based. Set by the view as it scrolls.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    private int _currentPage = 1;

    public string PageIndicator => Pages.Count > 0 ? $"Page {CurrentPage} of {Pages.Count}" : "";

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
        Selection = null;
        RaiseUndoRedo();

        _document?.Dispose();
        _document = null;
        DocumentPath = null;
        DocumentName = null;
        IsDocumentOpen = false;
        IsDirty = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public void Dispose()
    {
        // A clean exit ends the session, so the next launch does not offer to
        // recover a document the user deliberately finished with.
        _journal.EndSession();
        _journal.Dispose();
        CloseDocument();
        _engine.Dispose();
    }
}
