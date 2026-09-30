using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace MegaPDF.App;

/// <summary>
/// Page tools (#174) for Windows: rotate, delete, reorder, insert a blank page, combine pages
/// in from another file, and extract pages out to a new one.
///
/// <b>Where pages are seen as pages is the Pages pane</b> — a grid of page tiles docked to the
/// left of the document, inside the tab, opened from the toolbar's Pages menu or with F4.
///
/// <i>Left of the page, inside the tab</i>, because that is where the PDF reader every Windows
/// machine already has puts it: Edge's own page pane. Not a full-window mode you enter and
/// leave, which is Acrobat's Organize Pages and takes the document away at the moment you most
/// want to see what you are doing to it.
///
/// <i>A grid rather than a one-column strip</i>, because acting on many thumbnails at once is
/// File Explorer's Gallery and the Photos app on this platform, and both are reflowing grids
/// with extended multi-select, drag to reorder and the commands on the right-click menu. The
/// Avalonia desktops took a one-column side pane, which is what Preview and Evince have; this
/// is the same place with the Windows shape in it: at the pane's own width it shows two pages
/// across, and more as the pane is widened, so a twelve-page scan is one glance not a scroll.
///
/// <i>And because it is a grid, the reorder axis is earlier and later in the document</i>, not
/// up and down a column: in two columns the tile above a page is two pages back. So the
/// commands are Move Page Earlier / Move Page Later on Ctrl+Shift+Left / Ctrl+Shift+Right,
/// where the other desktop has Move Up / Move Down on Alt with an arrow.
///
/// Keys are Windows', not the Mac's or GTK's with the modifier swapped (the rule #505 wrote
/// down): F4 shows and hides the pane, which is what Acrobat and Acrobat Reader have done with
/// the navigation pane on this platform for twenty years; Ctrl+R turns the page right and
/// Ctrl+Shift+R left, which is Windows Photos' rotate; Delete and the two reorder chords work
/// inside the pane only — on the window they would take the key from every text box.
///
/// Every operation goes through <see cref="DoEditAsync"/>, the same pipeline every other edit
/// uses, so each one is a single undo step, writes one recovery-journal entry, is gated by the
/// document's permissions, and reports a refusal in the person's words. The undo of a delete
/// puts back the page itself, not a copy of it, because the engine keeps it alive
/// (<see cref="RemovedPage"/>) precisely so that it can.
/// </summary>
public partial class DocumentViewModel
{
    // --- The pane ------------------------------------------------------------

    /// <summary>
    /// Whether the Pages pane is showing. Per tab, and off by default: a document you are
    /// reading does not need it, and the point of the pane is that you ask for it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesPaneVisibility))]
    private bool _isPagesPaneOpen;

    partial void OnIsPagesPaneOpenChanged(bool value)
    {
        if (!value)
            SelectedPageIndices = [];
    }

    /// <summary>
    /// The pane is chrome, so reading mode hides it along with the toolbar and the tab strip
    /// (#504) — collapsed, not merely invisible, so nothing in it is left in the tab order of a
    /// window that is showing the page and nothing else. Leaving the mode brings it back if it
    /// was open, because <see cref="IsPagesPaneOpen"/> was never changed.
    /// </summary>
    public Visibility PagesPaneVisibility =>
        IsPagesPaneOpen && !IsReadingMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>One tile per page, in page order. Renumbered in place, never rebuilt (see <see cref="PageThumbnail"/>).</summary>
    public ObservableCollection<PageThumbnail> Thumbnails { get; } = [];

    /// <summary>
    /// True while the tiles are being moved, added or removed to match a page operation. The pane
    /// stops reporting its grid's selection back for the duration: a selected tile leaving the
    /// collection looks to the grid exactly like the person deselecting it, and that must not be
    /// allowed to overwrite the selection the renumbering is about to move.
    /// </summary>
    internal bool IsRenumbering { get; private set; }

    /// <summary>
    /// The pages selected in the pane, ascending. Set by the grid from its own selection (and
    /// directly by the self-test); every page command acts on this, falling back to the page in
    /// view when there is no selection — so Rotate Right from the toolbar with the pane shut
    /// still turns the page you are looking at.
    /// </summary>
    public IReadOnlyList<int> SelectedPageIndices
    {
        get => _selectedPageIndices;
        set
        {
            var next = value?.Where(i => i >= 0 && i < Pages.Count).Distinct().Order().ToArray() ?? [];
            if (next.SequenceEqual(_selectedPageIndices))
                return;
            _selectedPageIndices = next;
            OnPropertyChanged(nameof(SelectedPageIndices));
            OnPropertyChanged(nameof(HasPageSelection));
            OnPropertyChanged(nameof(SelectedPageSummary));
            OnPropertyChanged(nameof(PagesPaneHeading));
            RaisePageCommands();
        }
    }

    private int[] _selectedPageIndices = [];

    public bool HasPageSelection => _selectedPageIndices.Length > 0;

    /// <summary>
    /// The pages a command acts on: the pane's selection, or the page in view when there is
    /// none. Ascending, and never empty while a document is open.
    /// </summary>
    public IReadOnlyList<int> TargetPages =>
        _selectedPageIndices.Length > 0 ? _selectedPageIndices
        : Pages.Count == 0 ? []
        : [Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];

    /// <summary>
    /// The pane's own line: what is selected, or the pane's name when nothing is. A live region,
    /// so a screen reader hears the selection change without being asked.
    /// </summary>
    public string PagesPaneHeading =>
        SelectedPageSummary is { Length: > 0 } summary ? summary : Strings.ToolbarPages;

    /// <summary>"Page 3 selected" or "3 pages selected" — the pane's header and its announcement.</summary>
    public string SelectedPageSummary => _selectedPageIndices switch
    {
        { Length: 0 } => "",
        { Length: 1 } one => Strings.PageSelected(one[0] + 1),
        var many => Strings.PagesSelected(many.Length),
    };

    // --- What the document allows --------------------------------------------

    /// <summary>
    /// Rotating, deleting, reordering, inserting and combining need the assemble permission —
    /// or modify, which is the stronger right (#174, ADR-004). Deliberately not the same gate as
    /// <see cref="IsEditingAllowed"/>: a form that forbids content changes may still allow its
    /// pages to be assembled, and that is the case P-bit 11 exists for.
    /// </summary>
    public bool CanAssemblePages => IsDocumentOpen && Capabilities.CanAssemblePages && !Busy.IsBusy;

    /// <summary>Extracting makes a copy of part of the document, so it is the copy permission.</summary>
    public bool CanExtractPages => IsDocumentOpen && Capabilities.CanExtractPages && !Busy.IsBusy;

    /// <summary>
    /// A page's rotation in quarter turns clockwise, 0–3; -1 with no document or a bad index.
    /// Read from the engine rather than remembered: the rotation is the page's own
    /// <c>/Rotate</c>, and the document may well have arrived with one already set.
    /// </summary>
    public int PageRotation(int pageIndex)
    {
        if (_document is not { } document || pageIndex < 0 || pageIndex >= Pages.Count)
            return -1;
        try
        {
            return document.GetPageRotation(pageIndex);
        }
        catch (PageToolException)
        {
            return -1;
        }
    }

    private bool CanRotatePages() => CanAssemblePages && TargetPages.Count > 0;

    /// <summary>Deleting needs a page to spare: a PDF must keep one, and the engine refuses the last.</summary>
    private bool CanDeletePages() => CanAssemblePages && TargetPages.Count > 0 && TargetPages.Count < Pages.Count;

    private bool CanMovePageEarlier() => CanAssemblePages && TargetPages is [var only] && only > 0;

    private bool CanMovePageLater() => CanAssemblePages && TargetPages is [var only] && only < Pages.Count - 1;

    private bool CanInsertPages() => CanAssemblePages && Pages.Count > 0;

    private bool CanExtractNow() => CanExtractPages && TargetPages.Count > 0;

    /// <summary>
    /// Every page command's enabled state, re-asked. Called from the selection setter, from a
    /// page operation's aftermath, and from the busy-state and capability notifications the main
    /// view model already raises — a page tool waits while blocking work runs, like every other.
    /// </summary>
    internal void RaisePageCommands()
    {
        OnPropertyChanged(nameof(CanAssemblePages));
        OnPropertyChanged(nameof(CanExtractPages));
        RotatePagesRightCommand.NotifyCanExecuteChanged();
        RotatePagesLeftCommand.NotifyCanExecuteChanged();
        DeletePagesCommand.NotifyCanExecuteChanged();
        MovePageEarlierCommand.NotifyCanExecuteChanged();
        MovePageLaterCommand.NotifyCanExecuteChanged();
        InsertBlankPageCommand.NotifyCanExecuteChanged();
        InsertPagesFromFileCommand.NotifyCanExecuteChanged();
        ExtractSelectedPagesCommand.NotifyCanExecuteChanged();
    }

    // --- The operations ------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRotatePages))]
    private Task RotatePagesRightAsync() => RotateAsync(1);

    [RelayCommand(CanExecute = nameof(CanRotatePages))]
    private Task RotatePagesLeftAsync() => RotateAsync(-1);

    private Task RotateAsync(int quarterTurns)
    {
        if (_document is not { } document || TargetPages is not { Count: > 0 } pages)
            return Task.CompletedTask;
        var said = quarterTurns > 0
            ? pages.Count == 1 ? Strings.PageTurnedRight : Strings.PagesTurnedRight(pages.Count)
            : pages.Count == 1 ? Strings.PageTurnedLeft : Strings.PagesTurnedLeft(pages.Count);
        return DoPageEditAsync(new RotatePagesOperation(document, pages, quarterTurns), said);
    }

    [RelayCommand(CanExecute = nameof(CanDeletePages))]
    private Task DeletePagesAsync()
    {
        if (_document is not { } document || TargetPages is not { Count: > 0 } pages)
            return Task.CompletedTask;
        // Said as a fact afterwards, not asked as "are you sure?" first. A delete here is one
        // undo step that puts the page itself back (contract 10's removed page), which is a
        // stronger promise than a confirmation dialog and does not stand in the way of somebody
        // tidying a ten-page scan.
        var said = pages.Count == 1 ? Strings.PageDeleted : Strings.PagesDeleted(pages.Count);
        return DoPageEditAsync(new DeletePagesOperation(document, pages), said);
    }

    [RelayCommand(CanExecute = nameof(CanMovePageEarlier))]
    private Task MovePageEarlierAsync() =>
        TargetPages is [var page] && page > 0 ? MovePageAsync(page, page - 1) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanMovePageLater))]
    private Task MovePageLaterAsync() =>
        TargetPages is [var page] && page < Pages.Count - 1 ? MovePageAsync(page, page + 1) : Task.CompletedTask;

    /// <summary>
    /// Moves one page so it stands at <paramref name="to"/> — a drop in the pane's grid, and
    /// what Move Earlier and Move Later do one step at a time. The selection follows the page
    /// and not the index: the page you dragged is the page that stays selected.
    /// </summary>
    public Task MovePageAsync(int from, int to)
    {
        if (_document is not { } document || from == to
            || from < 0 || from >= Pages.Count || to < 0 || to >= Pages.Count)
            return Task.CompletedTask;
        return DoPageEditAsync(new MovePageOperation(document, from, to), Strings.PageMoved(from + 1, to + 1));
    }

    /// <summary>
    /// The size a new blank page takes: the page it goes in after, so a blank page in an A4
    /// document is A4 and one in a Letter document is Letter. US Letter when there is nothing to
    /// copy, which cannot happen while a document is open and is the sane answer if it ever does.
    /// </summary>
    private (double Width, double Height) BlankPageSize(int after) =>
        after >= 0 && after < Pages.Count ? (Pages[after].PointsWidth, Pages[after].PointsHeight) : (612, 792);

    [RelayCommand(CanExecute = nameof(CanInsertPages))]
    private Task InsertBlankPageAsync()
    {
        if (_document is not { } document || Pages.Count == 0)
            return Task.CompletedTask;
        // After the page you are on, which is what "insert a page" means on paper.
        var after = TargetPages.Count > 0 ? TargetPages[^1] : Pages.Count - 1;
        var at = after + 1;
        var (width, height) = BlankPageSize(after);
        return DoPageEditAsync(new InsertBlankPageOperation(document, at, width, height),
                               Strings.BlankPageInserted(at + 1));
    }

    /// <summary>
    /// Combine (#174): the pages of another PDF, inserted after the page you are on. The picker
    /// is here rather than in the window because every other file this view model reads opens
    /// the same way (<see cref="SaveAsAsync"/>'s), and because the refusal it can hit has to be
    /// worded by whoever applied the operation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInsertPages))]
    private async Task InsertPagesFromFileAsync()
    {
        if (_document is null || Pages.Count == 0 || Busy.IsBusy)
            return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        await ImportPagesAsync(file.Path);
    }

    /// <summary>
    /// Combine, with the file already chosen — what the picker calls and what the self-test
    /// drives. True when the pages arrived; a refusal has already been said by then.
    /// </summary>
    public async Task<bool> ImportPagesAsync(string otherPath, string? password = null,
                                             IReadOnlyList<int>? pages = null, int? insertAt = null)
    {
        if (_document is not { } document || Pages.Count == 0)
            return false;
        var at = Math.Clamp(insertAt ?? (TargetPages.Count > 0 ? TargetPages[^1] + 1 : Pages.Count), 0, Pages.Count);
        var operation = new ImportPagesOperation(document, otherPath, password, pages, at);
        return await DoPageEditAsync(operation, Strings.PagesInsertedFromFile(Path.GetFileName(otherPath)));
    }

    /// <summary>
    /// Split (#174): the selected pages written to a picked file as a new PDF. The document
    /// itself is untouched, so this is not an edit — nothing is journalled, nothing becomes
    /// unsaved, and there is nothing to undo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExtractNow))]
    private async Task ExtractSelectedPagesAsync()
    {
        if (_document is null || TargetPages is not { Count: > 0 } pages || Busy.IsBusy)
            return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add(Strings.PdfDocumentFilter, [".pdf"]);
        picker.SuggestedFileName = SuggestExtractedFileName(OpenDocumentName, pages);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        await ExtractPagesToPathAsync(file.Path, pages);
    }

    /// <summary>
    /// The extract itself, with the destination already chosen. The engine writes by path
    /// through its own staged-then-verified write (SDD §3.4), so a crash or a full disk leaves
    /// either the old file or the new one and never a torn one — which is why this hands it a
    /// path rather than the picked file's stream.
    /// </summary>
    public async Task<bool> ExtractPagesToPathAsync(string path, IReadOnlyList<int>? pages = null)
    {
        if (_document is not { } document)
            return false;
        if (!Capabilities.CanExtractPages)
        {
            ShowPageToolNotice(Strings.PageToolsRestricted);
            return false;
        }
        var wanted = pages ?? TargetPages;
        if (wanted.Count == 0)
            return false;

        try
        {
            using (Busy.Begin(Strings.BusySaving))
                await Task.Run(() => document.ExtractPages(wanted, path));
        }
        catch (PageToolException ex)
        {
            ShowPageToolNotice(DescribePageToolFailure(ex));
            return false;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotSaveTitle, UserFacing.Describe(ex));
            return false;
        }
        var name = Path.GetFileName(path);
        Announced?.Invoke(wanted.Count == 1 ? Strings.PageSavedAs(name) : Strings.PagesSavedAs(wanted.Count, name));
        return true;
    }

    /// <summary>
    /// A suggested name for the extracted file: "Form (pages 2-3).pdf". The same three shapes
    /// the Avalonia leg suggests, from the same catalogue keys, so the two desktops name a split
    /// file identically (#174).
    /// </summary>
    public static string SuggestExtractedFileName(string documentName, IReadOnlyList<int> pages)
    {
        var stem = Path.GetFileNameWithoutExtension(documentName);
        if (string.IsNullOrEmpty(stem))
            stem = Strings.DefaultDocumentName;
        var label = pages.Count switch
        {
            0 => "",
            1 => Strings.ExtractedOnePageName(pages[0] + 1),
            _ when IsRun(pages) => Strings.ExtractedPageRangeName(pages[0] + 1, pages[^1] + 1),
            _ => Strings.ExtractedPageCountName(pages.Count),
        };
        return $"{stem} ({label}).pdf";

        static bool IsRun(IReadOnlyList<int> pages)
        {
            for (var i = 1; i < pages.Count; i++)
            {
                if (pages[i] != pages[i - 1] + 1)
                    return false;
            }
            return true;
        }
    }

    // --- Saying what the engine refused, and why ------------------------------

    /// <summary>
    /// Where a page tool's refusal is shown. An InfoBar over the page, which is what this app
    /// already uses to say a thing happened that the person did not ask a dialog about (the
    /// restricted-document notice, the redaction summary) — not a modal, because nothing was
    /// changed and there is nothing to decide.
    /// </summary>
    [ObservableProperty]
    private string _pageToolNotice = "";

    [ObservableProperty]
    private bool _isPageToolNoticeOpen;

    /// <summary>
    /// The one refusal that is reached without asking the engine: Delete Page is off because the
    /// selection is the whole document, and a PDF must keep a page. Said as the rule it is, with
    /// the way round it, rather than as a command that does nothing when pressed — the same
    /// sentence <see cref="DescribePageToolFailure"/> gives for the engine's own
    /// <see cref="PageToolFailure.LastPage"/>, from the same string, so the two cannot drift.
    /// </summary>
    internal void ShowLastPageRefusal() => ShowPageToolNotice(Strings.CannotDeleteLastPage);

    private void ShowPageToolNotice(string text)
    {
        PageToolNotice = text;
        IsPageToolNoticeOpen = true;
        // Also spoken: an InfoBar that appears at the bottom of the page is easy to miss, and
        // the one thing a refusal must not be is silent.
        Announced?.Invoke(text);
    }

    /// <summary>
    /// The engine's typed refusal in the person's words (#174). Two of these are limits the
    /// engine has on purpose, and the interface has to admit to them rather than dress them up
    /// as a failure:
    ///
    /// <see cref="PageToolFailure.FieldHierarchy"/> — the pages carry form fields whose names
    /// live on a parent field, a shape the page copy cannot carry, so the whole operation was
    /// refused and the document is exactly as it was. Roughly 0.8% of real documents. "Could not
    /// copy the pages" would be true and useless; what the person needs to know is that it is
    /// the form on those pages, that nothing was changed, and that printing and saving a copy of
    /// the whole document still work.
    ///
    /// <see cref="PageToolFailure.LastPage"/> — a PDF must have a page, so the last one cannot
    /// be deleted. Said as the rule it is, with the way round it (extract the pages you want
    /// instead), rather than as a greyed-out command with no explanation.
    ///
    /// What is deliberately <i>not</i> here is a layout refusal. No page operation rewrites a
    /// content stream — a rotation is <c>/Rotate</c> and nothing else — so the #118 guard is not
    /// in this path at all and cannot answer <c>MEGAPDF_ERR_LAYOUT</c> (#556 established this);
    /// inventing a dialog for it would be a dialog for a state that cannot happen.
    /// </summary>
    internal static string DescribePageToolFailure(PageToolException ex) => ex.Reason switch
    {
        PageToolFailure.FieldHierarchy => Strings.PagesRefusedFormFields,
        PageToolFailure.LastPage => Strings.CannotDeleteLastPage,
        PageToolFailure.Restricted => Strings.PageToolsRestricted,
        PageToolFailure.Password => Strings.OtherFileNeedsPassword,
        PageToolFailure.File => Strings.PagesFileProblem,
        PageToolFailure.Redacted => Strings.RedactFailed,
        PageToolFailure.Cancelled => Strings.PagesCancelled,
        PageToolFailure.OutOfRange => Strings.PageNoLongerThere,
        _ => UserFacing.Describe(ex),
    };

    // --- Following the renumbering (contract 10) ------------------------------

    /// <summary>
    /// Brings this view model's index-keyed state in line with the document after a page
    /// operation (#174). Contract 10 is explicit that this is the app's job: the core keeps its
    /// own per-page state right — an open page handle follows its page, and so do that page's
    /// redaction marks, layout verdicts and detached objects — but it cannot see the page list,
    /// the rasters, the pane's tiles, the keyboard maps, the settled #139 pages, the selection
    /// or the search hits.
    ///
    /// Applied from the shifts the operation reports rather than by re-reading the document:
    /// asking the engine for every page's size again would be one call per page, a thousand of
    /// them on #147's big file to move a single page.
    /// </summary>
    private async Task ApplyPageShiftsAsync(IReadOnlyList<PageShift> shifts, IReadOnlyList<int> changedPages)
    {
        if (_document is not { } document)
            return;

        // The shared, index-keyed piece first: without it a settled page's "already warned
        // about" would land on a different page after a delete and skip a warning that was owed.
        _pageWarnings.Renumber(shifts);

        // The selection as it was *before* the tiles move, and the pane told to stop reporting its
        // own selection until they have. Taking a selected tile out of the collection makes the
        // pane's GridView drop it from its own SelectedItems and say so, and that arrives here as
        // "the person deselected everything" — which wiped the selection a moment before the
        // renumbering below was going to move it, so a dragged page came back unselected and its
        // Move Up/Move Down went dead. Caught by the `pages` self-test, not by looking at it.
        var selectedBefore = _selectedPageIndices;
        IsRenumbering = true;
        try
        {
            foreach (var shift in shifts)
                ApplyOneShift(document, shift);
        }
        finally
        {
            IsRenumbering = false;
        }

        for (var i = 0; i < Thumbnails.Count; i++)
            Thumbnails[i].Renumber(i);

        // The keyboard maps and the capped rasters are both keyed by page index, and a page that
        // moved takes neither with it. Cleared outright rather than remapped: they are caches,
        // and a wrong entry in either acts on the wrong page (#2, #94).
        _keyboardMaps.Clear();
        _cappedRenders.Clear();

        if (shifts.Any(s => s.Renumbers))
        {
            // Search hits: the rectangles are still right for the pages they are on, so the
            // matches travel with their pages rather than being thrown away — a find followed by
            // a delete should not lose the find.
            var kept = new List<(int PageIndex, IReadOnlyList<PdfRect> Rects)>(_searchMatches.Count);
            foreach (var match in _searchMatches)
            {
                if (PageShift.Map(shifts, match.PageIndex) is { } now)
                    kept.Add((now, match.Rects));
            }
            _searchMatches.Clear();
            _searchMatches.AddRange(kept.OrderBy(m => m.PageIndex));
            SearchMatchCount = _searchMatches.Count;
            CurrentSearchMatch = _searchMatches.Count == 0 ? 0 : Math.Clamp(CurrentSearchMatch, 1, _searchMatches.Count);

            // The pane's selection follows the pages, not the indices: drag page 3 to the front
            // and page 3 is what stays selected. Pages that are gone drop out, which is what
            // leaves a delete with nothing selected.
            SelectedPageIndices = selectedBefore
                .Select(page => PageShift.Map(shifts, page))
                .OfType<int>()
                .ToArray();
            // Said again even when the remapped selection is the same list it was: the tiles moved
            // under the grid, so its own selection needs writing back whether or not the page
            // numbers in it changed.
            OnPropertyChanged(nameof(SelectedPageIndices));
        }

        // The keyboard focus ring is addressed by page index and by bounds, and a rotation
        // changes the bounds as surely as a move changes the index. Cleared rather than chased:
        // there is nothing a stale ring can do but act on the wrong thing.
        ClearPageFocus();

        PageCount = Pages.Count;
        CurrentPage = Pages.Count == 0 ? 1 : Math.Clamp(CurrentPage, 1, Pages.Count);

        // The pages a rotation turned: their raster, their tile and their size are all of the
        // other way up now. Sizes come from the core, which reports the rotated crop box (#439).
        foreach (var index in changedPages)
        {
            if (index < 0 || index >= Pages.Count)
                continue;
            double width, height;
            using (var handle = document.GetPage(index))
                (width, height) = (handle.Width, handle.Height);
            Pages[index] = Placeholder(index, width, height);
            if (index < Thumbnails.Count)
            {
                Thumbnails[index].Resize(width, height);
                Thumbnails[index].Invalidate();
            }
        }

        ApplySearchHighlights();
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(HasPageSelection));
        OnPropertyChanged(nameof(SelectedPageSummary));
        OnPropertyChanged(nameof(PagesPaneHeading));
        RaisePageCommands();

        await UpdateViewportAsync(_viewFirst, _viewLast);
        // The tiles a rotation invalidated are already realised, so the grid will not ask for
        // them again on its own: the view redraws whichever of them are on screen.
        ThumbnailsInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyOneShift(IPdfDocument document, PageShift shift)
    {
        switch (shift.Kind)
        {
            case PageShiftKind.Removed:
                for (var i = shift.At + shift.Count - 1; i >= shift.At; i--)
                {
                    if (i < Pages.Count)
                        Pages.RemoveAt(i);
                    if (i < Thumbnails.Count)
                    {
                        var gone = Thumbnails[i];
                        Thumbnails.RemoveAt(i);
                        gone.Dispose();
                    }
                }
                break;

            case PageShiftKind.Inserted:
                for (var i = 0; i < shift.Count; i++)
                {
                    var index = shift.At + i;
                    // The only engine calls this method makes, and one per *new* page: a page
                    // that arrived has no slot or tile to renumber.
                    double width, height;
                    using (var handle = document.GetPage(index))
                        (width, height) = (handle.Width, handle.Height);
                    Pages.Insert(Math.Min(index, Pages.Count), Placeholder(index, width, height));
                    Thumbnails.Insert(Math.Min(index, Thumbnails.Count),
                                      new PageThumbnail(document, index, width, height));
                }
                break;

            case PageShiftKind.Moved:
                if (shift.At >= 0 && shift.At < Pages.Count)
                {
                    var slot = Pages[shift.At];
                    Pages.RemoveAt(shift.At);
                    Pages.Insert(Math.Clamp(shift.To, 0, Pages.Count), slot);
                }
                if (shift.At >= 0 && shift.At < Thumbnails.Count)
                {
                    var tile = Thumbnails[shift.At];
                    Thumbnails.RemoveAt(shift.At);
                    Thumbnails.Insert(Math.Clamp(shift.To, 0, Thumbnails.Count), tile);
                }
                break;
        }
    }

    // --- The tiles ------------------------------------------------------------

    /// <summary>
    /// Builds a tile per page for a freshly opened document. The tiles carry geometry only; no
    /// raster is drawn until the grid realises the tile, so opening a thousand-page file inside
    /// a shut pane costs nothing at all.
    /// </summary>
    private void BuildThumbnails(IPdfDocument document, IReadOnlyList<(double W, double H)> sizes)
    {
        foreach (var tile in Thumbnails)
            tile.Dispose();
        Thumbnails.Clear();
        for (var i = 0; i < sizes.Count; i++)
            Thumbnails.Add(new PageThumbnail(document, i, sizes[i].W, sizes[i].H));
    }

    /// <summary>
    /// Some tiles' rasters are of the wrong way up now (a rotation) — the view redraws the ones
    /// it has realised. Raised rather than pushed, because which tiles are on screen is the
    /// grid's business and it will not re-realise a container it already has.
    /// </summary>
    internal event EventHandler? ThumbnailsInvalidated;

    /// <summary>
    /// Draws one tile if it has none. Called by the pane as the grid realises each tile, so a
    /// shut pane costs nothing and an open one costs only the tiles in view — which is what
    /// makes a pane over #147's thousand-page file affordable.
    /// </summary>
    internal Task EnsureTileAsync(int index)
    {
        if (index < 0 || index >= Thumbnails.Count)
            return Task.CompletedTask;
        return Thumbnails[index].EnsureAsync(window.Content?.XamlRoot?.RasterizationScale ?? 1.0);
    }

    // --- Letting go of the pages an undo was holding ---------------------------

    /// <summary>
    /// Hands back every page a delete in the history was still holding for its undo (#174).
    /// Called wherever the history is thrown away — the document closing or being replaced, a
    /// redaction applied, a flatten — because a <see cref="RemovedPage"/> nothing can undo any
    /// more is a whole page held in the core until the document closes.
    /// </summary>
    private void DiscardHeldPages()
    {
        foreach (var operation in _undoStack.All.OfType<DeletePagesOperation>())
            operation.DiscardHeldPages();
    }

    /// <summary>
    /// Writes the open document — unsaved page operations and all — to <paramref name="path"/>
    /// through the same staged, verified write Save uses. For the `pages` self-test, which has to
    /// prove a rotation reaches the file and cannot drive a file picker from inside the process.
    /// </summary>
    internal Task SaveToPathForTestAsync(string path)
    {
        if (_document is not { } document)
            return Task.CompletedTask;
        return Task.Run(() => MegaPDF.Core.Services.VerifiedSave.ToPath(Engine, document, path, _ => { }));
    }

    /// <summary>A new document, or none: nothing of the previous one is selected or tiled.</summary>
    private void ResetPagesPane()
    {
        SelectedPageIndices = [];
        IsPageToolNoticeOpen = false;
        PageToolNotice = "";
        foreach (var tile in Thumbnails)
            tile.Dispose();
        Thumbnails.Clear();
    }
}
