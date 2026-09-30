using System.ComponentModel;
using System.Diagnostics;
using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Services;

/// <summary>Where the busy indicator belongs: the strip under the toolbar, or a spinner on the page or line.</summary>
public enum BusyScope
{
    /// <summary>Open, save, password, shrink, print, restore, search: a strip under the toolbar with a label.</summary>
    Document,

    /// <summary>The #139 page check, the text-edit check, applying a change: a small spinner on the page or line.</summary>
    Page,
}

/// <summary>
/// One document's busy state (#145), the pattern every app follows:
///
///   * at once: <see cref="IsBusy"/> is true, so the control that was used and every editing
///     and file command is disabled and repeat clicks are ignored;
///   * after 0.5 s: the indeterminate indicator shows (<see cref="IsIndicatorVisible"/>);
///   * once shown it stays at least 0.3 s, so a finish just past the threshold is not a flicker.
///
/// Operations nest: the newest one names the label and the scope. A non-blocking operation
/// (search, which a newer search supersedes) shows the indicator without disabling anything.
/// Property changes are raised on the synchronization context the state was created on (the UI
/// thread); an operation may be begun, relabelled and ended from any thread.
///
/// Work long enough that a spinner is not enough says more than that it is running (#145 P2):
///
///   * <see cref="Operation.Report(int, int)"/> gives it a denominator, which the indicator
///     draws as a determinate bar and <see cref="ProgressText"/> says in words. Core holds the
///     numbers and none of the wording: the words come from the <c>progressFormat</c> the app
///     hands <see cref="Begin"/>, because the noun differs per operation — a search counts pages
///     and a shrink counts pictures — and only the app has either word in either language;
///   * an operation begun with <c>cancellable: true</c> carries its own
///     <see cref="CancellationToken"/> and lights <see cref="CanCancel"/>, so the indicator can
///     offer a way out of work that is taking too long.
///
/// Both come from the same operation the label does — the newest one. Cancel is offered only
/// while that operation is genuinely running: once the last one has ended and the indicator is
/// merely living out its minimum, there is nothing left to stop, so <see cref="CanCancel"/> goes
/// false while <see cref="Progress"/> holds its last value (a bar that jumped to indeterminate
/// for the final 0.3 s would be a flicker of exactly the kind this class exists to prevent).
/// </summary>
public sealed class BusyState : INotifyPropertyChanged
{
    public static readonly TimeSpan DefaultShowAfter = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultMinimumVisible = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly SynchronizationContext? _context;
    private readonly TimeSpan _showAfter;
    private readonly TimeSpan _minimumVisible;
    private readonly List<Operation> _operations = [];
    private readonly TimeProvider _time;
    private readonly long _started;
    private List<TaskCompletionSource>? _idleWaiters;

    // Guarded by _gate.
    private int _generation;
    private bool _indicatorShown;
    private TimeSpan _shownAt;
    private Operation? _lastShown;

    // What was last published, read by bindings.
    private bool _isBusy;
    private bool _isWorking;
    private bool _isIndicatorVisible;
    private string _label = "";
    private BusyScope _scope;
    private int _pageIndex = -1;
    private PdfRect? _area;
    private double? _progress;
    private int _progressDone = -1;
    private int _progressTotal = -1;
    private string _progressText = "";
    private bool _canCancel;
    private bool _isCancelling;

    /// <summary>The app's timings, raising changes on the current synchronization context (create it on the UI thread).</summary>
    public BusyState()
        : this(DefaultShowAfter, DefaultMinimumVisible, SynchronizationContext.Current)
    {
    }

    /// <param name="showAfter">How long work runs before the indicator shows.</param>
    /// <param name="minimumVisible">How long a shown indicator stays.</param>
    /// <param name="context">Where property changes are raised; null raises them on whichever thread changed the state.</param>
    public BusyState(TimeSpan showAfter, TimeSpan minimumVisible, SynchronizationContext? context)
        : this(showAfter, minimumVisible, context, TimeProvider.System)
    {
    }

    /// <param name="showAfter">How long work runs before the indicator shows.</param>
    /// <param name="minimumVisible">How long a shown indicator stays.</param>
    /// <param name="context">Where property changes are raised; null raises them on whichever thread changed the state.</param>
    /// <param name="time">
    /// Where both the delays and the elapsed measurement come from. The app passes
    /// <see cref="TimeProvider.System"/>; a test passes a provider it advances itself.
    ///
    /// #515: the minimum-visible window used to be measured off a private Stopwatch and
    /// waited out with a bare Task.Delay, so a test could only assert on it by racing the
    /// thread pool. It lost that race on a loaded CI runner and failed pull requests whose
    /// diffs could not reach any of this code, which teaches everyone to re-run a red check
    /// without reading it. One provider makes the whole window deterministic instead of
    /// widening a margin and hoping.
    /// </param>
    public BusyState(TimeSpan showAfter, TimeSpan minimumVisible, SynchronizationContext? context, TimeProvider time)
    {
        _showAfter = showAfter;
        _minimumVisible = minimumVisible;
        _context = context;
        _time = time;
        _started = time.GetTimestamp();
    }

    /// <summary>How long this state has been alive, by its own clock.</summary>
    private TimeSpan Elapsed => _time.GetElapsedTime(_started);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A blocking operation is running: editing, Close and file commands wait.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>Any operation is running, blocking or not.</summary>
    public bool IsWorking => _isWorking;

    /// <summary>The indicator is showing: 0.5 s into the work, and for at least 0.3 s.</summary>
    public bool IsIndicatorVisible => _isIndicatorVisible;

    /// <summary>What the indicator says: "Saving…". Kept while the indicator lingers after the work.</summary>
    public string Label => _label;

    public BusyScope Scope => _scope;

    /// <summary>The page a <see cref="BusyScope.Page"/> operation is on; -1 otherwise.</summary>
    public int PageIndex => _pageIndex;

    /// <summary>The line (page points, top-left origin) a page operation is about, when it is about one.</summary>
    public PdfRect? Area => _area;

    /// <summary>
    /// How far through the work is, 0…1, or null when it cannot say — which is most work, and
    /// is what an indeterminate bar means. Held through the indicator's minimum-visible window
    /// so the bar does not fall back to indeterminate as it goes.
    /// </summary>
    public double? Progress => _progress;

    /// <summary>An indeterminate bar or a determinate one.</summary>
    public bool HasProgress => _progress is not null;

    /// <summary>How many units of the work are done; -1 when the work does not count in units.</summary>
    public int ProgressDone => _progressDone;

    /// <summary>How many units there are in all; -1 when the work does not count in units.</summary>
    public int ProgressTotal => _progressTotal;

    /// <summary>
    /// The progress in the person's words — "Page 37 of 2,000" — from the operation's own
    /// <c>progressFormat</c>. Empty when the work reports no progress, or when it was begun
    /// without a format, which is what a caller that wants a bare bar passes.
    /// </summary>
    public string ProgressText => _progressText;

    /// <summary>
    /// Work that can be stopped is running, and has not been asked to stop yet: the indicator
    /// offers Cancel. False the moment <see cref="RequestCancel"/> is called, so the button
    /// cannot be pressed twice, and false while the indicator is only living out its minimum.
    /// </summary>
    public bool CanCancel => _canCancel;

    /// <summary>Cancel was asked for and the work has not ended yet.</summary>
    public bool IsCancelling => _isCancelling;

    /// <summary>
    /// Asks the newest running operation to stop. Nothing happens when nothing is running, when
    /// the newest operation is not cancellable, or when it has already been asked — so a double
    /// click, or a click landing while the indicator is on its way out, is a no-op rather than a
    /// cancel aimed at whatever runs next.
    /// </summary>
    public void RequestCancel()
    {
        Operation? target = null;
        lock (_gate)
        {
            if (_operations.Count > 0 && _operations[^1] is { Cancellation: not null } top
                && !top.IsCancellationRequested)
                target = top;
        }
        if (target is null)
            return;
        target.RequestCancel();
        Publish();
    }

    /// <summary>The strip under the toolbar shows.</summary>
    public bool ShowsStrip => _isIndicatorVisible && _scope == BusyScope.Document;

    /// <summary>A spinner on the page or line shows.</summary>
    public bool ShowsPageSpinner => _isIndicatorVisible && _scope == BusyScope.Page;

    /// <summary>
    /// Starts an operation. Dispose the result when the work ends, whatever the outcome.
    /// </summary>
    /// <param name="cancellable">
    /// The operation makes its own <see cref="CancellationTokenSource"/>, offers
    /// <see cref="CanCancel"/> while it is the newest one running, and exposes the token as
    /// <see cref="Operation.CancellationToken"/>. The operation owns the source and disposes it
    /// with itself, so a cancel arriving after the work ended has nothing to reach.
    /// </param>
    public Operation Begin(string label, bool blocksEditing = true, BusyScope scope = BusyScope.Document,
                           int pageIndex = -1, PdfRect? area = null, bool cancellable = false,
                           Func<int, int, string>? progressFormat = null)
    {
        var operation = new Operation(this, label, blocksEditing, scope, pageIndex, area, cancellable, progressFormat);
        int? showTimer = null;
        lock (_gate)
        {
            if (_operations.Count == 0 && !_indicatorShown)
                showTimer = ++_generation;
            _operations.Add(operation);
        }
        Publish();
        if (showTimer is { } generation)
            _ = ShowLaterAsync(generation);
        return operation;
    }

    /// <summary>Runs <paramref name="work"/> as one operation.</summary>
    public async Task<T> RunAsync<T>(string label, Func<Operation, Task<T>> work, bool blocksEditing = true,
                                     BusyScope scope = BusyScope.Document, int pageIndex = -1, PdfRect? area = null,
                                     bool cancellable = false, Func<int, int, string>? progressFormat = null)
    {
        using var operation = Begin(label, blocksEditing, scope, pageIndex, area, cancellable, progressFormat);
        return await work(operation);
    }

    /// <inheritdoc cref="RunAsync{T}"/>
    public async Task RunAsync(string label, Func<Operation, Task> work, bool blocksEditing = true,
                               BusyScope scope = BusyScope.Document, int pageIndex = -1, PdfRect? area = null,
                               bool cancellable = false, Func<int, int, string>? progressFormat = null)
    {
        using var operation = Begin(label, blocksEditing, scope, pageIndex, area, cancellable, progressFormat);
        await work(operation);
    }

    /// <summary>Completes when no operation is running.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            if (_operations.Count == 0)
                return Task.CompletedTask;
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (_idleWaiters ??= []).Add(waiter);
            return waiter.Task;
        }
    }

    private async Task ShowLaterAsync(int generation)
    {
        await Task.Delay(_showAfter, _time).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _generation || _operations.Count == 0 || _indicatorShown)
                return;
            _indicatorShown = true;
            _shownAt = Elapsed;
        }
        Publish();
    }

    private void End(Operation operation)
    {
        TimeSpan? hideAfter = null;
        var generation = 0;
        List<TaskCompletionSource>? waiters = null;
        lock (_gate)
        {
            if (!_operations.Remove(operation))
                return;
            if (_operations.Count == 0)
            {
                generation = ++_generation;
                _lastShown = operation;
                if (_indicatorShown)
                {
                    var shownFor = Elapsed - _shownAt;
                    if (shownFor >= _minimumVisible)
                        _indicatorShown = false;
                    else
                        hideAfter = _minimumVisible - shownFor;
                }
                waiters = _idleWaiters;
                _idleWaiters = null;
            }
        }
        Publish();
        foreach (var waiter in waiters ?? [])
            waiter.TrySetResult();
        if (hideAfter is { } delay)
            _ = HideLaterAsync(generation, delay);
    }

    private async Task HideLaterAsync(int generation, TimeSpan delay)
    {
        await Task.Delay(delay, _time).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _generation || _operations.Count > 0)
                return;
            _indicatorShown = false;
        }
        Publish();
    }

    private void Publish()
    {
        if (_context is not null && SynchronizationContext.Current != _context)
            _context.Post(_ => PublishNow(), null);
        else
            PublishNow();
    }

    private void PublishNow()
    {
        bool busy, working, visible, canCancel, cancelling;
        string label;
        BusyScope scope;
        int pageIndex;
        PdfRect? area;
        double? progress;
        int done, total;
        string progressText;
        lock (_gate)
        {
            var running = _operations.Count > 0 ? _operations[^1] : null;
            var top = running ?? _lastShown;
            busy = _operations.Any(o => o.BlocksEditing);
            working = _operations.Count > 0;
            visible = _indicatorShown;
            label = top?.Label ?? "";
            scope = top?.Scope ?? BusyScope.Document;
            pageIndex = top?.PageIndex ?? -1;
            area = top?.Area;
            // Progress survives the linger (the bar holds its last value on the way out);
            // Cancel does not, because there is nothing running left to stop.
            progress = top?.Progress;
            done = top?.ProgressDone ?? -1;
            total = top?.ProgressTotal ?? -1;
            // Formatted under the gate, with the numbers it belongs to: formatting outside it
            // could pair a count from one publish with a total from the next.
            progressText = total > 0 ? top?.Describe(done, total) ?? "" : "";
            canCancel = running is { Cancellation: not null, IsCancellationRequested: false };
            cancelling = running is { IsCancellationRequested: true };
        }

        var changed = new List<string>();
        void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            changed.Add(name);
        }
        Set(ref _isBusy, busy, nameof(IsBusy));
        Set(ref _isWorking, working, nameof(IsWorking));
        Set(ref _isIndicatorVisible, visible, nameof(IsIndicatorVisible));
        Set(ref _label, label, nameof(Label));
        Set(ref _scope, scope, nameof(Scope));
        Set(ref _pageIndex, pageIndex, nameof(PageIndex));
        Set(ref _area, area, nameof(Area));
        Set(ref _progress, progress, nameof(Progress));
        Set(ref _progressDone, done, nameof(ProgressDone));
        Set(ref _progressTotal, total, nameof(ProgressTotal));
        Set(ref _progressText, progressText, nameof(ProgressText));
        Set(ref _canCancel, canCancel, nameof(CanCancel));
        Set(ref _isCancelling, cancelling, nameof(IsCancelling));
        if (changed.Contains(nameof(Progress)))
            changed.Add(nameof(HasProgress));
        if (changed.Contains(nameof(IsIndicatorVisible)) || changed.Contains(nameof(Scope)))
        {
            changed.Add(nameof(ShowsStrip));
            changed.Add(nameof(ShowsPageSpinner));
        }
        foreach (var name in changed)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One piece of work. Disposing it ends it.</summary>
    public sealed class Operation : IDisposable
    {
        private readonly BusyState _owner;

        private bool _disposed;

        private readonly Func<int, int, string>? _progressFormat;

        internal Operation(BusyState owner, string label, bool blocksEditing, BusyScope scope, int pageIndex,
                           PdfRect? area, bool cancellable, Func<int, int, string>? progressFormat)
        {
            _owner = owner;
            _progressFormat = progressFormat;
            Label = label;
            BlocksEditing = blocksEditing;
            Scope = scope;
            PageIndex = pageIndex;
            Area = area;
            Cancellation = cancellable ? new CancellationTokenSource() : null;
        }

        public string Label { get; private set; }
        public bool BlocksEditing { get; }
        public BusyScope Scope { get; }
        public int PageIndex { get; }
        public PdfRect? Area { get; }

        /// <summary>This operation's own source, or null when it was not begun cancellable.</summary>
        internal CancellationTokenSource? Cancellation { get; }

        /// <summary>
        /// The token the work watches. <see cref="CancellationToken.None"/> for an operation that
        /// was not begun cancellable, so a caller may pass this straight through either way.
        /// </summary>
        public CancellationToken CancellationToken =>
            Cancellation is { } source && !_disposed ? source.Token : CancellationToken.None;

        /// <summary>Whether someone has asked this operation to stop.</summary>
        public bool IsCancellationRequested => Cancellation?.IsCancellationRequested ?? false;

        // Guarded by the owner's gate, and read by PublishNow under it.
        internal double? Progress { get; private set; }
        internal int ProgressDone { get; private set; } = -1;
        internal int ProgressTotal { get; private set; } = -1;

        /// <summary>
        /// How far through the work is, in units the app can word — "page 37 of 2,000". A
        /// <paramref name="total"/> of 0 or less means the work cannot be counted after all, and
        /// puts the indicator back to indeterminate. Safe from any thread; the app's bindings are
        /// raised on the state's own context.
        /// </summary>
        public void Report(int done, int total)
        {
            lock (_owner._gate)
            {
                if (total <= 0)
                {
                    Progress = null;
                    ProgressDone = -1;
                    ProgressTotal = -1;
                }
                else
                {
                    ProgressDone = Math.Clamp(done, 0, total);
                    ProgressTotal = total;
                    Progress = (double)ProgressDone / total;
                }
            }
            _owner.Publish();
        }

        /// <summary>
        /// This operation's progress in words, from the format the app gave <see cref="Begin"/>.
        /// A format that throws would take down the publish that every other binding rides on,
        /// so it cannot: a bad format costs its own line and nothing else.
        /// </summary>
        internal string Describe(int done, int total)
        {
            if (_progressFormat is null)
                return "";
            try
            {
                return _progressFormat(done, total);
            }
            catch (FormatException)
            {
                return "";
            }
        }

        /// <summary>"Saving…" becomes "Checking the saved file…". Safe from any thread.</summary>
        public void SetLabel(string label)
        {
            lock (_owner._gate)
                Label = label;
            _owner.Publish();
        }

        /// <summary>Raises this operation's own token. Called through <see cref="BusyState.RequestCancel"/>.</summary>
        internal void RequestCancel()
        {
            // Cancel() runs the token's registered callbacks, so it is deliberately not called
            // under the owner's gate: a callback that ended the operation would deadlock on it.
            if (_disposed)
                return;
            try
            {
                Cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the check and here: the work has ended, so there is
                // nothing to stop, which is the outcome a cancel wanted anyway.
            }
        }

        public void Dispose()
        {
            _owner.End(this);
            // After End, so nothing can still observe a token that is about to go away.
            _disposed = true;
            Cancellation?.Dispose();
        }
    }
}
