namespace MegaPDF.Core.Engine;

/// <summary>
/// The single seam to the underlying PDF library (SDD §4.2, §4.3).
/// Nothing outside the engine adapter may reference PDFium directly.
/// </summary>
public interface IPdfEngine : IDisposable
{
    /// <summary>Opens a document; <paramref name="password"/> for protected files.
    /// Throws <see cref="Pdfium.PdfLoadException"/> with a password error code when one is required/wrong.</summary>
    IPdfDocument Open(string filePath, string? password = null);

    /// <summary>Opens a saved copy of <paramref name="like"/> with the credentials it was opened
    /// with (#132): a copy of a protected document is still protected, so reading it back needs
    /// the same password. Throws <see cref="Pdfium.PdfLoadException"/> when it does not open.</summary>
    IPdfDocument OpenLike(IPdfDocument like, string filePath);
}

public interface IPdfDocument : IDisposable
{
    int PageCount { get; }

    IPdfPage GetPage(int pageIndex);

    /// <summary>
    /// Writes the document to <paramref name="target"/> as a full rewrite. SDD §3.4
    /// asks for an incremental (append-only) update where the engine supports it;
    /// PDFium's incremental mode appends every object the document has loaded rather
    /// than the ones that changed, which after the open-time size pass is the whole
    /// document, so it roughly doubles the file (#97, measured on the corpus). Until
    /// there is a writer with change tracking, the rewrite is the smaller output.
    /// Callers own atomicity — see <see cref="Services.AtomicFileWriter"/>.
    /// </summary>
    void Save(Stream target);

    /// <summary>Whether the document is encrypted and what this open may do (#131).</summary>
    PdfSecurity Security { get; }

    /// <summary>
    /// Writes a copy encrypted with AES-256 under new passwords, in place of any security
    /// the document had (#131). <paramref name="userPassword"/> opens the copy with
    /// <paramref name="permissions"/>; <paramref name="ownerPassword"/> opens it with all of
    /// them, and null means the same as the user password. The copy no longer opens like
    /// this document — verify it with the new password. Throws
    /// <see cref="DocumentRestrictedException"/> unless <see cref="PdfSecurity.HasFullAccess"/>.
    /// </summary>
    void SaveWithSecurity(Stream target, string userPassword, string? ownerPassword, PdfPermissions permissions);

    /// <summary>
    /// Writes a copy with no security (#131). Throws <see cref="DocumentRestrictedException"/>
    /// unless <see cref="PdfSecurity.HasFullAccess"/>.
    /// </summary>
    void SaveWithoutSecurity(Stream target);

    /// <summary>
    /// Bakes marks, signatures, and form values permanently into page content
    /// (SDD §3.3 "flatten on save", off by default). Irreversible: annotations
    /// and fields stop being interactive afterwards.
    /// </summary>
    void FlattenAllPages();

    /// <summary>
    /// How many redaction marks the whole document carries (#173) — what the save
    /// confirmation asks before it offers "Save as a copy".
    /// </summary>
    int RedactionMarkCount { get; }

    /// <summary>Drops every redaction mark on the document.</summary>
    void ClearRedactionMarks();

    /// <summary>
    /// Applies every redaction mark, then drops them. Needs the modify permission
    /// (ADR-004 decision 2): throws <see cref="DocumentRestrictedException"/> otherwise.
    ///
    /// It fails closed. The work is planned read-only, rehearsed on a copy of each marked
    /// page with the #118 guard, and only then carried out. A refusal at either of the
    /// first two stages returns a report with <see cref="RedactionReport.Applied"/> false
    /// and leaves the document untouched, with its marks still on it. A failure part-way
    /// through — which the rehearsal says cannot happen — throws
    /// <see cref="RedactionFailedException"/>, and the document is poisoned: every save on
    /// it fails from then on, so a half-redacted file can never be written.
    ///
    /// Applying also discards every undo handle the document holds: those keep removed
    /// objects alive so an undo can put them back, which after a redaction is precisely
    /// what must not happen. Callers drop their undo stacks and rewrite the recovery
    /// journal in the same step (#145).
    /// </summary>
    RedactionReport ApplyRedactions();

    /// <summary>
    /// True when a redaction failed part-way and the document may no longer be saved.
    /// It can only be closed and reopened from disk.
    /// </summary>
    bool IsRedactionPoisoned { get; }

    /// <summary>All raster images in the document, with stored and displayed sizes.</summary>
    IReadOnlyList<PdfImageInfo> GetImages();

    /// <summary>Renders an image at the requested pixel size (BGRA, masks applied).</summary>
    StampImage RenderImageAt(PdfImageInfo image, int targetWidth, int targetHeight);

    /// <summary>Swaps an image's stream for the given JPEG bytes (shrink-for-email).</summary>
    void ReplaceImageWithJpeg(PdfImageInfo image, byte[] jpegBytes);

    /// <summary>
    /// Whether this document reads the file at <paramref name="filePath"/> (the same file, not
    /// the same name). A document is read from its file on demand for as long as it is open
    /// (#147): replacing that file (<see cref="Services.AtomicFileWriter"/>) is safe, writing
    /// it in place is not, until <see cref="ReadFromCopy"/> has moved the document off it.
    /// </summary>
    bool ReadsFile(string filePath);

    /// <summary>
    /// Moves the document onto a private copy of the file it reads, so that file can be
    /// written in place — the macOS sandbox's only way to save (#147). A clone on APFS, which
    /// costs no time or space; elsewhere a copy in the temp folder. The copy leaves no name
    /// behind and is freed with the document. Throws <see cref="IOException"/> when the copy
    /// cannot be made, and the document still reads its file.
    /// </summary>
    void ReadFromCopy();

    /// <summary>
    /// Exports the document's text over <paramref name="firstPage"/>..<paramref name="pageCount"/>
    /// (default: the whole document) as plain text, over contract 9's inferred blocks (#142, #355).
    /// One-way: this is a text export, not an alternate save format (#386) — see
    /// <see cref="DocumentWriteOptions"/> for what "the app UI would want" means here versus the
    /// CLI's own scripting-flag defaults. Returns the count of pages in the range that had a text
    /// layer (a page with none becomes a single "[Page N has no text layer]" line).
    /// </summary>
    int WriteText(Stream target, int firstPage = 0, int? pageCount = null, DocumentWriteOptions? options = null);

    /// <summary>
    /// <see cref="WriteText"/>, as CommonMark (#357) — the format #386's Save As/Save a copy
    /// actually needs. Headings, list items and form fields become Markdown syntax; spans render
    /// as bold/italic/monospace. Same one-way-export caveat as <see cref="WriteText"/>.
    /// </summary>
    int WriteMarkdown(Stream target, int firstPage = 0, int? pageCount = null, DocumentWriteOptions? options = null);
}

/// <summary>How pages are separated in a <see cref="IPdfDocument.WriteText"/>/<c>WriteMarkdown</c> export.
/// Mirrors MEGAPDF_PAGE_BREAK_* (megapdf_core.h).</summary>
public enum DocumentPageBreak
{
    /// <summary>U+000C between pages — pdftotext's own convention, and megapdf-cli's default.</summary>
    FormFeed = 0,
    /// <summary>"--- page N ---" (text) / "&lt;!-- page N --&gt;" (Markdown) lines.</summary>
    Marker = 1,
    /// <summary>No separator beyond the blank line that already separates any two blocks.</summary>
    None = 2,
}

/// <summary>Which FIELD blocks (contract 9) a text/Markdown export includes. Mirrors MEGAPDF_WRITE_FIELDS_*.</summary>
public enum DocumentWriteFields
{
    /// <summary>A checked box, or a text field with a value.</summary>
    Filled = 0,
    /// <summary>Every field: empty text fields and unchecked boxes too.</summary>
    All = 1,
    /// <summary>No FIELD blocks: page text only.</summary>
    None = 2,
}

/// <summary>
/// Options for <see cref="IPdfDocument.WriteText"/>/<c>WriteMarkdown</c> (#386). The defaults here
/// are the "someone tapped Save As in the app" ones, not megapdf-cli's own scripting-flag defaults
/// (core/cli/megapdf_cli.cpp): a GUI export has no flags, so the choice is made once, here, and is
/// still overridable per call.
///
/// <see cref="Fields"/> = Filled and <see cref="KeepFurniture"/> = false match the CLI's own
/// defaults (and contract 9's), which are already right for "export what I'm looking at" (SDD
/// §3.9). <see cref="PageBreak"/> departs from the CLI's form-feed default: a form feed is a
/// terminal/pdftotext convention that a document opened in a text editor or a Markdown viewer
/// mostly renders as nothing at all — the two pages' text runs together with no visible break —
/// and MEGAPDF_PAGE_BREAK_NONE (a blank line, contract 9's "between any two blocks" rule) already
/// separates pages visibly in both formats. In Markdown output this makes no difference either
/// way: megapdf_write_text.cpp's own Markdown writer already renders form-feed and none
/// identically (a blank line — neither a form feed nor "--- page N ---" belongs in CommonMark).
/// </summary>
public sealed record DocumentWriteOptions
{
    /// <summary>Keep the PDF's own line breaks inside a block, on a best-effort basis. Default false: unwrap to one line.</summary>
    public bool KeepLines { get; init; }

    /// <summary>Default <see cref="DocumentPageBreak.None"/> — see the type's own doc comment for why.</summary>
    public DocumentPageBreak PageBreak { get; init; } = DocumentPageBreak.None;

    /// <summary>Keep running headers/footers/page numbers as blocks. Default false: they are dropped.</summary>
    public bool KeepFurniture { get; init; }

    /// <summary>Default <see cref="DocumentWriteFields.Filled"/>: a checked box, or a text field with a value.</summary>
    public DocumentWriteFields Fields { get; init; } = DocumentWriteFields.Filled;

    /// <summary>Ignore the document's structure tree even when present (measurement/testing only). Default false.</summary>
    public bool HeuristicOnly { get; init; }

    /// <summary>The GUI Save As/Save a copy defaults (#386). Equivalent to <c>new()</c>; named for callers that want to be explicit.</summary>
    public static readonly DocumentWriteOptions Default = new();
}

public interface IPdfPage : IDisposable
{
    int Index { get; }

    /// <summary>Page size in PDF points.</summary>
    double Width { get; }
    double Height { get; }

    /// <summary>Renders the page to 32-bit BGRA at the given pixel size.</summary>
    RenderedPage Render(int pixelWidth, int pixelHeight);

    /// <summary>What is under this point? Drives cursor affordances and click routing (SDD §2.2).</summary>
    PageHit HitTest(PdfPoint point);

    IReadOnlyList<PdfTextRun> GetTextRuns();

    /// <summary>
    /// Case-insensitive substring search on this page (issue #26 simple search —
    /// no whole-word, no regex). Matches in reading order; each match carries the
    /// rectangles that cover it in top-left page space (several when it wraps lines).
    /// </summary>
    IReadOnlyList<PdfSearchMatch> FindText(string term);

    /// <summary>Text runs assembled into visual lines (same baseline, no column-wide gaps).</summary>
    IReadOnlyList<PdfTextLine> GetTextLines();

    IReadOnlyList<PdfFormField> GetFormFields();

    /// <summary>
    /// Heuristic detection of drawn (non-form) checkbox squares: small, roughly
    /// square, stroked-not-filled paths (SDD §3.2). Results in top-left page space.
    /// </summary>
    IReadOnlyList<PdfRect> DetectCheckboxSquares();

    /// <summary>
    /// Places the mark stamp over a drawn square; returns the stamp id (SDD §3.2).
    /// Pass <paramref name="stampId"/> to restore a previously removed stamp under its
    /// original id — ids must stay stable across undo/redo cycles.
    /// </summary>
    string AddCheckMarkStamp(PdfRect squareBounds, string? stampId = null, CheckMarkStyle style = CheckMarkStyle.Cross);

    /// <summary>Tiered body-text edit — see SDD §3.1. Throws <see cref="TextEditException"/> per tier rules.</summary>
    TextEditOutcome SetTextRunText(PdfTextRun run, string newText);

    /// <summary>
    /// As <see cref="SetTextRunText(PdfTextRun, string)"/>, handing back the untouched
    /// original run, with any hidden copy drawn under it (#136), so an undo can put them back
    /// byte-identical with <see cref="RestoreOriginalTextRun"/> — whichever tier the edit took (#117).
    /// </summary>
    TextEditOutcome SetTextRunText(PdfTextRun run, string newText, out DetachedTextRun original);

    /// <summary>
    /// Undoes <see cref="SetTextRunText(PdfTextRun, string, out DetachedTextRun)"/>: takes
    /// the edited run at <paramref name="objectIndex"/> off the page and puts the original back.
    /// </summary>
    void RestoreOriginalTextRun(DetachedTextRun original, int objectIndex);

    /// <summary>
    /// Retypes a visual line (#136): the new text goes into the first of <paramref name="runs"/>,
    /// the others leave the page, and so do the hidden copies a producer drew under any of them
    /// (fake bold, outlines, shadows), all at once. Everything taken comes back in
    /// <paramref name="originals"/>; <see cref="RestoreDetached"/> undoes the edit byte-identical.
    /// </summary>
    TextEditOutcome SetLineText(IReadOnlyList<PdfTextRun> runs, string newText, out DetachedTextRun originals);

    /// <summary>
    /// Removes a visual line's runs with the hidden copies drawn under them (#136), all at once;
    /// <see cref="RestoreDetached"/> puts every object back where it was.
    /// </summary>
    DetachedTextRun DetachTextRuns(IReadOnlyList<PdfTextRun> runs);

    /// <summary>
    /// Undoes <see cref="SetLineText"/>, <see cref="DetachTextRuns"/> or
    /// <see cref="DetachTextRun"/>: every object the handle holds goes back at its own index,
    /// after the edited run is taken off. Later edits on the page must be undone first.
    /// </summary>
    void RestoreDetached(DetachedTextRun detached);

    /// <summary>
    /// Whether the text object at <paramref name="objectIndex"/> can be edited or removed
    /// without PDFium changing anything else about the page when it rewrites the content
    /// stream (#118). Ask before opening an editor; the edit itself refuses the same way.
    /// </summary>
    bool IsTextEditable(int objectIndex);

    /// <summary>
    /// <see cref="IsTextEditable"/> with its reason (#128): which check refused, how many
    /// pixels would change and where. The same cached dry run, so asking both costs one.
    /// Null when the object is not text, which <see cref="IsTextEditable"/> answers with false.
    /// </summary>
    LayoutVerdict? GetLayoutVerdict(int objectIndex);

    /// <summary>
    /// Whether regenerating this page's content, with nothing changed, would change how it
    /// looks (#139). Whiteouts, text boxes and removing objects regenerate the page too and are
    /// never refused; ask before the first such change to warn when <see cref="LayoutVerdict.Editable"/>
    /// is false. The same dry run and budgets as <see cref="GetLayoutVerdict"/>, cached per page
    /// until the page next changes. Slow on a heavy page the first time: call it off the UI thread.
    /// </summary>
    LayoutVerdict GetPageRegenerationVerdict();

    /// <summary>
    /// <see cref="GetPageRegenerationVerdict()"/> for a check started early, in the background
    /// (#145). The core lets other calls run between the dry run's stages, so rendering and edits
    /// wait one stage rather than the whole run. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is cancelled, or the document is disposed, before
    /// the check has answered.
    /// </summary>
    LayoutVerdict GetPageRegenerationVerdict(CancellationToken cancellationToken);

    /// <summary>The page's cached verdict, without running anything; null when it has not been judged since it last changed (#145).</summary>
    LayoutVerdict? GetCachedPageRegenerationVerdict();

    /// <summary>
    /// Removes a text run from the page, keeping the native object alive so
    /// <see cref="RestoreTextRun"/> can put it back byte-identical (undo).
    /// </summary>
    DetachedTextRun DetachTextRun(PdfTextRun run);

    /// <summary>Re-inserts a detached run at its original object index.</summary>
    void RestoreTextRun(DetachedTextRun detached, int objectIndex);

    /// <summary>
    /// Recreates a deleted run from recorded properties (crash-recovery replay only —
    /// uses the closest standard font, not the original).
    /// </summary>
    void InsertTextRun(int objectIndex, string text, string fontName, double fontSize, PdfRect bounds);

    /// <summary>
    /// Appends a white filled rectangle to the page CONTENT (not an annotation), so it
    /// covers everything drawn before it — text and images alike. Tagged with a content
    /// mark so it stays identifiable. Returns its object index.
    /// </summary>
    int AppendWhiteout(PdfRect bounds);

    /// <summary>MegaPDF whiteout rectangles on this page (object index + bounds).</summary>
    IReadOnlyList<(int ObjectIndex, PdfRect Bounds)> GetWhiteouts();

    /// <summary>
    /// Marks an area for redaction (#173) and returns the mark's id. Nothing on the page
    /// changes — a mark is the core's own and is never written to the file, so the apps
    /// draw it in their overlay layer. Marking is free: no content regeneration, no
    /// invalidated layout verdict.
    /// </summary>
    int MarkForRedaction(PdfRect bounds);

    /// <summary>
    /// Marks the text a drag selected: one mark per line the selection spans, each grown to
    /// the glyphs it touches so a mark always covers whole glyphs. Empty when the selection
    /// covers no text, and the caller then marks the rectangle itself.
    /// </summary>
    IReadOnlyList<int> MarkTextForRedaction(PdfRect selection);

    /// <summary>The redaction marks on this page, in the order they were made.</summary>
    IReadOnlyList<RedactionMark> GetRedactionMarks();

    /// <summary>Moves or resizes a mark. False when the page has no such mark.</summary>
    bool MoveRedactionMark(int markId, PdfRect bounds);

    /// <summary>Removes a mark. Already gone counts as success, so an undo cannot fail.</summary>
    void RemoveRedactionMark(int markId);

    /// <summary>
    /// Appends new standard-font text at the given top-left position (a "text box").
    /// Appended after any whiteout, so it renders above one. Returns its object index.
    /// The object is tagged so it stays identifiable as a MegaPDF text box (movable,
    /// SDD §3.3-style) even though it is otherwise a normal, editable text run.
    /// <paramref name="fontName"/> must be one of <see cref="StandardTextBoxFonts"/>;
    /// the face is recorded on the mark so every platform reads back exactly what was
    /// chosen (#43). Optional so existing callers keep the pre-#43 default.
    /// </summary>
    int AppendTextBox(string text, double fontSize, PdfPoint topLeft,
                      string fontName = StandardTextBoxFonts.Default);

    /// <summary>MegaPDF-added text boxes on this page, as their underlying text runs.</summary>
    IReadOnlyList<PdfTextRun> GetTextBoxes();

    /// <summary>
    /// Repositions a text box (drag/nudge, SDD §3.3) to <paramref name="newBounds"/> by
    /// translating its object in place — the object index stays stable, so selection and
    /// undo references survive the move.
    /// </summary>
    void MoveTextBox(int objectIndex, PdfRect newBounds);

    /// <summary>Detaches any page object by index (kept alive for undo), regardless of type.</summary>
    DetachedTextRun DetachObjectAt(int objectIndex);

    /// <summary>
    /// Inserts a MegaPDF text box at <paramref name="objectIndex"/> in the given face and
    /// size, tagged with <paramref name="id"/> so it keeps its identity across a restyle
    /// (SDD §6.2 contract 4) — that id is the handle the mobile apps address it by.
    ///
    /// Positioned so the box's *bounds* bottom-left lands on <paramref name="anchor"/>
    /// (page space, so a larger Y is lower down). Deliberately not the baseline: a bigger
    /// face has a deeper descender, so a restyle anchored on the baseline would sink the
    /// box through the printed rule it sits on. Same trap the mobile engines document.
    /// </summary>
    void InsertStyledTextBox(int objectIndex, string text, string fontName, double fontSize,
                             PdfPoint anchor, string id);

    void SetFormFieldValue(PdfFormField field, string value);
    void ToggleCheckbox(PdfFormField field);

    /// <summary>Places a BGRA image (alpha respected) as a stamp annotation — a signature (SDD §3.3).</summary>
    string AddImageStamp(ReadOnlyMemory<byte> bgra, int pixelWidth, int pixelHeight, PdfRect bounds, string? stampId = null);

    /// <summary>Reads back a placed image stamp's pixels, e.g. to make removal undoable.</summary>
    StampImage? GetStampImage(string annotationId);

    /// <summary>Moves/resizes a placed image stamp (signature drag/resize, SDD §3.3).</summary>
    void MoveStampAnnotation(string annotationId, PdfRect newBounds);

    /// <summary>All MegaPDF-placed stamps (marks and signatures) on this page.</summary>
    IReadOnlyList<StampInfo> GetStamps();

    void RemoveStampAnnotation(string annotationId);
}

/// <summary>A rendered page bitmap: 32-bit BGRA, top-down rows.</summary>
public sealed record RenderedPage(int PixelWidth, int PixelHeight, byte[] Bgra);

/// <summary>Pixels of a placed image stamp (BGRA).</summary>
public sealed record StampImage(byte[] Bgra, int PixelWidth, int PixelHeight);

/// <summary>A raster image in the document: where it is, its stored resolution and byte size,
/// and how large it displays (points).</summary>
public sealed record PdfImageInfo(
    int PageIndex, int ObjectIndex, int PixelWidth, int PixelHeight,
    double DisplayWidthPoints, double DisplayHeightPoints, long StoredByteLength);

/// <summary>A MegaPDF-placed stamp: id (mark:/sig: prefixed) and bounds in top-left page space.</summary>
public sealed record StampInfo(string Id, PdfRect Bounds);

/// <summary>Check-mark styles (SDD §3.2 + Appendix B #3: ✗ default, ✓ and regional ■).</summary>
public enum CheckMarkStyle
{
    Cross,
    Check,
    FilledSquare,
}

/// <summary>
/// Opaque handle to page objects removed from their page but kept alive for undo: one object,
/// or a line's runs with the hidden copies drawn under them (#136).
/// </summary>
public sealed class DetachedTextRun
{
    internal DetachedTextRun(IntPtr handle, IReadOnlyList<DetachedPart> parts)
    {
        Handle = handle;
        Parts = parts;
    }

    internal IntPtr Handle { get; }

    /// <summary>What the handle holds, ascending by the object index each had (and goes back to).</summary>
    public IReadOnlyList<DetachedPart> Parts { get; }
}

/// <summary>
/// One object in a <see cref="DetachedTextRun"/>: where it stood, and for a hidden copy of a
/// run (#136) the object index of that run; <see cref="CopyOf"/> is -1 for a run itself.
/// </summary>
public sealed record DetachedPart(int ObjectIndex, int CopyOf);

public enum PageHitKind
{
    None,
    TextRun,
    FormTextField,
    FormCheckbox,

    /// <summary>A drawn (non-form) square that reads as a checkbox — SDD §3.2.</summary>
    DrawnCheckbox,

    /// <summary>A MegaPDF-placed stamp (check mark or signature).</summary>
    StampAnnotation,

    /// <summary>A MegaPDF whiteout rectangle (page content, covers what's beneath).</summary>
    Whiteout,

    /// <summary>A MegaPDF-added text box — selects for move/nudge/delete; double-click edits.</summary>
    TextBox,
}

public sealed record PageHit(
    PageHitKind Kind,
    PdfTextRun? TextRun = null,
    PdfFormField? Field = null,
    string? AnnotationId = null,
    PdfRect? Bounds = null,
    PdfTextLine? TextLine = null,
    int? ObjectIndex = null);

/// <summary>A contiguous run of body text sharing one font/size/color.</summary>
/// <param name="TextBoxId">
/// For MegaPDF text boxes, the `id` carried by the object's MegaPDFTextBox mark
/// (SDD §6.2 contract 4) — how the mobile apps address a box, since page-object
/// indices shift. Null for ordinary body text, and for boxes written before the
/// param existed.
/// </param>
public sealed record PdfTextRun(int ObjectIndex, string Text, PdfRect Bounds, string FontName, double FontSize, string? TextBoxId = null, string? TextBoxFont = null);

/// <summary>
/// The base-14 faces an added text box may be written in (#43).
///
/// Three, not fourteen: SDD §3.1 keeps formatting controls out of the app, and a
/// choice between serif, sans and monospace is what "make this match the form I am
/// filling in" actually needs. These are the exact names FPDFText_LoadStandardFont
/// takes, so nothing has to be mapped.
/// </summary>
public static class StandardTextBoxFonts
{
    public const string Sans = "Helvetica";
    public const string Serif = "Times-Roman";
    public const string Mono = "Courier";

    /// <summary>What a box with no recorded face is, and what a new one defaults to.</summary>
    public const string Default = Sans;

    public static readonly IReadOnlyList<string> All = [Sans, Serif, Mono];

    public static bool IsSupported(string fontName) => All.Contains(fontName);
}

/// <summary>One search hit: the rectangles covering it, in top-left page space.</summary>
public sealed record PdfSearchMatch(IReadOnlyList<PdfRect> Rects);

/// <summary>
/// A visual line: adjacent same-baseline runs merged so the user edits what they
/// see, not the PDF's arbitrary fragmentation (1.1 paragraph-grade editing).
/// </summary>
public sealed record PdfTextLine(IReadOnlyList<PdfTextRun> Runs, string Text, PdfRect Bounds, string FontName, double FontSize);

public enum FormFieldKind
{
    Text,
    Checkbox,
    RadioButton,
    Other,
}

public sealed record PdfFormField(string Name, FormFieldKind Kind, PdfRect Bounds, string Value, bool IsChecked = false);

/// <summary>How a body-text edit was performed (SDD §3.1 tiers 1 and 2).</summary>
public enum TextEditOutcome
{
    /// <summary>Tier 1: the document's own font rendered the new text.</summary>
    EditedInPlace,

    /// <summary>Tier 2: the font couldn't cover the new text; a similar standard font was used.</summary>
    EditedWithSubstitutedFont,
}

/// <summary>Why a body-text edit could not be performed (SDD §3.1 tier rules).</summary>
public sealed class TextEditException(TextEditFailure reason, string message, LayoutVerdict? layout = null) : Exception(message)
{
    public TextEditFailure Reason { get; } = reason;

    /// <summary>
    /// For <see cref="TextEditFailure.LayoutWouldChange"/>: the layout guard's verdict on the
    /// refused run (#128). Null for the other failures.
    /// </summary>
    public LayoutVerdict? Layout { get; } = layout;
}

/// <summary>Which layout-guard check refused an edit (#128). Mirrors MEGAPDF_LAYOUT_* in megapdf_core.h.</summary>
public enum LayoutCause
{
    /// <summary>Editable: the rewrite changed nothing past the guard's budgets.</summary>
    Ok = 0,

    /// <summary>More than 0.05% of the page's pixels would look different.</summary>
    Render = 1,

    /// <summary>Some text object's bounds would move by more than 0.5 pt.</summary>
    TextMoved = 2,

    /// <summary>The page would have a different number of text objects, or different text.</summary>
    TextChanged = 3,

    /// <summary>PDFium could not rewrite, save or reopen the copy of the page.</summary>
    RewriteFailed = 4,
}

/// <summary>Where the changed pixels are (#128). Mirrors MEGAPDF_LAYOUT_WHERE_*.</summary>
[Flags]
public enum LayoutArea
{
    None = 0,

    /// <summary>On the judged text (with its hidden copies), padded by 2 pt.</summary>
    EditedText = 1,

    /// <summary>Elsewhere, on another text object.</summary>
    OtherText = 2,

    /// <summary>Elsewhere, off every text object: images, paths, forms, shadings.</summary>
    NonText = 4,
}

/// <summary>
/// The layout guard's verdict on one text object (#118, #128). The pixel counts are from
/// the guard's own render of the page, whose size is <see cref="TotalPixels"/>;
/// <see cref="MaxShiftPoints"/> is the largest move of any text object's bounds.
/// </summary>
public sealed record LayoutVerdict(bool Editable, LayoutCause Cause, LayoutArea Where,
    int ChangedPixels, int TotalPixels, double MaxShiftPoints)
{
    /// <summary>The cause as people should hear it: text moving elsewhere, or the rest of the page looking different.</summary>
    public bool TextWouldMove => Cause is LayoutCause.TextMoved or LayoutCause.TextChanged;
}

public enum TextEditFailure
{
    /// <summary>Font lacks needed glyphs and no acceptable substitute was found (tier 2 failed).</summary>
    NoUsableFont,

    /// <summary>The text is rasterized (scanned) and cannot be edited (tier 3).</summary>
    NotExtractable,

    /// <summary>
    /// PDFium would change how the rest of the page looks if it rewrote this text: its
    /// content writer drops character and word spacing, scaling and rise (#118).
    /// </summary>
    LayoutWouldChange,
}
