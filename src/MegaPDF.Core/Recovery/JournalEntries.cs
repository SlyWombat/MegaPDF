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
[JsonDerivedType(typeof(FormTextEntry), "formText")]
[JsonDerivedType(typeof(CheckToggleEntry), "checkToggle")]
[JsonDerivedType(typeof(AddMarkEntry), "addMark")]
[JsonDerivedType(typeof(RemoveStampEntry), "removeStamp")]
[JsonDerivedType(typeof(AddSignatureEntry), "addSignature")]
public abstract record JournalEntry(int PageIndex);

public sealed record TextEditEntry(int PageIndex, int ObjectIndex, string NewText) : JournalEntry(PageIndex);

public sealed record FormTextEntry(int PageIndex, string FieldName, string NewValue) : JournalEntry(PageIndex);

public sealed record CheckToggleEntry(int PageIndex, string FieldName) : JournalEntry(PageIndex);

public sealed record AddMarkEntry(int PageIndex, double X, double Y, double Width, double Height, string StampId) : JournalEntry(PageIndex);

public sealed record RemoveStampEntry(int PageIndex, string StampId) : JournalEntry(PageIndex);

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
