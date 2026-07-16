using System.IO.Compression;
using System.Text.Json.Serialization;

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
[JsonDerivedType(typeof(WhiteoutAddEntry), "whiteoutAdd")]
[JsonDerivedType(typeof(WhiteoutRemoveEntry), "whiteoutRemove")]
[JsonDerivedType(typeof(FormTextEntry), "formText")]
[JsonDerivedType(typeof(CheckToggleEntry), "checkToggle")]
[JsonDerivedType(typeof(AddMarkEntry), "addMark")]
[JsonDerivedType(typeof(RemoveStampEntry), "removeStamp")]
[JsonDerivedType(typeof(AddSignatureEntry), "addSignature")]
[JsonDerivedType(typeof(MoveStampEntry), "moveStamp")]
[JsonDerivedType(typeof(TextBoxAddEntry), "textBoxAdd")]
[JsonDerivedType(typeof(MoveTextBoxEntry), "moveTextBox")]
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

/// <summary>Undo of a line edit/delete: recreate runs ascending; FirstIndex ≥ 0 also restores that run's text.</summary>
public sealed record LineRestoreEntry(int PageIndex, int FirstIndex, string? FirstText, RestoreRun[] Restores) : JournalEntry(PageIndex);

public sealed record WhiteoutAddEntry(int PageIndex, double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

/// <summary>Removal is resolved by bounds at replay time (content indexes shift).</summary>
public sealed record WhiteoutRemoveEntry(int PageIndex, double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

public sealed record FormTextEntry(int PageIndex, string FieldName, string NewValue) : JournalEntry(PageIndex);

public sealed record CheckToggleEntry(int PageIndex, string FieldName) : JournalEntry(PageIndex);

public sealed record AddMarkEntry(int PageIndex, double X, double Y, double Width, double Height, string StampId, string Style = "Cross") : JournalEntry(PageIndex);

public sealed record RemoveStampEntry(int PageIndex, string StampId) : JournalEntry(PageIndex);

public sealed record MoveStampEntry(int PageIndex, string StampId, double X, double Y, double Width, double Height) : JournalEntry(PageIndex);

/// <summary>Text-box add: replayed through AppendTextBox so the box keeps its movable tag.</summary>
public sealed record TextBoxAddEntry(int PageIndex, string Text, double FontSize, double X, double Y) : JournalEntry(PageIndex);

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
