namespace MegaPDF.Core.Engine;

/// <summary>
/// How one page operation renumbered a document's pages (#174, core contract 10).
///
/// The core keeps its own per-page state right — an open page handle follows its page,
/// and so do that page's redaction marks, layout verdicts and detached objects. What it
/// cannot see is the *app's* index-keyed state: the page list, the render caches, the
/// thumbnail strip, the once-per-page #139 warnings, the selection and the search hits.
/// Contract 10 says in so many words that the app renumbers those itself after each call.
///
/// This is that renumbering, written once for both desktops rather than twice. Every
/// operation in <see cref="Editing.IPageStructureOperation"/> reports one, forwards and
/// (through <see cref="Inverse"/>) for its undo, so a view model has one thing to apply
/// and no arithmetic of its own to get wrong.
///
/// The one rule worth stating: <see cref="Map"/> answers null for a page that is no longer
/// there. A caller that silently treats null as 0 would move a deleted page's state onto
/// the first page, which is the class of bug this type exists to prevent.
/// </summary>
public readonly record struct PageShift
{
    private PageShift(PageShiftKind kind, int at, int to, int count)
    {
        Kind = kind;
        At = at;
        To = to;
        Count = count;
    }

    public PageShiftKind Kind { get; }

    /// <summary>The page the operation acted on: the rotated, removed, inserted-at or moved-from index.</summary>
    public int At { get; }

    /// <summary>Where a moved page ended up; equal to <see cref="At"/> otherwise.</summary>
    public int To { get; }

    /// <summary>How many pages the operation removed or inserted; 1 for the rest.</summary>
    public int Count { get; }

    /// <summary>Nothing changed — not even a page's content.</summary>
    public static PageShift None { get; } = new(PageShiftKind.None, 0, 0, 0);

    /// <summary>A page changed but no index did: a rotation. The page still needs re-rendering.</summary>
    public static PageShift InPlace(int at) => new(PageShiftKind.InPlace, at, at, 1);

    /// <summary><paramref name="count"/> pages from <paramref name="at"/> left the document.</summary>
    public static PageShift Removed(int at, int count = 1) => new(PageShiftKind.Removed, at, at, count);

    /// <summary><paramref name="count"/> pages arrived at <paramref name="at"/>, pushing the rest down.</summary>
    public static PageShift Inserted(int at, int count = 1) => new(PageShiftKind.Inserted, at, at, count);

    /// <summary>The page at <paramref name="from"/> now stands at <paramref name="to"/>.</summary>
    public static PageShift Moved(int from, int to) => new(PageShiftKind.Moved, from, to, 1);

    /// <summary>Whether any index moved (a rotation does not).</summary>
    public bool Renumbers => Kind is PageShiftKind.Removed or PageShiftKind.Inserted or PageShiftKind.Moved;

    /// <summary>
    /// Where the page that stood at <paramref name="oldIndex"/> stands now, or null when it
    /// is no longer in the document. An index that was never a page is returned unchanged;
    /// this maps, it does not validate.
    /// </summary>
    public int? Map(int oldIndex) => Kind switch
    {
        PageShiftKind.Removed when oldIndex >= At && oldIndex < At + Count => null,
        PageShiftKind.Removed when oldIndex >= At + Count => oldIndex - Count,
        PageShiftKind.Inserted when oldIndex >= At => oldIndex + Count,
        PageShiftKind.Moved when oldIndex == At => To,
        PageShiftKind.Moved when At < To && oldIndex > At && oldIndex <= To => oldIndex - 1,
        PageShiftKind.Moved when At > To && oldIndex >= To && oldIndex < At => oldIndex + 1,
        _ => oldIndex,
    };

    /// <summary>The indices the operation put pages at, which have no "before" to map from.</summary>
    public IEnumerable<int> AddedIndices =>
        Kind == PageShiftKind.Inserted ? Enumerable.Range(At, Count) : [];

    /// <summary>The indices the operation took pages from, which map to nothing.</summary>
    public IEnumerable<int> RemovedIndices =>
        Kind == PageShiftKind.Removed ? Enumerable.Range(At, Count) : [];

    /// <summary>
    /// The shift that undoes this one. Exact for every operation in contract 10, because
    /// each has an inverse in the same contract: a delete is undone by putting the page
    /// back where it was, an import of n pages by taking n pages off the same index.
    /// </summary>
    public PageShift Inverse => Kind switch
    {
        PageShiftKind.Removed => Inserted(At, Count),
        PageShiftKind.Inserted => Removed(At, Count),
        PageShiftKind.Moved => Moved(To, At),
        _ => this,
    };

    /// <summary>
    /// <paramref name="pages"/> renumbered, with the pages that are gone dropped. For the
    /// app's index-keyed sets: settled #139 pages, selected thumbnails, pages with search hits.
    /// </summary>
    public IEnumerable<int> Remap(IEnumerable<int> pages)
    {
        foreach (var page in pages)
        {
            if (Map(page) is { } now)
                yield return now;
        }
    }

    /// <summary>
    /// A sequence of shifts as one map: each index carried through them in order, or null once
    /// one of them drops it. Deleting a selection of pages is several shifts, because the
    /// selection need not be contiguous.
    /// </summary>
    public static int? Map(IReadOnlyList<PageShift> shifts, int oldIndex)
    {
        int? index = oldIndex;
        foreach (var shift in shifts)
        {
            if (index is not { } current)
                return null;
            index = shift.Map(current);
        }
        return index;
    }

    /// <summary>
    /// The sequence that undoes <paramref name="shifts"/>: each one inverted, in the opposite
    /// order. What a view model applies after an Undo.
    /// </summary>
    public static IReadOnlyList<PageShift> Invert(IReadOnlyList<PageShift> shifts) =>
        shifts.Reverse().Select(s => s.Inverse).ToList();
}

/// <summary>What a <see cref="PageShift"/> did. See each factory on <see cref="PageShift"/>.</summary>
public enum PageShiftKind
{
    None,
    InPlace,
    Removed,
    Inserted,
    Moved,
}

/// <summary>
/// A page taken off the document and kept alive so an undo can put back exactly what was
/// deleted (contract 10's <c>megapdf_removed_page</c>, the page-level twin of
/// <see cref="DetachedTextRun"/>).
///
/// The core owns the page: <see cref="IPdfDocument.RestorePage"/> consumes the handle,
/// <see cref="IPdfDocument.DiscardRemovedPage"/> frees it, and closing the document frees
/// any still held — which is what bounds this when an undo stack drops an operation past
/// its 500-step capacity without ever discarding the page it was holding.
/// </summary>
public sealed class RemovedPage
{
    internal RemovedPage(IntPtr handle) => Handle = handle;

    internal IntPtr Handle { get; private set; }

    /// <summary>Whether the page is still held: false once it has been restored or discarded.</summary>
    public bool IsHeld => Handle != IntPtr.Zero;

    /// <summary>Called by the engine once the core has taken the handle.</summary>
    internal void Released() => Handle = IntPtr.Zero;
}

/// <summary>Why a page operation could not be performed (#174, contract 10).</summary>
public enum PageToolFailure
{
    /// <summary>A page index outside the document, or an empty page list where one was needed.</summary>
    OutOfRange,

    /// <summary>
    /// The document must keep a page: a PDF with none is not a PDF. Separated from
    /// <see cref="OutOfRange"/> because it is the one refusal a person can act on — remove
    /// the other pages first, or extract the ones you want instead.
    /// </summary>
    LastPage,

    /// <summary>
    /// The document's security does not allow page assembly (or, for an extract, copying).
    /// Its owner password would (ADR-004).
    /// </summary>
    Restricted,

    /// <summary>
    /// The pages carry form fields in a <c>/Parent</c> hierarchy that this build's PDFium
    /// cannot carry across a page copy, so nothing was changed (<c>MEGAPDF_ERR_FIELDS</c>).
    ///
    /// Deliberate, and not a bug to route around: the alternative is a saved file whose
    /// widgets name an object that is not their parent — a form that looks right and has
    /// lost its field names. About 0.8% of a real corpus. The engine refuses the whole
    /// operation and leaves the document alone; the app has to say so in the person's words.
    /// </summary>
    FieldHierarchy,

    /// <summary>The other file could not be opened, or the new file could not be written.</summary>
    File,

    /// <summary>The other file needs a password that was not given.</summary>
    Password,

    /// <summary>An extract was cancelled; nothing was left behind.</summary>
    Cancelled,

    /// <summary>A redaction failed part-way and the document may no longer be written to.</summary>
    Redacted,

    /// <summary>PDFium refused, with no more specific reason.</summary>
    Engine,
}

/// <summary>
/// A page operation the engine refused (#174). Typed, like <see cref="TextEditException"/>,
/// so the desktops map the reason to their own words once rather than matching on a message.
/// </summary>
public sealed class PageToolException(PageToolFailure reason, string message) : Exception(message)
{
    public PageToolFailure Reason { get; } = reason;
}
