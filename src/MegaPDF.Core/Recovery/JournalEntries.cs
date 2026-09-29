using System.IO.Compression;
using System.Text.Json.Serialization;
using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Recovery;

/// <summary>
/// One recorded edit action (SDD §3.4). The journal logs the *effective* stream —
/// undo records the inverse action — so replaying the log front-to-back reproduces
/// the document state at crash time.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$op")]
[JsonDerivedType(typeof(TextEditEntry), "text")]
[JsonDerivedType(typeof(TextDeleteEntry), "textDelete")]
[JsonDerivedType(typeof(TextRestoreEntry), "textRestore")]
[JsonDerivedType(typeof(LineEditEntry), "lineEdit")]
[JsonDerivedType(typeof(LineDeleteEntry), "lineDelete")]
[JsonDerivedType(typeof(LineRestoreEntry), "lineRestore")]
[JsonDerivedType(typeof(RedactionMarkAddEntry), "redactionMarkAdd")]
[JsonDerivedType(typeof(RedactionMarkRemoveEntry), "redactionMarkRemove")]
[JsonDerivedType(typeof(RedactionMarkMoveEntry), "redactionMarkMove")]
[JsonDerivedType(typeof(RedactionMarkClearEntry), "redactionMarkClear")]
[JsonDerivedType(typeof(WhiteoutAddEntry), "whiteoutAdd")]
[JsonDerivedType(typeof(WhiteoutRemoveEntry), "whiteoutRemove")]
[JsonDerivedType(typeof(FormTextEntry), "formText")]
[JsonDerivedType(typeof(CheckToggleEntry), "checkToggle")]
[JsonDerivedType(typeof(AddMarkEntry), "addMark")]
[JsonDerivedType(typeof(RemoveStampEntry), "removeStamp")]
[JsonDerivedType(typeof(AddSignatureEntry), "addSignature")]
[JsonDerivedType(typeof(MoveStampEntry), "moveStamp")]
[JsonDerivedType(typeof(TextBoxAddEntry), "textBoxAdd")]
[JsonDerivedType(typeof(TextBoxRestyleEntry), "textBoxRestyle")]
[JsonDerivedType(typeof(MoveTextBoxEntry), "moveTextBox")]
[JsonDerivedType(typeof(MoveWhiteoutEntry), "moveWhiteout")]
[JsonDerivedType(typeof(TextBoxesAddEntry), "textBoxesAdd")]
[JsonDerivedType(typeof(TextBoxesDeleteEntry), "textBoxesDelete")]
public abstract record JournalEntry(int PageIndex);

public sealed record TextEditEntry(int PageIndex, int ObjectIndex, string NewText) : JournalEntry(PageIndex);

public sealed record TextDeleteEntry(int PageIndex, int ObjectIndex) : JournalEntry(PageIndex);

/// <summary>Undo-of-delete for replay: recreated with a standard font (best effort).</summary>
public sealed record TextRestoreEntry(
    int PageIndex, int ObjectIndex, string Text, string FontName, double FontSize,
    double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

/// <summary>One run recreated during line-level replay (standard font, best effort).</summary>
public sealed record RestoreRun(int Index, string Text, string FontName, double FontSize, double X, double Y, double Width, double Height)
{
    public static RestoreRun From(Engine.PdfTextRun run) =>
        new(run.ObjectIndex, run.Text, run.FontName, run.FontSize,
            run.Bounds.X, run.Bounds.Y, run.Bounds.Width, run.Bounds.Height);
}

/// <summary>Line edit: new text into the first run, the listed runs detached (indexes pre-recorded descending).</summary>
public sealed record LineEditEntry(int PageIndex, int FirstIndex, string NewText, int[] DetachIndexes) : JournalEntry(PageIndex);

/// <summary>Line delete: all listed runs detached (indexes pre-recorded descending).</summary>
public sealed record LineDeleteEntry(int PageIndex, int[] DetachIndexes) : JournalEntry(PageIndex);

/// <summary>
/// Undo of a line edit/delete: recreate runs ascending; FirstIndex ≥ 0 also restores that run's text.
/// Restores may include a run's hidden copies (#136), recreated like the run at their own index. With
/// FirstIndex ≥ 0 and no FirstText, the edited run is taken off first and Restores recreates it too.
/// </summary>
public sealed record LineRestoreEntry(int PageIndex, int FirstIndex, string? FirstText, RestoreRun[] Restores) : JournalEntry(PageIndex);

/// <summary>
/// A redaction mark placed (#173). A mark is not content and is never written to the file,
/// so this entry carries a rectangle and nothing else — a journal that replayed removed text
/// would be a copy of what the redaction took out, which is the one thing it must not be.
/// </summary>
public sealed record RedactionMarkAddEntry(int PageIndex, double X, double Y, double Width, double Height)
    : JournalEntry(PageIndex);

/// <summary>A redaction mark removed (#173).</summary>
public sealed record RedactionMarkRemoveEntry(int PageIndex, double X, double Y, double Width, double Height)
    : JournalEntry(PageIndex);

/// <summary>A redaction mark moved or resized (#173).</summary>
public sealed record RedactionMarkMoveEntry(int PageIndex, int MarkId, double FromX, double FromY, double FromWidth,
                                            double FromHeight, double ToX, double ToY, double ToWidth,
                                            double ToHeight) : JournalEntry(PageIndex);

/// <summary>
/// Every mark on the document dropped (#329), as one step. The rectangles travel with the
/// entry so an undo — or a replay — can put them back where they were.
/// </summary>
public sealed record RedactionMarkClearEntry(int PageIndex, MarkRect[] Marks) : JournalEntry(PageIndex);

/// <summary>One mark's page and rectangle, for <see cref="RedactionMarkClearEntry"/>.</summary>
public sealed record MarkRect(int PageIndex, double X, double Y, double Width, double Height);

public sealed record WhiteoutAddEntry(int PageIndex, double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

/// <summary>Removal is resolved by bounds at replay time (content indexes shift).</summary>
public sealed record WhiteoutRemoveEntry(int PageIndex, double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

/// <summary>
/// Whiteout move/resize (#3): resolved by the from-bounds at replay time (content
/// indexes shift), the same convention <see cref="MoveTextBoxEntry"/> uses — and for
/// the same reason: a move detaches the old rectangle and appends a fresh one
/// (MoveWhiteoutOperation, in Editing), so there is no stable index to record.
/// </summary>
public sealed record MoveWhiteoutEntry(
    int PageIndex, double FromX, double FromY, double FromWidth, double FromHeight,
    double ToX, double ToY, double ToWidth, double ToHeight) : JournalEntry(PageIndex);

public sealed record FormTextEntry(int PageIndex, string FieldName, string NewValue) : JournalEntry(PageIndex);

public sealed record CheckToggleEntry(int PageIndex, string FieldName) : JournalEntry(PageIndex);

public sealed record AddMarkEntry(int PageIndex, double X, double Y, double Width, double Height, string StampId, string Style = "Cross") : JournalEntry(PageIndex);

public sealed record RemoveStampEntry(int PageIndex, string StampId) : JournalEntry(PageIndex);

public sealed record MoveStampEntry(int PageIndex, string StampId, double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

/// <summary>
/// Text-box add: replayed through AppendTextBox so the box keeps its movable tag.
/// <c>FontName</c> is optional so a journal written before #43 still replays — those
/// boxes are all Helvetica, which is the default.
/// </summary>
public sealed record TextBoxAddEntry(int PageIndex, string Text, double FontSize, double X, double Y, string FontName = StandardTextBoxFonts.Default) : JournalEntry(PageIndex);

/// <summary>
/// A Shift+Enter note of more than one line (#4): replayed the same way as
/// <see cref="TextBoxAddEntry"/>, one AppendTextBox per line, top to bottom at the
/// face's own line height (<c>AddTextBoxesOperation.LineHeightFactor</c>).
/// </summary>
public sealed record TextBoxesAddEntry(int PageIndex, string[] Lines, double FontSize, double X, double Y,
                                       string FontName = StandardTextBoxFonts.Default) : JournalEntry(PageIndex);

/// <summary>
/// Undo of a multi-line note: every line's object index, recorded together because they
/// were made together — an undo replay detaches them all as the one step that placed
/// them (raw indexes, the same convention <see cref="TextDeleteEntry"/> uses, are valid
/// here because replay reconstructs them by re-running every earlier entry in order).
/// </summary>
public sealed record TextBoxesDeleteEntry(int PageIndex, int[] ObjectIndexes) : JournalEntry(PageIndex);

/// <summary>
/// Text-box restyle (#43): replayed by detaching whatever sits at the index and
/// inserting the described box in its place, under the same id.
/// </summary>
public sealed record TextBoxRestyleEntry(
    int PageIndex, int ObjectIndex, string Text, string FontName, double FontSize,
    double AnchorX, double AnchorY, string Id) : JournalEntry(PageIndex);

/// <summary>Text-box move: resolved by the from-bounds at replay time (content indexes shift).</summary>
public sealed record MoveTextBoxEntry(
    int PageIndex, double FromX, double FromY, double FromWidth, double FromHeight,
    double ToX, double ToY, double ToWidth, double ToHeight) : JournalEntry(PageIndex);

public sealed record AddSignatureEntry(
    int PageIndex, double X, double Y, double Width, double Height, string StampId,
    string PixelsDeflated, int PixelWidth, int PixelHeight) : JournalEntry(PageIndex);

/// <summary>Deflate+base64 packing for image payloads in journal entries.</summary>
public static class JournalBlob
{
    public static string Pack(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    public static byte[] Unpack(string packed)
    {
        using var input = new MemoryStream(Convert.FromBase64String(packed));
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
