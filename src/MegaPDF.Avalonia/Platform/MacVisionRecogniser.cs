using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Find on scanned pages (#676) on the Mac: Vision's <c>VNRecognizeTextRequest</c>, in the OS, on
/// the device, with no network and nothing written anywhere. The iOS build runs the same request
/// from Swift; the Avalonia app reaches it the way it already reaches AppKit for printing
/// (<see cref="MacPrinter"/>) — hand-written <c>objc_msgSend</c> declarations, one per distinct
/// signature, because on arm64 reusing a declaration across shapes is silent corruption.
///
/// Phase 0 (#676) measured Vision at about 0.14 s a page and flat across resolution (134 ms at
/// 100 dpi, 154 ms at 300), so there is nothing to gain from rendering small; <c>.fast</c> is four
/// to ten times quicker but reads 61-92% of the characters, which for Find is silently missing
/// hits, so this asks for the accurate level and for the language to be detected per page.
///
/// A word's box is asked of Vision as <c>boundingBoxForRange:error:</c>, which answers a
/// <c>VNRectangleObservation</c>, and read through its two corner points. Not through its
/// <c>boundingBox</c>: a CGRect is 32 bytes and comes back through <c>objc_msgSend_stret</c> on
/// Intel, where a CGPoint (two doubles) comes back in registers on both architectures.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacVisionRecogniser : IPageRecogniser
{
    private const string Objc = "/usr/lib/libobjc.dylib";
    private const string Dl = "/usr/lib/libSystem.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRange { public nuint Location; public nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { public double X; public double Y; }

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg_Ptr_Ptr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void Msg_Long(IntPtr receiver, IntPtr selector, nint value);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void Msg_Bool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Msg_Perform(IntPtr receiver, IntPtr selector, IntPtr requests, ref IntPtr error);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nuint Msg_NUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg_NUInt_Arg(IntPtr receiver, IntPtr selector, nuint arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Msg_Range(IntPtr receiver, IntPtr selector, NSRange range, ref IntPtr error);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern CGPoint Msg_Point(IntPtr receiver, IntPtr selector);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Objc)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Dl)]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGDataProviderCreateWithData(IntPtr info, IntPtr data, nuint size, IntPtr release);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGImageCreate(
        nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow,
        IntPtr space, uint bitmapInfo, IntPtr provider, IntPtr decode,
        [MarshalAs(UnmanagedType.I1)] bool shouldInterpolate, int intent);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    private const int RtldNow = 2;
    /// <summary>kCGImageAlphaNoneSkipFirst | kCGBitmapByteOrder32Little: the BGRA the core renders, its alpha always opaque.</summary>
    private const uint BgraBitmapInfo = 6 | (2 << 12);

    private MacVisionRecogniser() { }

    /// <summary>The recogniser, or null when Vision is not there to ask (before macOS 10.15).</summary>
    public static IPageRecogniser? TryCreate()
    {
        try
        {
            if (dlopen("/System/Library/Frameworks/Vision.framework/Vision", RtldNow) == IntPtr.Zero)
                return null;
            return objc_getClass("VNRecognizeTextRequest") == IntPtr.Zero ? null : new MacVisionRecogniser();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"no Vision: {ex.Message}");
            return null;
        }
    }

    /// <summary>Vision has no hard ceiling the way Windows' 10,000 px is; a poster at 150 dpi is a very large bitmap for no gain.</summary>
    public int MaxPixelsPerSide => 6_000;

    public OcrPage? Read(RenderedPage image, double pageWidth, double pageHeight)
    {
        // Objects Vision hands back are autoreleased, and a pool-thread has no pool.
        var pool = objc_autoreleasePoolPush();
        var pinned = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
        IntPtr space = IntPtr.Zero, provider = IntPtr.Zero, cgImage = IntPtr.Zero;
        IntPtr request = IntPtr.Zero, handler = IntPtr.Zero;
        try
        {
            space = CGColorSpaceCreateDeviceRGB();
            provider = CGDataProviderCreateWithData(IntPtr.Zero, pinned.AddrOfPinnedObject(), (nuint)image.Bgra.Length, IntPtr.Zero);
            cgImage = CGImageCreate((nuint)image.PixelWidth, (nuint)image.PixelHeight, 8, 32,
                (nuint)(image.PixelWidth * 4), space, BgraBitmapInfo, provider, IntPtr.Zero, true, 0);
            if (cgImage == IntPtr.Zero)
                return null;

            request = Msg(Msg(objc_getClass("VNRecognizeTextRequest"), Sel("alloc")), Sel("init"));
            Msg_Long(request, Sel("setRecognitionLevel:"), 0);            // VNRequestTextRecognitionLevelAccurate
            Msg_Bool(request, Sel("setUsesLanguageCorrection:"), true);
            Msg_Bool(request, Sel("setAutomaticallyDetectsLanguage:"), true);

            var options = Msg(objc_getClass("NSDictionary"), Sel("dictionary"));
            handler = Msg_Ptr_Ptr(Msg(objc_getClass("VNImageRequestHandler"), Sel("alloc")),
                Sel("initWithCGImage:options:"), cgImage, options);
            var requests = Msg_Ptr(objc_getClass("NSArray"), Sel("arrayWithObject:"), request);
            var error = IntPtr.Zero;
            if (!Msg_Perform(handler, Sel("performRequests:error:"), requests, ref error))
                return null;

            var results = Msg(request, Sel("results"));
            var lines = new List<OcrLine>();
            var count = results == IntPtr.Zero ? 0 : (int)Msg_NUInt(results, Sel("count"));
            for (var i = 0; i < count; i++)
            {
                var observation = Msg_NUInt_Arg(results, Sel("objectAtIndex:"), (nuint)i);
                var candidates = Msg_NUInt_Arg(observation, Sel("topCandidates:"), 1);
                if (candidates == IntPtr.Zero || Msg_NUInt(candidates, Sel("count")) == 0)
                    continue;
                var candidate = Msg_NUInt_Arg(candidates, Sel("objectAtIndex:"), 0);
                var text = Marshal.PtrToStringUTF8(Msg(Msg(candidate, Sel("string")), Sel("UTF8String"))) ?? "";
                var words = WordsOf(candidate, text, pageWidth, pageHeight);
                if (words.Count > 0)
                    lines.Add(new OcrLine(text, words));
            }
            return new OcrPage(lines);
        }
        finally
        {
            if (handler != IntPtr.Zero) CFRelease(handler);
            if (request != IntPtr.Zero) CFRelease(request);
            if (cgImage != IntPtr.Zero) CFRelease(cgImage);
            if (provider != IntPtr.Zero) CFRelease(provider);
            if (space != IntPtr.Zero) CFRelease(space);
            pinned.Free();
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// One line as words with page-space boxes. Vision's boxes are normalised with a bottom-left
    /// origin; the page space <see cref="PdfRect"/> uses has its origin top-left.
    /// </summary>
    private static List<OcrWord> WordsOf(IntPtr candidate, string text, double pageWidth, double pageHeight)
    {
        var words = new List<OcrWord>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || char.IsWhiteSpace(text[i]);
            if (!space)
            {
                if (start < 0)
                    start = i;
                continue;
            }
            if (start < 0)
                continue;
            var range = new NSRange { Location = (nuint)start, Length = (nuint)(i - start) };
            var error = IntPtr.Zero;
            var box = Msg_Range(candidate, Sel("boundingBoxForRange:error:"), range, ref error);
            if (box != IntPtr.Zero)
            {
                var bottomLeft = Msg_Point(box, Sel("bottomLeft"));
                var topRight = Msg_Point(box, Sel("topRight"));
                words.Add(new OcrWord(start, i, new PdfRect(
                    bottomLeft.X * pageWidth,
                    (1 - topRight.Y) * pageHeight,
                    (topRight.X - bottomLeft.X) * pageWidth,
                    (topRight.Y - bottomLeft.Y) * pageHeight)));
            }
            start = -1;
        }
        return words;
    }

    private static readonly Dictionary<string, IntPtr> Selectors = [];

    private static IntPtr Sel(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out var sel))
                Selectors[name] = sel = sel_registerName(name);
            return sel;
        }
    }
}
