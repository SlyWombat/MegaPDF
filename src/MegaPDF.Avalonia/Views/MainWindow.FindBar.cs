using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The find bar's one row, at every window width (#237).
///
/// The bar holds the box, Previous, Next, the match counter and Done, laid out left to
/// right. At the window's 480 DIP minimum that is wider than the window in every
/// language: Done was off the right edge and the French counter was cut mid-word
/// ("1 su"), which left no pointer-driven way to close find at all.
///
/// So the row sheds detail as it narrows, in the order the toolbar sheds its own
/// (MainWindow.Toolbar.cs), and for the same reason — what goes is what the row can
/// afford to lose:
///
///   1. Full: Previous and Next spelled out, the box at its usual 260 DIP.
///   2. Chevrons: the two buttons keep their glyph and give up their word.
///   3. The box narrows, to <see cref="FindBoxMin"/> DIP.
///   4. The counter trims with an ellipsis.
///
/// Done is measured first and is never asked to give anything up: it is the way out of
/// find, so it stays whole and stays on screen. Nothing is lost to a screen reader at
/// step 2 either — the buttons carry their word as their accessible name and their
/// tooltip whether or not it is showing, which is what the toolbar does at its own
/// icons-only step.
///
/// The thresholds are measured, not constants, because the words differ per language:
/// "Précédent" and "Suivant" are wider than "Previous" and "Next", which is why French
/// lost more of the row than English did.
/// </summary>
public partial class MainWindow
{
    /// <summary>The find box's width when the row has room for it, as the XAML sets it.</summary>
    private const double FindBoxFull = 260;

    /// <summary>How narrow the find box may get before the counter starts giving up width instead.</summary>
    private const double FindBoxMin = 120;

    private double _findPreviousLabelled, _findPreviousGlyph;
    private double _findNextLabelled, _findNextGlyph;
    private double _findDoneWidth, _findSummaryReserve;
    private bool _findMeasured;
    private bool? _findLabelsShown;

    /// <summary>Called from WireFind, with the rest of the find bar's wiring.</summary>
    private void WireFindBar()
    {
        // Read whether or not the word is showing, so the chevron step costs a screen
        // reader nothing (#237). The same contract the toolbar's icons-only step keeps.
        // Done is named here too: its word is its content today, and a name that is
        // stated does not depend on that staying true.
        AutomationProperties.SetName(FindPreviousButton, Strings.Previous);
        AutomationProperties.SetName(FindNextButton, Strings.Next);
        AutomationProperties.SetName(CloseFindButton, Strings.Done);

        FindBarHost.SizeChanged += (_, _) => ApplyFindBarLayout();
    }

    /// <summary>
    /// Picks the step for the width the row has. Cheap: the widths are measured once
    /// per window, so this is arithmetic plus, when the step changes, visibility.
    /// </summary>
    private void ApplyFindBarLayout()
    {
        var available = FindBarHost.Bounds.Width
                        - FindBarHost.Padding.Left - FindBarHost.Padding.Right
                        - FindBarHost.BorderThickness.Left - FindBarHost.BorderThickness.Right;
        if (available <= 0)
            return;

        if (!_findMeasured)
            MeasureFindBar();

        // Five items, so four gaps, whichever step the row is in.
        var spacing = FindBarItems.Spacing * 4;

        // Step 1 only if the box can stay at its full width with the words showing.
        var labels = available - _findDoneWidth - _findPreviousLabelled - _findNextLabelled
                     - _findSummaryReserve - spacing >= FindBoxFull;
        var buttons = labels
            ? _findPreviousLabelled + _findNextLabelled
            : _findPreviousGlyph + _findNextGlyph;

        // What the box and the counter have to share once Done and the buttons are paid for.
        var slack = available - _findDoneWidth - buttons - spacing;
        var box = Math.Clamp(slack - _findSummaryReserve, FindBoxMin, FindBoxFull);
        if (box > slack)
            box = Math.Max(0, slack);   // narrower than anything this bar was designed for.
        var summary = slack - box;

        if (_findLabelsShown != labels)
        {
            _findLabelsShown = labels;
            SetFindButtonContent(labels);
            // A tooltip only where the word has gone: over a button that still says
            // "Previous", a tip reading "Previous" is noise.
            ToolTip.SetTip(FindPreviousButton, labels ? null : Strings.Previous);
            ToolTip.SetTip(FindNextButton, labels ? null : Strings.Next);
        }
        FindBox.Width = box;
        // Uncapped while there is room for the reserve, so the ellipsis appears only
        // when the counter really is being squeezed rather than through a rounding
        // difference between what was measured and what was given.
        FindMatchSummary.MaxWidth = summary >= _findSummaryReserve ? double.PositiveInfinity : Math.Max(0, summary);
    }

    /// <summary>
    /// Measures the parts of the row that do not change with the window: the two
    /// buttons with their word and with their chevron, Done, and the width to keep
    /// for the counter.
    /// </summary>
    private void MeasureFindBar()
    {
        _findMeasured = true;

        var labelsWere = FindPreviousLabel.IsVisible;
        SetFindButtonContent(labels: true);
        _findPreviousLabelled = MeasureWidth(FindPreviousButton);
        _findNextLabelled = MeasureWidth(FindNextButton);
        SetFindButtonContent(labels: false);
        _findPreviousGlyph = MeasureWidth(FindPreviousButton);
        _findNextGlyph = MeasureWidth(FindNextButton);
        SetFindButtonContent(labels: labelsWere);
        _findLabelsShown = labelsWere;

        _findDoneWidth = MeasureWidth(CloseFindButton);
        _findSummaryReserve = MeasureSummaryReserve();
    }

    private void SetFindButtonContent(bool labels)
    {
        FindPreviousLabel.IsVisible = labels;
        FindNextLabel.IsVisible = labels;
        FindPreviousGlyph.IsVisible = !labels;
        FindNextGlyph.IsVisible = !labels;
    }

    /// <summary>
    /// How much of the row to keep for the counter, whatever it happens to say at the
    /// moment.
    ///
    /// Not the counter's current width: that is empty until the first search and then
    /// changes with the numbers in it, so sizing the box against it would make the box
    /// jump as you typed. The reserve is the widest thing the counter can ordinarily
    /// read — "Not found", or a two-digit hit out of a two-digit total, in the running
    /// language. A document with more hits than that simply ellipsises at the narrow
    /// step, which is the one place there is no room to do better.
    /// </summary>
    private double MeasureSummaryReserve()
    {
        var typeface = new Typeface(FindMatchSummary.FontFamily, FindMatchSummary.FontStyle,
                                    FindMatchSummary.FontWeight, FindMatchSummary.FontStretch);
        var widest = 0.0;
        foreach (var text in new[] { Strings.NotFound, Strings.MatchOf(88, 88) })
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                                              FlowDirection.LeftToRight, typeface,
                                              FindMatchSummary.FontSize, Brushes.Black);
            widest = Math.Max(widest, formatted.Width);
        }
        return Math.Ceiling(widest) + FindMatchSummary.Margin.Left + FindMatchSummary.Margin.Right;
    }

    /// <summary>For --screenshot runs and the self-test: which step the row is in.</summary>
    internal string DescribeFindBar() =>
        $"find bar: step={(_findLabelsShown == true ? "Full" : "Chevrons")}, "
        + $"width={FindBarHost.Bounds.Width:F0} DIP, box={FindBox.Width:F0} DIP, "
        + $"counter={(double.IsInfinity(FindMatchSummary.MaxWidth) ? "untrimmed" : $"{FindMatchSummary.MaxWidth:F0} DIP")}, "
        + $"Done right edge at {FindBarRightEdge(CloseFindButton):F0} of {FindBarHost.Bounds.Width:F0} DIP";

    /// <summary>
    /// Where <paramref name="item"/>'s right edge falls in the find bar's own
    /// coordinates — what "Done is on screen" means, for the self-test to assert.
    /// </summary>
    internal double FindBarRightEdge(Control item)
    {
        var origin = item.TranslatePoint(new Point(item.Bounds.Width, 0), FindBarHost);
        return origin?.X ?? double.NaN;
    }
}
