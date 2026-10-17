using System.Runtime.InteropServices;
using MegaPDF.Core.Engine.Core;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #679: the hand-written mirror of <c>megapdf_block_image_info</c>. The core writes the whole C
/// struct through the pointer, so a managed struct that is shorter than the C one is overflowed;
/// the size check is what makes a field added on one side and not the other fail here, loudly,
/// instead of corrupting the stack of whichever app calls it.
/// </summary>
public class BlockImageBindingTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "structure", name + ".pdf");

    [Fact]
    public void ManagedMirrorIsSevenInts()
    {
        // core/megapdf_core.h megapdf_block_image_info: count, pixel_width, pixel_height,
        // dpi_horizontal, dpi_vertical, bits_per_pixel, colorspace.
        Assert.Equal(7 * sizeof(int), Marshal.SizeOf<CoreNative.BlockImageInfo>());
    }

    [Fact]
    public void AScannedPageReportsItsOneImageThroughTheBinding()
    {
        var doc = CoreNative.megapdf_open_file(FixturePath("scan"), null);
        Assert.NotEqual(IntPtr.Zero, doc);
        try
        {
            var s = CoreNative.megapdf_structure_load(doc, 0, 1, 0, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, s);
            try
            {
                var found = false;
                var n = (int)CoreNative.megapdf_block_count(s);
                for (var i = 0; i < n; i++)
                {
                    Assert.Equal(0, CoreNative.megapdf_block_get(s, (nuint)i, out var block));
                    Assert.Equal(0, CoreNative.megapdf_block_image(s, (nuint)i, out var info));
                    if (block.Kind != CoreNative.BlockPageImage)
                    {
                        // A block that is not a picture answers zeroes, so a caller need not check kind first.
                        Assert.Equal(0, info.Count);
                        continue;
                    }
                    found = true;
                    Assert.Equal(1, info.Count);
                    Assert.True(info.PixelWidth > 0 && info.PixelHeight > 0);
                }
                Assert.True(found, "scan.pdf should produce a PAGE_IMAGE block");

                // A bad index is refused rather than written through.
                Assert.NotEqual(0, CoreNative.megapdf_block_image(s, (nuint)n, out _));
            }
            finally { CoreNative.megapdf_structure_free(s); }
        }
        finally { CoreNative.megapdf_close(doc); }
    }
}
