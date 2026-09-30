using Microsoft.Extensions.Time.Testing;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The busy pattern every app follows (#145): disable at once, show the indicator after a
/// delay, keep it up for a minimum once shown.
///
/// #515: the two timing-dependent tests drive a clock they control rather than waiting on a
/// real one. They used to wait on wall time with margins described as "wide enough for a
/// loaded test machine", and on a runner also building PDFium that stopped being true: the
/// gap between observing the indicator and disposing the operation is thread-pool scheduling,
/// unbounded, and losing enough of it in that gap let the minimum-visible window expire
/// before the assertion that the window was still open. It failed pull requests whose diffs
/// could not reach any of this code. Widening the margin would only move the threshold; a
/// clock the test advances removes the race.
///
/// The tests that assert on state rather than on elapsed time still use the real provider —
/// they never wait for a deadline, so there is nothing to race.
/// </summary>
public sealed class BusyStateTests
{
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(600);

    private static BusyState NewState() => new(ShowAfter, MinimumVisible, context: null);

    /// <summary>A state whose delays and elapsed measurement both come from a clock the test moves.</summary>
    private static (BusyState Busy, FakeTimeProvider Time) NewControlledState()
    {
        var time = new FakeTimeProvider();
        return (new BusyState(ShowAfter, MinimumVisible, context: null, time), time);
    }

    /// <summary>
    /// Advances the clock and lets the continuations it released actually run. FakeTimeProvider
    /// fires its timers synchronously, but the awaiting continuation inside BusyState is posted
    /// to the thread pool, so a bare Advance can return before the state has published. Yielding
    /// is not a timing margin: it waits for scheduled work, not for a duration.
    /// </summary>
    private static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        for (var i = 0; i < 100; i++)
            await Task.Yield();
    }

    [Fact]
    public async Task QuickWork_DisablesAtOnce_AndNeverShowsTheIndicator()
    {
        var (busy, time) = NewControlledState();
        var operation = busy.Begin("Saving…");
        Assert.True(busy.IsBusy);
        Assert.False(busy.IsIndicatorVisible);
        operation.Dispose();
        Assert.False(busy.IsBusy);

        // Well past the show delay: work that finished first must never flash an indicator.
        await AdvanceAsync(time, ShowAfter * 2);
        Assert.False(busy.IsIndicatorVisible);
    }

    [Fact]
    public async Task SlowWork_ShowsTheIndicatorAfterTheDelay_AndKeepsItUpForTheMinimum()
    {
        var (busy, time) = NewControlledState();
        var operation = busy.Begin("Saving…");
        Assert.False(busy.IsIndicatorVisible);

        // Not yet: one tick short of the delay the indicator still owes.
        await AdvanceAsync(time, ShowAfter - TimeSpan.FromMilliseconds(1));
        Assert.False(busy.IsIndicatorVisible);

        await AdvanceAsync(time, TimeSpan.FromMilliseconds(1));
        Assert.True(busy.IsIndicatorVisible);
        Assert.True(busy.ShowsStrip);
        Assert.False(busy.ShowsPageSpinner);
        Assert.Equal("Saving…", busy.Label);

        // The work ends immediately after the indicator appeared, which is the case the
        // minimum exists for. The clock has not moved, so no scheduling delay can eat it.
        operation.Dispose();
        Assert.False(busy.IsBusy);
        Assert.True(busy.IsIndicatorVisible);
        Assert.Equal("Saving…", busy.Label);

        // Still up one tick before the minimum is served, gone once it is.
        await AdvanceAsync(time, MinimumVisible - TimeSpan.FromMilliseconds(1));
        Assert.True(busy.IsIndicatorVisible);
        await AdvanceAsync(time, TimeSpan.FromMilliseconds(1));
        Assert.False(busy.IsIndicatorVisible);
    }

    [Fact]
    public async Task TheNewestOperation_NamesTheLabelAndScope()
    {
        var busy = NewState();
        using var save = busy.Begin("Saving…");
        save.SetLabel("Checking the saved file…");
        Assert.Equal("Checking the saved file…", busy.Label);

        var check = busy.Begin("Checking this page…", scope: BusyScope.Page, pageIndex: 2);
        Assert.Equal("Checking this page…", busy.Label);
        Assert.Equal(BusyScope.Page, busy.Scope);
        Assert.Equal(2, busy.PageIndex);
        await WaitFor(() => busy.IsIndicatorVisible, TimeSpan.FromSeconds(5));
        Assert.True(busy.ShowsPageSpinner);

        check.Dispose();
        Assert.Equal("Checking the saved file…", busy.Label);
        Assert.Equal(BusyScope.Document, busy.Scope);
        Assert.True(busy.IsBusy);
    }

    [Fact]
    public void Search_ShowsWorkWithoutBlockingEditing()
    {
        var busy = NewState();
        using var search = busy.Begin("Searching…", blocksEditing: false);
        Assert.True(busy.IsWorking);
        Assert.False(busy.IsBusy);
    }

    [Fact]
    public async Task WhenIdle_CompletesWhenTheLastOperationEnds()
    {
        var busy = NewState();
        Assert.True(busy.WhenIdleAsync().IsCompleted);

        var first = busy.Begin("Opening…");
        var second = busy.Begin("Checking this page…", scope: BusyScope.Page);
        var idle = busy.WhenIdleAsync();
        first.Dispose();
        Assert.False(idle.IsCompleted);
        second.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PropertyChanges_AreRaised()
    {
        var busy = NewState();
        var raised = new List<string>();
        busy.PropertyChanged += (_, e) => { lock (raised) raised.Add(e.PropertyName!); };
        var operation = busy.Begin("Opening…");
        await WaitFor(() => busy.IsIndicatorVisible, TimeSpan.FromSeconds(5));
        operation.Dispose();
        lock (raised)
        {
            Assert.Contains(nameof(BusyState.IsBusy), raised);
            Assert.Contains(nameof(BusyState.IsIndicatorVisible), raised);
            Assert.Contains(nameof(BusyState.ShowsStrip), raised);
            Assert.Contains(nameof(BusyState.Label), raised);
        }
    }

    // --- Progress and Cancel (#145 P2) -------------------------------------------------------

    [Fact]
    public void WorkThatCountsItself_PublishesItsProgressAndItsWords()
    {
        var busy = NewState();
        Assert.Null(busy.Progress);
        Assert.False(busy.HasProgress);
        Assert.Equal(-1, busy.ProgressDone);
        Assert.Equal(-1, busy.ProgressTotal);
        Assert.Equal("", busy.ProgressText);

        using var search = busy.Begin("Searching…", blocksEditing: false,
                                      progressFormat: (done, total) => $"Page {done} of {total}");
        // Begun, but nothing reported yet: an indeterminate bar, not a bar sitting at zero,
        // because "0 of 2000" before the first page is read is a claim about work not yet begun.
        Assert.False(busy.HasProgress);

        search.Report(0, 2000);
        Assert.True(busy.HasProgress);
        Assert.Equal(0.0, busy.Progress);
        Assert.Equal("Page 0 of 2000", busy.ProgressText);

        search.Report(500, 2000);
        Assert.Equal(0.25, busy.Progress);
        Assert.Equal(500, busy.ProgressDone);
        Assert.Equal(2000, busy.ProgressTotal);
        Assert.Equal("Page 500 of 2000", busy.ProgressText);

        search.Report(2000, 2000);
        Assert.Equal(1.0, busy.Progress);
    }

    [Fact]
    public void ProgressIsClamped_AndATotalOfNoneMeansIndeterminate()
    {
        var busy = NewState();
        using var work = busy.Begin("Making a smaller copy…",
                                    progressFormat: (done, total) => $"Picture {done} of {total}");
        work.Report(5, 10);
        Assert.Equal(0.5, busy.Progress);

        // More done than there is to do, and less than none: a miscount must not draw a bar
        // past its end or before its start, and must not throw inside a publish every other
        // binding rides on.
        work.Report(50, 10);
        Assert.Equal(1.0, busy.Progress);
        Assert.Equal(10, busy.ProgressDone);
        work.Report(-5, 10);
        Assert.Equal(0.0, busy.Progress);
        Assert.Equal(0, busy.ProgressDone);

        // A total that turns out not to be countable after all puts the bar back to
        // indeterminate rather than leaving it stuck where it was.
        work.Report(0, 0);
        Assert.False(busy.HasProgress);
        Assert.Null(busy.Progress);
        Assert.Equal("", busy.ProgressText);
    }

    /// <summary>
    /// <see cref="BusyState.ProgressValue"/> and <see cref="BusyState.IsIndeterminate"/> exist for
    /// a binding target that cannot take a null double or negate one — WinUI's x:Bind, which has
    /// no <c>TargetNullValue</c> the way Avalonia's binding does (#145).
    /// </summary>
    [Fact]
    public void ProgressValueAndIsIndeterminate_MirrorProgressForABindingThatCannotTakeNull()
    {
        var busy = NewState();
        Assert.Equal(0, busy.ProgressValue);
        Assert.True(busy.IsIndeterminate);

        using var work = busy.Begin("Making a smaller copy…");
        Assert.Equal(0, busy.ProgressValue);
        Assert.True(busy.IsIndeterminate);

        work.Report(3, 12);
        Assert.Equal(0.25, busy.ProgressValue);
        Assert.False(busy.IsIndeterminate);

        // Not countable after all: back to indeterminate, and the plain value back to 0 rather
        // than holding the last fraction.
        work.Report(0, 0);
        Assert.Equal(0, busy.ProgressValue);
        Assert.True(busy.IsIndeterminate);
    }

    [Fact]
    public void OnlyWorkBegunCancellable_OffersCancel()
    {
        var busy = NewState();
        Assert.False(busy.CanCancel);

        using (var save = busy.Begin("Saving…"))
        {
            // A save is deliberately not cancellable: there is no honest half-saved file to
            // leave behind, so the strip must not offer a way out that it cannot honour.
            Assert.False(busy.CanCancel);
            Assert.Equal(CancellationToken.None, save.CancellationToken);
            busy.RequestCancel();
            Assert.False(save.IsCancellationRequested);
        }

        using var shrink = busy.Begin("Making a smaller copy…", cancellable: true);
        Assert.True(busy.CanCancel);
        Assert.False(busy.IsCancelling);
        Assert.False(shrink.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_RaisesTheTokenOnceAndThenStopsOffering()
    {
        var busy = NewState();
        using var shrink = busy.Begin("Making a smaller copy…", cancellable: true);
        var token = shrink.CancellationToken;

        busy.RequestCancel();
        Assert.True(token.IsCancellationRequested);
        Assert.True(shrink.IsCancellationRequested);
        // The button goes away the moment it is pressed, and "Stopping…" takes its place: a
        // forty-second shrink does not stop the instant it is asked to, and a button still
        // inviting a second press implies the first one did nothing.
        Assert.False(busy.CanCancel);
        Assert.True(busy.IsCancelling);

        // Idempotent: a double click is not two cancels.
        busy.RequestCancel();
        Assert.True(busy.IsCancelling);
    }

    [Fact]
    public async Task CancelReachesOnlyRunningWork_NotTheIndicatorOnItsWayOut()
    {
        var (busy, time) = NewControlledState();
        var shrink = busy.Begin("Making a smaller copy…", cancellable: true);
        shrink.Report(300, 1000);
        await AdvanceAsync(time, ShowAfter);
        Assert.True(busy.IsIndicatorVisible);
        Assert.True(busy.CanCancel);

        // The work ends while the indicator still owes its minimum. The strip is still up, and
        // still says what it said, but there is nothing left to stop — so Stop is gone. A cancel
        // that survived this window would be aimed at whatever operation begins next.
        shrink.Dispose();
        Assert.True(busy.IsIndicatorVisible);
        Assert.Equal("Making a smaller copy…", busy.Label);
        Assert.False(busy.CanCancel);
        Assert.False(busy.IsCancelling);

        // And the bar holds its last value rather than falling back to indeterminate for the
        // final 0.3 s, which would be exactly the flicker this class exists to prevent.
        Assert.True(busy.HasProgress);
        Assert.Equal(0.3, busy.Progress);

        busy.RequestCancel(); // must be a no-op, and must not throw on the disposed source
        Assert.False(busy.IsCancelling);

        await AdvanceAsync(time, MinimumVisible);
        Assert.False(busy.IsIndicatorVisible);
    }

    [Fact]
    public void ProgressAndCancel_ComeFromTheSameOperationTheLabelDoes()
    {
        var busy = NewState();
        using var search = busy.Begin("Searching…", blocksEditing: false, cancellable: true,
                                      progressFormat: (done, total) => $"Page {done} of {total}");
        search.Report(10, 100);
        Assert.True(busy.CanCancel);
        Assert.Equal("Page 10 of 100", busy.ProgressText);

        // A page check nested inside it takes over the label, so it takes over the bar and the
        // button too: a strip that said "Checking this page…" over a search's progress, with a
        // Stop that stopped the search, would be reporting one thing and doing another.
        using (var check = busy.Begin("Checking this page…", scope: BusyScope.Page, pageIndex: 3))
        {
            Assert.Equal("Checking this page…", busy.Label);
            Assert.False(busy.HasProgress);
            Assert.Equal("", busy.ProgressText);
            Assert.False(busy.CanCancel);
            busy.RequestCancel();
            Assert.False(search.IsCancellationRequested);
        }

        // And the search gets them back when the check ends.
        Assert.Equal("Searching…", busy.Label);
        Assert.Equal("Page 10 of 100", busy.ProgressText);
        Assert.True(busy.CanCancel);
    }

    [Fact]
    public void WorkWithNoFormat_StillDrawsABarButSaysNothing()
    {
        var busy = NewState();
        using var work = busy.Begin("Opening…");
        work.Report(1, 4);
        Assert.True(busy.HasProgress);
        Assert.Equal(0.25, busy.Progress);
        Assert.Equal("", busy.ProgressText);
    }

    [Fact]
    public async Task ProgressAndCancelChanges_AreRaised()
    {
        var busy = NewState();
        var raised = new List<string>();
        busy.PropertyChanged += (_, e) => { lock (raised) raised.Add(e.PropertyName!); };
        var work = busy.Begin("Making a smaller copy…", cancellable: true,
                              progressFormat: (done, total) => $"Picture {done} of {total}");
        work.Report(1, 10);
        busy.RequestCancel();
        await WaitFor(() => busy.IsIndicatorVisible, TimeSpan.FromSeconds(5));
        work.Dispose();
        lock (raised)
        {
            Assert.Contains(nameof(BusyState.Progress), raised);
            Assert.Contains(nameof(BusyState.HasProgress), raised);
            Assert.Contains(nameof(BusyState.ProgressValue), raised);
            Assert.Contains(nameof(BusyState.IsIndeterminate), raised);
            Assert.Contains(nameof(BusyState.ProgressDone), raised);
            Assert.Contains(nameof(BusyState.ProgressTotal), raised);
            Assert.Contains(nameof(BusyState.ProgressText), raised);
            Assert.Contains(nameof(BusyState.CanCancel), raised);
            Assert.Contains(nameof(BusyState.IsCancelling), raised);
        }
    }

    [Fact]
    public void AFormatThatThrows_CostsItsOwnLineAndNothingElse()
    {
        var busy = NewState();
        using var work = busy.Begin("Searching…",
                                    progressFormat: (_, _) => string.Format("{1}", 1));
        // The publish that carries every other binding must survive a bad format string, so
        // this must not throw and the rest of the state must still be right.
        work.Report(3, 9);
        Assert.Equal("", busy.ProgressText);
        Assert.Equal(1.0 / 3, busy.Progress!.Value, 10);
        Assert.True(busy.IsBusy);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("timed out waiting for the busy state");
            await Task.Delay(10);
        }
    }
}
