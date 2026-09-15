using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The busy pattern every app follows (#145): disable at once, show the indicator after a
/// delay, keep it up for a minimum once shown. Scaled-down timings, with margins wide enough
/// for a loaded test machine.
/// </summary>
public sealed class BusyStateTests
{
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(600);

    private static BusyState NewState() => new(ShowAfter, MinimumVisible, context: null);

    [Fact]
    public void QuickWork_DisablesAtOnce_AndNeverShowsTheIndicator()
    {
        var busy = NewState();
        var operation = busy.Begin("Saving…");
        Assert.True(busy.IsBusy);
        Assert.False(busy.IsIndicatorVisible);
        operation.Dispose();
        Assert.False(busy.IsBusy);

        Thread.Sleep(ShowAfter * 2);
        Assert.False(busy.IsIndicatorVisible);
    }

    [Fact]
    public async Task SlowWork_ShowsTheIndicatorAfterTheDelay_AndKeepsItUpForTheMinimum()
    {
        var busy = NewState();
        var operation = busy.Begin("Saving…");
        Assert.False(busy.IsIndicatorVisible);

        await WaitFor(() => busy.IsIndicatorVisible, TimeSpan.FromSeconds(5));
        Assert.True(busy.ShowsStrip);
        Assert.False(busy.ShowsPageSpinner);
        Assert.Equal("Saving…", busy.Label);

        operation.Dispose();
        Assert.False(busy.IsBusy);
        // Just shown, so it lingers rather than flickering off.
        Assert.True(busy.IsIndicatorVisible);
        Assert.Equal("Saving…", busy.Label);
        await WaitFor(() => !busy.IsIndicatorVisible, TimeSpan.FromSeconds(5));
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
