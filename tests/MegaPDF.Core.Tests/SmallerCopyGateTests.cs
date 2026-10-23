using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Shrink for email must be honest about the result (#6, design step 3.5): a copy is offered only
/// if its serialised bytes are strictly below the file on disk, and otherwise nothing is saved.
///
/// The desktops used to say "Nothing to shrink" only when zero images were replaced; when some
/// were replaced they saved and reported success without ever comparing sizes, so a document whose
/// shrunk copy was bigger than the original (87 of the survey's documents grew because of shrink
/// alone) got a bigger file reported as "Smaller copy saved". These tests decide on bytes, with no
/// window and no save picker, which is the part both desktops share.
/// </summary>
public sealed class SmallerCopyGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-smaller-gate-").FullName;
    private readonly string _staging = Directory.CreateTempSubdirectory("megapdf-smaller-gate-staging-").FullName;
    private readonly PdfiumEngine _engine = new();

    public SmallerCopyGateTests() => VerifiedSave.StagingDirectoryForTests = _staging;

    public void Dispose()
    {
        VerifiedSave.StagingDirectoryForTests = null;
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_staging, recursive: true);
    }

    /// <summary>Re-encodes to 1/20th of the pixels: a genuine saving.</summary>
    private static byte[] SmallEncode(byte[] bgra, int width, int height, double quality)
        => new byte[Math.Max(1, bgra.Length / 20)];

    /// <summary>One 200x200 image drawn <paramref name="placements"/> times from the same XObject.</summary>
    private string WriteDocumentDrawingTheImage(int placements)
    {
        var content = string.Concat(Enumerable.Range(0, placements)
            .Select(i => $"q 36 0 0 36 {20 + i * 40} 400 cm /Im1 Do Q\n"));
        var pixels = new char[200 * 200 * 3];
        var seed = 0x2545F491u;
        for (var i = 0; i < pixels.Length; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            pixels[i] = (char)('!' + (seed >> 16) % 90);
        }
        var imageData = new string(pixels);
        var path = Path.Combine(_dir, $"drawn-{placements}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.Assemble(
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 200 /Height 200 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {imageData.Length} >>\nstream\n{imageData}\nendstream\nendobj\n",
        ]));
        return path;
    }

    private int StagedFiles() => Directory.GetFiles(_staging).Length;

    [Theory]
    [InlineData(99L, 100L, true)]
    [InlineData(100L, 100L, false)]   // equal is not smaller
    [InlineData(101L, 100L, false)]
    public void IsSmaller_IsStrictlyBelowTheBaseline(long copy, long baseline, bool expected) =>
        Assert.Equal(expected, SmallerCopyGate.IsSmaller(copy, baseline));

    [Fact]
    public void ACopyThatIsSmallerThanTheFileOnDisk_IsStagedAndHandedBack()
    {
        var path = WriteDocumentDrawingTheImage(1);
        var onDisk = new FileInfo(path).Length;
        using var copy = _engine.Open(path);
        Assert.Equal(1, ImageShrinker.Shrink(copy, SmallEncode).ImagesReplaced);

        using var staged = SmallerCopyGate.StageIfSmaller(_engine, copy, onDisk);

        Assert.NotNull(staged);
        Assert.True(staged.Length < onDisk, $"staged {staged.Length} bytes against {onDisk} on disk");
        Assert.True(File.Exists(staged.Path));
    }

    [Fact]
    public void ACopyThatIsExactlyTheSizeOfTheFileOnDisk_IsNotOffered_AndLeavesNothingBehind()
    {
        var path = WriteDocumentDrawingTheImage(1);
        using var copy = _engine.Open(path);
        Assert.Equal(1, ImageShrinker.Shrink(copy, SmallEncode).ImagesReplaced);
        long size;
        using (var probe = VerifiedSave.ToStagedFile(_engine, copy))
            size = probe.Length;
        Assert.Equal(0, StagedFiles());

        // The same copy against a baseline of exactly its own size: not strictly smaller.
        Assert.Null(SmallerCopyGate.StageIfSmaller(_engine, copy, size));
        Assert.Equal(0, StagedFiles());
    }

    [Fact]
    public void ADocumentWhoseImagesWereReplacedButWhoseCopyIsNotSmaller_IsNotOffered()
    {
        // The reported bug: images were replaced, so the old check ("did anything shrink?") said
        // yes, yet the copy is no smaller than what the person has. The baseline is set to one byte
        // under the copy's own size, which is what a file that is already tighter than the engine's
        // rewrite looks like (a full PDFium save drops object streams and can grow a document by
        // itself: 30 to 40 % of the survey's documents), without depending on a fixture that
        // happens to grow.
        var path = WriteDocumentDrawingTheImage(1);
        using var copy = _engine.Open(path);
        var result = ImageShrinker.Shrink(copy, SmallEncode);
        Assert.True(result.ImagesReplaced > 0, "the old test for success: something was replaced");
        long size;
        using (var probe = VerifiedSave.ToStagedFile(_engine, copy))
            size = probe.Length;

        Assert.Null(SmallerCopyGate.StageIfSmaller(_engine, copy, size - 1));
        Assert.Equal(0, StagedFiles());   // the temporary file is deleted, not left in the temp folder

        // And the very next byte of headroom is enough: the gate is the comparison and nothing else.
        using var offered = SmallerCopyGate.StageIfSmaller(_engine, copy, size + 1);
        Assert.NotNull(offered);
    }
}
