using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Editing;

/// <summary>What the #139 page check says when a change is about to regenerate a page.</summary>
public enum PageCheckAnswer
{
    /// <summary>The page keeps its look, or it is settled: apply without a warning.</summary>
    KeepsLook,

    /// <summary>Regenerating the page would change parts of it: warn, with Continue and Cancel.</summary>
    WouldChange,

    /// <summary>The check did not answer within the budget: apply without a warning (#145).</summary>
    OverBudget,

    /// <summary>
    /// Another document opened while the change waited, so there is nothing to apply the change to.
    /// The running check is stopped and the page is left unsettled, because the page now on screen
    /// is a page of the new document (#332).
    /// </summary>
    Cancelled,
}

/// <summary>
/// The once-per-page warning before a change the layout guard never judges (#139).
/// Whiteouts and text boxes make PDFium regenerate their page's content just as a body-text
/// edit does, and on a page its writer cannot write back faithfully that changes parts of the
/// page the person never touched. Those changes are never refused: the app asks this before
/// applying one, and when it says so, warns with Continue and Cancel. One instance per open
/// document; a page is asked about at most once per open, however many changes follow.
///
/// The check is slow on a heavy page (seconds, and a minute on a few), so it is started early
/// (#145): <see cref="Prepare"/> when a page is first shown or a tool is armed, in the
/// background, one check per page. <see cref="AskAsync(IPdfDocument, int, TimeSpan)"/> at the
/// change reuses it and waits at most <see cref="Budget"/>; a check that has not answered by
/// then is cancelled and the change applies without a warning, because a warning that arrives
/// after the person has moved on helps nobody.
/// </summary>
public sealed class PageRegenerationWarnings
{
    /// <summary>How long a change waits on its page's check before it applies without one (#145).</summary>
    public static TimeSpan Budget { get; } = TimeSpan.FromSeconds(1.5);

    private readonly HashSet<int> _settled = [];
    private readonly object _gate = new();

    /// <summary>
    /// The one check running, for the page in front of the person (#332). One, not one per page:
    /// "starting a check cancels an unfinished check for another page" is then true by
    /// construction, and closing the document drops the only record of it, so nothing here keeps
    /// a closed document's page alive.
    /// </summary>
    private RunningCheck? _running;

    /// <summary>
    /// The page a change is waiting on, if any. A check started for another page leaves it alone:
    /// its answer is the one somebody is waiting for, and this page's turn comes later (#332).
    /// </summary>
    private int? _awaitedPage;

    /// <summary>Runs one page's check: the core's dry run, unless a test stands in for it.</summary>
    private readonly Func<IPdfDocument, int, CancellationToken, LayoutVerdict> _judge;

    /// <summary>Completes when a change's budget has run out: a real delay, unless a test stands in for it.</summary>
    private readonly Func<TimeSpan, Task> _waitBudget;

    public PageRegenerationWarnings()
        : this(judge: null, waitBudget: null)
    {
    }

    /// <summary>
    /// For tests (#145): <paramref name="judge"/> replaces the core's dry run and
    /// <paramref name="waitBudget"/> the budget's timer, so which of the two finishes first is
    /// decided by the test, not by how fast the machine runs.
    /// </summary>
    internal PageRegenerationWarnings(Func<IPdfDocument, int, CancellationToken, LayoutVerdict>? judge,
                                      Func<TimeSpan, Task>? waitBudget)
    {
        _judge = judge ?? JudgeWithCore;
        _waitBudget = waitBudget ?? (budget => Task.Delay(budget));
    }

    private static LayoutVerdict JudgeWithCore(IPdfDocument document, int pageIndex, CancellationToken cancellationToken)
    {
        using var page = document.GetPage(pageIndex);
        return page.GetPageRegenerationVerdict(cancellationToken);
    }

    /// <summary>Bumped by <see cref="Reset"/>, so a check for the previous document settles nothing in the next.</summary>
    private int _generation;

    private sealed record RunningCheck(int PageIndex, IPdfDocument Document, Task<LayoutVerdict?> Task, CancellationTokenSource Cancel);

    /// <summary>
    /// Whether <paramref name="operation"/> regenerates its page's content without the text
    /// guard judging it. Body-text edits and deletes are judged (and refused) on their own;
    /// signatures, check marks and form values are annotations and leave the content alone.
    /// </summary>
    public static bool RegeneratesUnjudged(IPageEditOperation operation) => operation is
        AddWhiteoutOperation or RemoveWhiteoutOperation or
        AddTextBoxOperation or MoveTextBoxOperation or RestyleTextBoxOperation or RemoveTextBoxOperation;

    /// <summary>
    /// True when the person should be warned before <paramref name="operation"/>: it regenerates
    /// unjudged content, its page has not been settled in this document, and regenerating the page
    /// would change how it looks. A page that keeps its look is settled here. Runs the core's dry
    /// run the first time for a page, which can take seconds on a heavy page: call it off the UI thread.
    /// </summary>
    public bool ShouldWarn(IPdfDocument document, IPageEditOperation operation) =>
        RegeneratesUnjudged(operation) && ShouldWarn(document, operation.PageIndex);

    /// <inheritdoc cref="ShouldWarn(IPdfDocument, IPageEditOperation)"/>
    public bool ShouldWarn(IPdfDocument document, int pageIndex)
    {
        lock (_gate)
        {
            if (_settled.Contains(pageIndex))
                return false;
        }

        LayoutVerdict verdict;
        try
        {
            using var page = document.GetPage(pageIndex);
            verdict = page.GetPageRegenerationVerdict();
        }
        catch (InvalidOperationException)
        {
            // A page the core cannot judge is no reason to stand in the way of the change.
            return false;
        }

        if (verdict.Editable)
            Settle(pageIndex);
        return !verdict.Editable;
    }

    /// <summary>
    /// Starts the page's check in the background, unless it is settled or already running
    /// (#145). Call when a page is first shown and when a tool that regenerates content is
    /// armed, so the answer is usually ready by the change. An unfinished check for another
    /// page is cancelled — the page in front of the person comes first, and a page left behind
    /// is checked again when it comes back — unless a change is waiting on that page's answer,
    /// in which case this page waits its turn (#332).
    /// </summary>
    public void Prepare(IPdfDocument document, int pageIndex) => _ = Start(document, pageIndex);

    /// <summary>
    /// The question at the change (#145): <see cref="PageCheckAnswer.WouldChange"/> means warn.
    /// Waits for the page's check (started now if <see cref="Prepare"/> was not called) at most
    /// <paramref name="budget"/>; past it the check is cancelled, the page settled, and the answer
    /// is <see cref="PageCheckAnswer.OverBudget"/>. A page the core cannot judge keeps its look.
    /// A check that answers in the same breath as the budget is the answer (#332): the budget
    /// stops the waiting, it does not discard what is already there.
    /// </summary>
    public async Task<PageCheckAnswer> AskAsync(IPdfDocument document, int pageIndex, TimeSpan budget)
    {
        if (IsSettled(pageIndex))
            return PageCheckAnswer.KeepsLook;

        var generation = _generation;
        var check = Start(document, pageIndex);
        if (!await WaitAsync(check, pageIndex, generation, budget).ConfigureAwait(false))
            return PageCheckAnswer.OverBudget;

        var verdict = await check.ConfigureAwait(false);
        // The document went away while we waited: what is on screen now is another document's page,
        // or nothing at all, so settle nothing in it (#332).
        if (generation != _generation)
            return PageCheckAnswer.Cancelled;

        if (verdict is not { Editable: false })
        {
            Settle(pageIndex);
            return PageCheckAnswer.KeepsLook;
        }
        return PageCheckAnswer.WouldChange;
    }

    /// <summary>
    /// Waits out <paramref name="budget"/> for the check, with the page marked as the one a change is
    /// on. True when the page has an answer to read, false when the budget ran out on a check with
    /// nothing to say — in which case the check is stopped and the page settled, because the change
    /// regenerates the page now and a warning about it afterwards would be too late.
    /// </summary>
    private async Task<bool> WaitAsync(Task<LayoutVerdict?> check, int pageIndex, int generation, TimeSpan budget)
    {
        lock (_gate)
            _awaitedPage = pageIndex;
        try
        {
            // A check that has answered by the time the budget runs out wins, whether or not the
            // budget's task came back first (#332): the budget stops the waiting, it does not
            // discard an answer that is already there.
            if (await Task.WhenAny(check, _waitBudget(budget)).ConfigureAwait(false) == check || check.IsCompleted)
                return true;

            if (generation == _generation)
                Settle(pageIndex); // also stops the check, which is still the one running
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _awaitedPage = null;
            }
        }
    }

    /// <summary><see cref="AskAsync(IPdfDocument, int, TimeSpan)"/> for an operation, with the default budget; anything that does not regenerate the page keeps its look.</summary>
    public Task<PageCheckAnswer> AskAsync(IPdfDocument document, IPageEditOperation operation, TimeSpan? budget = null) =>
        RegeneratesUnjudged(operation)
            ? AskAsync(document, operation.PageIndex, budget ?? Budget)
            : Task.FromResult(PageCheckAnswer.KeepsLook);

    /// <summary>Whether the page needs no more asking: it keeps its look, or the person chose Continue.</summary>
    public bool IsSettled(int pageIndex)
    {
        lock (_gate)
            return _settled.Contains(pageIndex);
    }

    /// <summary>
    /// The person chose Continue, or a change went in unasked: no more warnings for this page in
    /// this document, and a check still running for it has nothing left to say.
    /// </summary>
    public void Settle(int pageIndex)
    {
        lock (_gate)
        {
            _settled.Add(pageIndex);
            if (_running is { } running && running.PageIndex == pageIndex)
            {
                _running = null;
                running.Cancel.Cancel();
            }
        }
    }

    /// <summary>Forgets every page and cancels the running check: call when another document is opened.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _running?.Cancel.Cancel();
            _running = null;
            _awaitedPage = null;
            _settled.Clear();
        }
    }

    /// <summary>Whether a check for the page is running (tests and diagnostics).</summary>
    public bool IsChecking(int pageIndex)
    {
        lock (_gate)
            return _running is { } running && running.PageIndex == pageIndex && !running.Task.IsCompleted;
    }

    private Task<LayoutVerdict?> Start(IPdfDocument document, int pageIndex)
    {
        lock (_gate)
        {
            if (_settled.Contains(pageIndex))
                return Task.FromResult<LayoutVerdict?>(null);
            if (_running is { } existing)
            {
                if (existing.PageIndex == pageIndex && ReferenceEquals(existing.Document, document)
                    && !existing.Cancel.IsCancellationRequested)
                    return existing.Task;
                // A change is waiting on that answer; this page's turn comes later (#332). Only
                // Prepare passes through here: a change asks about the page it is already waiting on.
                if (existing.PageIndex == _awaitedPage && !existing.Task.IsCompleted)
                    return Task.FromResult<LayoutVerdict?>(null);
                if (!existing.Task.IsCompleted)
                {
                    _running = null;
                    existing.Cancel.Cancel();
                }
            }

            var cancel = new CancellationTokenSource();
            var generation = _generation;
            var task = Task.Run(() => Judge(document, pageIndex, generation, cancel.Token));
            _running = new RunningCheck(pageIndex, document, task, cancel);
            return task;
        }
    }

    /// <summary>Off the UI thread: the verdict, or null when cancelled or the page cannot be judged.</summary>
    private LayoutVerdict? Judge(IPdfDocument document, int pageIndex, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var verdict = _judge(document, pageIndex, cancellationToken);
            if (verdict.Editable)
            {
                lock (_gate)
                {
                    if (generation == _generation)
                        _settled.Add(pageIndex);
                }
            }
            return verdict;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null; // the document closed before the check began
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
