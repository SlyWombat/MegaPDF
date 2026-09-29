using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// Page tools (#174): rotate, delete, reorder, insert a blank page, combine pages in from
/// another file, and extract pages out to a new one — the Mac and Linux half.
///
/// Where people see pages as pages is the <b>Pages sidebar</b>: a strip of thumbnails down the
/// left of the document, selectable, reorderable by drag, with the page number under each. That
/// is what both of these desktops already mean by "show me the pages": Preview's View ▸
/// Thumbnails (⌥⌘2) and Evince's and Okular's side pane (F9). It is deliberately *not* a
/// separate grid mode that replaces the document, which is Acrobat's "Organize Pages" — a mode
/// you enter, do one thing in and leave. Beside the page, the strip is where you already are:
/// turn page 3 and watch page 3 turn.
///
/// Every operation goes through the same <see cref="DocumentViewModel.Apply"/> pipeline as every
/// other edit, so each one is a single undo step, is recorded in the recovery journal, is gated
/// by the document's permissions, and reports its failures in the status line. The undo of a
/// delete puts back the page itself, not a copy of it, because the engine keeps it alive
/// (<see cref="RemovedPage"/>) precisely so it can.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Whether the Pages sidebar is showing. Per tab, and off by default: a document you are
    /// reading does not need it, and the whole point of the strip is that you ask for it.
    /// </summary>
    [ObservableProperty]
    private bool _isPageStripOpen;

    partial void OnIsPageStripOpenChanged(bool value)
    {
        if (!value)
            SelectedPageIndices = [];
    }

    [RelayCommand]
    private void TogglePageStrip()
    {
        if (IsDocumentOpen)
            IsPageStripOpen = !IsPageStripOpen;
    }

    /// <summary>
    /// The pages selected in the strip, ascending. Set by the view from the list's selection
    /// (and directly by the self-test); every page command acts on this, falling back to the
    /// page the person is on when the strip has no selection — so Rotate Right from the menu
    /// with no strip open still turns the page you are looking at.
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
            RaisePageCommands();
        }
    }

    private int[] _selectedPageIndices = [];

    public bool HasPageSelection => _selectedPageIndices.Length > 0;

    /// <summary>
    /// The pages a command acts on: the strip's selection, or the page in view when there is
    /// none. Ascending, and never empty while a document is open.
    /// </summary>
    public IReadOnlyList<int> TargetPages =>
        _selectedPageIndices.Length > 0 ? _selectedPageIndices
        : Pages.Count == 0 ? []
        : [Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];

    /// <summary>"Page 3" or "3 pages selected" — what the status line and the strip's label say.</summary>
    public string SelectedPageSummary => _selectedPageIndices switch
    {
        { Length: 0 } => "",
        { Length: 1 } one => Strings.PageSelected(one[0] + 1),
        var many => Strings.PagesSelected(many.Length),
    };

    /// <summary>
    /// Rotating, deleting, reordering, inserting and combining need the assemble permission —
    /// or modify, which is the stronger right (#174, ADR-004). Not the same gate as
    /// <see cref="CanEditContent"/>: a form that forbids content changes may still allow page
    /// assembly, and that is what the bit is for.
    /// </summary>
    public bool CanAssemblePages => IsDocumentOpen && Capabilities.CanAssemblePages && !Busy.IsBusy;

    /// <summary>Extracting pages makes a copy of part of the document, so it is the copy permission.</summary>
    public bool CanExtractPages => IsDocumentOpen && Capabilities.CanExtractPages && !Busy.IsBusy;

    /// <summary>
    /// A page's rotation in quarter turns clockwise, 0–3; -1 with no document or a bad index.
    /// Read from the engine rather than remembered: a rotation is the page's own <c>/Rotate</c>,
    /// and the document may have arrived with one already set.
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

    /// <summary>Deleting needs a page to spare: a PDF must keep one (the engine refuses the last).</summary>
    private bool CanDeletePages() => CanAssemblePages && TargetPages.Count > 0 && TargetPages.Count < Pages.Count;

    private bool CanRotatePages() => CanAssemblePages && TargetPages.Count > 0;

    private bool CanMovePageUp() => CanAssemblePages && TargetPages is [var only] && only > 0;

    private bool CanMovePageDown() => CanAssemblePages && TargetPages is [var only] && only < Pages.Count - 1;

    private bool CanExtractNow() => CanExtractPages && TargetPages.Count > 0;

    internal void RaisePageCommands()
    {
        OnPropertyChanged(nameof(CanAssemblePages));
        OnPropertyChanged(nameof(CanExtractPages));
        RotatePagesLeftCommand.NotifyCanExecuteChanged();
        RotatePagesRightCommand.NotifyCanExecuteChanged();
        DeletePagesCommand.NotifyCanExecuteChanged();
        MovePageUpCommand.NotifyCanExecuteChanged();
        MovePageDownCommand.NotifyCanExecuteChanged();
        InsertBlankPageCommand.NotifyCanExecuteChanged();
    }

    // --- The operations ------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRotatePages))]
    private void RotatePagesLeft() => Rotate(-1);

    [RelayCommand(CanExecute = nameof(CanRotatePages))]
    private void RotatePagesRight() => Rotate(1);

    private void Rotate(int quarterTurns)
    {
        if (_document is null || TargetPages is not { Count: > 0 } pages)
            return;
        var done = quarterTurns > 0
            ? Strings.Plural(pages.Count, Strings.PageTurnedRight, Strings.PagesTurnedRight(pages.Count))
            : Strings.Plural(pages.Count, Strings.PageTurnedLeft, Strings.PagesTurnedLeft(pages.Count));
        Apply(new RotatePagesOperation(_document, pages, quarterTurns), done);
    }

    [RelayCommand(CanExecute = nameof(CanDeletePages))]
    private void DeletePages()
    {
        if (_document is null || TargetPages is not { Count: > 0 } pages)
            return;
        // Said as a fact, with Undo the way back — not behind a confirmation dialog. A delete
        // here is one undo step that puts the page itself back (contract 10's removed page),
        // which is a stronger promise than "are you sure?" and does not stand in the way of
        // someone tidying a ten-page scan.
        var done = Strings.Plural(pages.Count, Strings.PageDeleted, Strings.PagesDeleted(pages.Count));
        Apply(new DeletePagesOperation(_document, pages), done);
    }

    [RelayCommand(CanExecute = nameof(CanMovePageUp))]
    private void MovePageUp()
    {
        if (TargetPages is [var page] && page > 0)
            MovePage(page, page - 1);
    }

    [RelayCommand(CanExecute = nameof(CanMovePageDown))]
    private void MovePageDown()
    {
        if (TargetPages is [var page] && page < Pages.Count - 1)
            MovePage(page, page + 1);
    }

    /// <summary>
    /// Moves one page so it stands at <paramref name="to"/> — the drag in the strip, and what
    /// Move Up/Move Down do one step at a time. The selection follows the page, not the index:
    /// the page you were holding is the page that stays selected.
    /// </summary>
    public void MovePage(int from, int to)
    {
        if (_document is null || from == to
            || from < 0 || from >= Pages.Count || to < 0 || to >= Pages.Count)
            return;
        Apply(new MovePageOperation(_document, from, to), Strings.PageMoved(from + 1, to + 1));
    }

    /// <summary>
    /// The size a new blank page takes: the page it is inserted after, so a blank page in an A4
    /// document is A4 and one in a Letter document is Letter. US Letter when there is nothing to
    /// copy, which cannot happen while a document is open and is the sane default if it ever does.
    /// </summary>
    private (double Width, double Height) BlankPageSize(int after)
    {
        if (after >= 0 && after < Pages.Count)
            return (Pages[after].PointWidth, Pages[after].PointHeight);
        return (612, 792);
    }

    [RelayCommand(CanExecute = nameof(CanAssemblePagesNow))]
    private void InsertBlankPage()
    {
        if (_document is null || Pages.Count == 0)
            return;
        // After the page you are on, which is where "insert a page" means on paper.
        var after = TargetPages.Count > 0 ? TargetPages[^1] : Pages.Count - 1;
        var at = after + 1;
        var (width, height) = BlankPageSize(after);
        Apply(new InsertBlankPageOperation(_document, at, width, height), Strings.BlankPageInserted(at + 1));
    }

    private bool CanAssemblePagesNow() => CanAssemblePages && Pages.Count > 0;

    /// <summary>
    /// Combine (#174): the pages of another PDF inserted after the page you are on. Awaited, so
    /// the view can report what happened; the refusals this can hit are the interesting part —
    /// see <see cref="DescribePageToolFailure"/>.
    /// </summary>
    public Task<bool> ImportPagesAsync(string otherPath, string? password = null,
                                       IReadOnlyList<int>? pages = null, int? insertAt = null)
    {
        if (_document is null || Pages.Count == 0)
            return Task.FromResult(false);
        var at = insertAt ?? (TargetPages.Count > 0 ? TargetPages[^1] + 1 : Pages.Count);
        at = Math.Clamp(at, 0, Pages.Count);
        var operation = new ImportPagesOperation(_document, otherPath, password, pages, at);
        var name = Path.GetFileName(otherPath);
        var applied = false;
        var task = ApplyAsync(operation,
            doneMessage: Strings.PagesInsertedFromFile(name),
            cancelled: null,
            applied: () => applied = true);
        return Finish();

        async Task<bool> Finish()
        {
            await task;
            return applied;
        }
    }

    /// <summary>
    /// Split (#174): the selected pages written to <paramref name="path"/> as a new PDF. The
    /// document itself is untouched, so this is not an edit — nothing is journalled, nothing
    /// becomes unsaved, and there is nothing to undo.
    /// </summary>
    public async Task<bool> ExtractPagesToPathAsync(string path, IReadOnlyList<int>? pages = null)
    {
        if (_document is not { } document)
            return false;
        if (!Capabilities.CanExtractPages)
        {
            Status = Strings.ActionRestricted;
            return false;
        }
        var wanted = pages ?? TargetPages;
        if (wanted.Count == 0)
            return false;

        using var busy = Busy.Begin(Strings.BusySaving);
        try
        {
            try
            {
                await OffUiThread(() => document.ExtractPages(wanted, path));
            }
            catch (PageToolException ex) when (ex.Reason == PageToolFailure.File)
            {
                // The engine writes through a *sibling* temporary file, read back and then
                // renamed into place (SDD §3.4). Under the Snap's `home` plug that sibling is
                // refused: the plug allows ~/pages.pdf and no hidden file beside it — the same
                // #158 trap the save path already knows about, and it is what "Save Selected
                // Pages As…" into your home folder hits. Staging inside our own writable area
                // and copying the verified bytes over is the way through. The write is still
                // staged and read back before anything is copied; only the last step becomes a
                // copy rather than a rename, which is the trade the sandboxed save path makes.
                await StagedExtractAsync(document, wanted,
                    () => Task.FromResult<Stream>(File.Create(path)));
            }
            Status = Strings.Plural(wanted.Count,
                Strings.PageSavedAs(Path.GetFileName(path)),
                Strings.PagesSavedAs(wanted.Count, Path.GetFileName(path)));
            return true;
        }
        catch (PageToolException ex)
        {
            Status = DescribePageToolFailure(ex);
            return false;
        }
        catch (Exception ex)
        {
            Status = Strings.WithDetail(Strings.CouldNotSave, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// <see cref="ExtractPagesToPathAsync"/> for a destination that is a stream rather than a
    /// path — a file the sandbox granted us and whose real path we may not have. The engine
    /// writes by path (its own staged, verified, atomic write), so this extracts to a private
    /// temporary file and copies it through, then removes it. The same shape as the save path.
    /// </summary>
    public async Task<bool> ExtractPagesThroughAsync(Func<Task<Stream>> openDestination, string fileName,
                                                     IReadOnlyList<int>? pages = null)
    {
        if (_document is not { } document)
            return false;
        if (!Capabilities.CanExtractPages)
        {
            Status = Strings.ActionRestricted;
            return false;
        }
        var wanted = pages ?? TargetPages;
        if (wanted.Count == 0)
            return false;

        using var busy = Busy.Begin(Strings.BusySaving);
        try
        {
            await StagedExtractAsync(document, wanted, openDestination);
            Status = Strings.Plural(wanted.Count,
                Strings.PageSavedAs(fileName), Strings.PagesSavedAs(wanted.Count, fileName));
            return true;
        }
        catch (PageToolException ex)
        {
            Status = DescribePageToolFailure(ex);
            return false;
        }
        catch (Exception ex)
        {
            Status = Strings.WithDetail(Strings.CouldNotSave, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Extracts into a private temporary file and copies the verified bytes to the destination.
    /// The engine's own write is unchanged — staged, read back, page count checked — so nothing
    /// is copied that has not already been proved to open; what this gives up is the atomic
    /// rename *at the destination*, which is the trade a granted stream forces anyway.
    /// </summary>
    private async Task StagedExtractAsync(IPdfDocument document, IReadOnlyList<int> pages,
                                          Func<Task<Stream>> openDestination)
    {
        var staged = Path.Combine(Path.GetTempPath(), $"megapdf-pages-{Guid.NewGuid():N}.pdf");
        try
        {
            await OffUiThread(() => document.ExtractPages(pages, staged));
            await using var source = File.OpenRead(staged);
            await using var destination = await openDestination();
            await source.CopyToAsync(destination);
        }
        finally
        {
            try
            {
                if (File.Exists(staged))
                    File.Delete(staged);
            }
            catch (IOException)
            {
                // A temporary file we could not remove is not worth failing a save over.
            }
        }
    }

    /// <summary>
    /// A suggested name for the extracted file: "Form (pages 2-3).pdf". Static so the Windows
    /// desktop can suggest the same thing (#174), the way <see cref="SuggestRedactedFileName"/> does.
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

    // --- Saying what the engine refused, and why -----------------------------

    /// <summary>
    /// The engine's typed refusal in the person's words (#174). Two of these are limits the
    /// engine has on purpose and the interface has to admit to rather than dress up as a
    /// failure:
    ///
    /// <see cref="PageToolFailure.FieldHierarchy"/> — the pages carry form fields whose names
    /// live on a parent field, a shape the page copy cannot carry, so the whole operation was
    /// refused and the document is exactly as it was. Roughly 0.8% of real documents. Saying
    /// "could not copy the pages" would be true and useless; what the person needs to know is
    /// that it is the form on those pages, that nothing was changed, and that printing or
    /// saving a copy still works.
    ///
    /// <see cref="PageToolFailure.LastPage"/> — a PDF must have a page, so the last one cannot
    /// be deleted. Said as the rule it is, with the way round it (extract the pages you want
    /// instead), rather than as a greyed-out button with no explanation.
    /// </summary>
    internal static string DescribePageToolFailure(PageToolException ex) => ex.Reason switch
    {
        PageToolFailure.FieldHierarchy => Strings.PagesRefusedFormFields,
        PageToolFailure.LastPage => Strings.CannotDeleteLastPage,
        PageToolFailure.Restricted => Strings.PageToolsRestricted,
        PageToolFailure.Password => Strings.OtherFileNeedsPassword,
        PageToolFailure.File => Strings.WithDetail(Strings.PagesFileProblem, ex.Message),
        PageToolFailure.Redacted => Strings.RedactFailed,
        PageToolFailure.Cancelled => Strings.PagesCancelled,
        PageToolFailure.OutOfRange => Strings.PageNoLongerThere,
        _ => Strings.WithDetail(Strings.ChangeFailed, ex.Message),
    };

    // --- Following the renumbering (contract 10) -----------------------------

    /// <summary>
    /// Brings this view model's index-keyed state in line with the document after a page
    /// operation (#174). Contract 10 is explicit that this is the app's job: the core keeps its
    /// own per-page state right — an open page handle follows its page, and so do that page's
    /// redaction marks, layout verdicts and detached objects — but it cannot see the page list,
    /// the rasters, the thumbnails, the settled #139 pages, the selection or the search hits.
    ///
    /// Applied from the shifts the operation reports rather than by re-reading the document:
    /// asking the engine for every page's size again would be one call per page, a thousand of
    /// them on #147's big file to move a single page.
    /// </summary>
    private void ApplyPageShifts(IReadOnlyList<PageShift> shifts, IReadOnlyList<int> changedPages)
    {
        if (_document is not { } document)
            return;

        // The shared, index-keyed piece first: without this a settled page's "already warned
        // about" would land on a different page after a delete and skip a warning that was owed.
        _pageWarnings.Renumber(shifts);

        foreach (var shift in shifts)
            ApplyOneShift(document, shift);

        for (var i = 0; i < Pages.Count; i++)
            Pages[i].Renumber(i);

        if (shifts.Any(s => s.Renumbers))
        {
            // Search hits: the rectangles are still right for the pages they are on, so the
            // matches travel with their pages rather than being thrown away — a find followed
            // by a delete should not lose the find.
            var kept = new List<Match>(_matches.Count);
            foreach (var match in _matches)
            {
                if (PageShift.Map(shifts, match.PageIndex) is { } now)
                    kept.Add(new Match(now, match.Rects));
            }
            _matches.Clear();
            _matches.AddRange(kept.OrderBy(m => m.PageIndex));
            MatchCount = _matches.Count;
            CurrentMatchIndex = _matches.Count == 0 ? -1 : Math.Clamp(CurrentMatchIndex, 0, _matches.Count - 1);
            ApplyHighlights();

            // The strip's selection follows the pages, not the indices: drag page 3 to the
            // front and page 3 is what stays selected. Pages that are gone drop out, which is
            // what leaves a delete with nothing selected.
            SelectedPageIndices = _selectedPageIndices
                .Select(page => PageShift.Map(shifts, page))
                .OfType<int>()
                .ToArray();
        }
        // The *page* selection — a signature, text box, whiteout or mark — is addressed by page
        // index and by bounds, and a rotation changes the bounds as surely as a move changes the
        // index. Cleared rather than chased: there is nothing a stale selection can do but act
        // on the wrong thing.
        ClearSelection();
        ClearPageFocus();

        // The pages a rotation turned: their raster and their thumbnail are of the other way up.
        foreach (var index in changedPages)
        {
            if (index < 0 || index >= Pages.Count)
                continue;
            using var handle = document.GetPage(index);
            Pages[index].Resize(handle.Width, handle.Height);
            Pages[index].InvalidateThumbnail();
            if (IsPageStripOpen)
                Pages[index].EnsureThumbnail();
            RerenderPage(index);
        }

        CurrentPage = Pages.Count == 0 ? 1 : Math.Clamp(CurrentPage, 1, Pages.Count);
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(HasPageSelection));
        OnPropertyChanged(nameof(SelectedPageSummary));
        RaisePageCommands();
    }

    private void ApplyOneShift(IPdfDocument document, PageShift shift)
    {
        switch (shift.Kind)
        {
            case PageShiftKind.Removed:
                for (var i = shift.At + shift.Count - 1; i >= shift.At && i < Pages.Count; i--)
                {
                    var gone = Pages[i];
                    Pages.RemoveAt(i);
                    gone.Dispose();
                }
                break;

            case PageShiftKind.Inserted:
                for (var i = 0; i < shift.Count; i++)
                {
                    var index = shift.At + i;
                    // The only engine calls this method makes, and one per *new* page: a page
                    // that arrived has no view model to renumber.
                    using var handle = document.GetPage(index);
                    var page = new PageViewModel(document, index, handle.Width, handle.Height)
                    {
                        Zoom = Zoom,
                        Tint = Tint,
                    };
                    Pages.Insert(Math.Min(index, Pages.Count), page);
                    if (IsPageStripOpen)
                        page.EnsureThumbnail();
                }
                break;

            case PageShiftKind.Moved:
                if (shift.At >= 0 && shift.At < Pages.Count)
                {
                    var moved = Pages[shift.At];
                    Pages.RemoveAt(shift.At);
                    Pages.Insert(Math.Clamp(shift.To, 0, Pages.Count), moved);
                }
                break;
        }
    }

    /// <summary>
    /// Hands back every page a delete in the history was still holding for its undo (#174).
    /// Called when the history is thrown away — the document closing, a redaction applied, a
    /// flatten — because a <see cref="RemovedPage"/> nothing can undo any more is a whole page
    /// held in the core until the document closes.
    /// </summary>
    private void DiscardHeldPages()
    {
        foreach (var operation in _undoStack.All.OfType<DeletePagesOperation>())
            operation.DiscardHeldPages();
    }

    /// <summary>
    /// Draws the thumbnails the strip has room for when it is first opened. The strip
    /// virtualizes, so this is the visible window and not the document.
    /// </summary>
    internal void PrepareThumbnails(int firstPage, int count)
    {
        for (var i = firstPage; i < Math.Min(firstPage + count, Pages.Count); i++)
        {
            if (i >= 0)
                Pages[i].EnsureThumbnail();
        }
    }
}
