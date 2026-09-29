using MegaPDF.Core.Engine;
using MegaPDF.Core.Services;
using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Reading mode's two non-UI rules, and the settings both desktops share (#168, #504,
/// #510).
///
/// This is deliberately not a test of a window. The WinUI app has no self-test that CI
/// can run (#462) — WinUI has no headless platform and a GitHub runner has no desktop
/// session — so the parts of #504/#510 that a machine can check on every push are the
/// ones that were written to be checkable: the Escape ladder, the rule that keeps the
/// floating bar up, and the two fields in settings.json that the Windows ⚙ flyout and
/// the Avalonia Options flyout both read and write.
///
/// What this does *not* prove is said plainly rather than left to be assumed: that the
/// window wires Escape to <see cref="ReadingMode.NextEscape"/> at all, that hidden
/// chrome really leaves the tab order, or that a screen reader is detected. Those are
/// the WinUI app's `--screenshot-state reading` checks, which need a real window.
/// </summary>
public class ReadingModeTests
{
    /// <summary>
    /// The ladder from the plan's §7: find bar → full screen → reading mode → whatever
    /// Escape did before. Every combination, because the risk is a level being skipped
    /// in exactly the combinations nobody thought to try by hand.
    /// </summary>
    [Theory]
    // find bar open: it always goes first, whatever is underneath it
    [InlineData(true, true, true, ReadingModeStep.CloseFind)]
    [InlineData(true, false, true, ReadingModeStep.CloseFind)]
    [InlineData(true, true, false, ReadingModeStep.CloseFind)]
    [InlineData(true, false, false, ReadingModeStep.CloseFind)]
    // no find bar, full screen on: full screen goes, reading mode stays
    [InlineData(false, true, true, ReadingModeStep.LeaveFullScreen)]
    [InlineData(false, true, false, ReadingModeStep.LeaveFullScreen)]
    // reading mode alone
    [InlineData(false, false, true, ReadingModeStep.LeaveReadingMode)]
    // none of ours: Escape is somebody else's key
    [InlineData(false, false, false, ReadingModeStep.Nothing)]
    public void EscapeStepsBackExactlyOneLevel(bool find, bool fullScreen, bool reading, ReadingModeStep expected) =>
        Assert.Equal(expected, ReadingMode.NextEscape(find, fullScreen, reading));

    /// <summary>
    /// The sequence the issue asks for by name: find bar open, full screen on, reading
    /// mode on, then Escape three times. Each press must take exactly one level, in
    /// order, and the fourth must hand the key back.
    /// </summary>
    [Fact]
    public void ThreeEscapesUnwindTheThreeLevelsInOrder()
    {
        var (find, fullScreen, reading) = (true, true, true);
        var taken = new List<ReadingModeStep>();

        for (var press = 0; press < 4; press++)
        {
            var step = ReadingMode.NextEscape(find, fullScreen, reading);
            taken.Add(step);
            switch (step)
            {
                case ReadingModeStep.CloseFind: find = false; break;
                // Leaving reading mode drops full screen with it, so the state machine
                // here is the window's: full screen is a level *inside* the mode.
                case ReadingModeStep.LeaveFullScreen: fullScreen = false; break;
                case ReadingModeStep.LeaveReadingMode: reading = false; fullScreen = false; break;
            }
        }

        Assert.Equal(
        [
            ReadingModeStep.CloseFind,
            ReadingModeStep.LeaveFullScreen,
            ReadingModeStep.LeaveReadingMode,
            ReadingModeStep.Nothing,
        ], taken);
    }

    /// <summary>
    /// The bar fades only when nobody is relying on it being there. The screen-reader
    /// half is the accessibility invariant from plan §7; the focus half is what stops
    /// the bar disappearing from under the Tab key.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]   // nobody is watching: fade
    [InlineData(true, false, false)]   // a screen reader is on: never
    [InlineData(false, true, false)]   // keyboard focus is in it: never
    [InlineData(true, true, false)]
    public void TheBarFadesOnlyWhenNobodyNeedsIt(bool reader, bool focus, bool mayFade) =>
        Assert.Equal(mayFade, ReadingMode.BarMayFade(reader, focus));

    /// <summary>
    /// The two fields Windows and the Avalonia desktops share in one settings.json
    /// (#510/#511). Written by one, read by the other: the names and the defaults are
    /// the contract, so they are asserted rather than assumed.
    /// </summary>
    [Fact]
    public void PageColoursAndOpenInReadingModeRoundTripThroughTheSharedFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"megapdf-reading-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var written = new AppSettings(path);
            Assert.Equal("", written.PageColours);
            Assert.Equal(PageTint.Normal, written.PageTint);
            Assert.False(written.OpenInReadingMode);

            written.PageTint = PageTint.Sepia;
            written.OpenInReadingMode = true;

            var read = new AppSettings(path);
            Assert.Equal("Sepia", read.PageColours);
            Assert.Equal(PageTint.Sepia, read.PageTint);
            Assert.True(read.OpenInReadingMode);

            // A value this version does not know is the page as the document draws it,
            // not a crash: a settings file written by a later version is user data.
            File.WriteAllText(path, """{"PageColours":"Aubergine"}""");
            Assert.Equal(PageTint.Normal, new AppSettings(path).PageTint);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
