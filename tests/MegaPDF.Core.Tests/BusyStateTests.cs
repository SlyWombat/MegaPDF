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
