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
    private readonly Stopwatch _clock = Stopwatch.StartNew();
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

    /// <summary>The app's timings, raising changes on the current synchronization context (create it on the UI thread).</summary>
    public BusyState()
        : this(DefaultShowAfter, DefaultMinimumVisible, SynchronizationContext.Current)
    {
    }

    /// <param name="showAfter">How long work runs before the indicator shows.</param>
    /// <param name="minimumVisible">How long a shown indicator stays.</param>
    /// <param name="context">Where property changes are raised; null raises them on whichever thread changed the state.</param>
    public BusyState(TimeSpan showAfter, TimeSpan minimumVisible, SynchronizationContext? context)
    {
        _showAfter = showAfter;
        _minimumVisible = minimumVisible;
        _context = context;
    }

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

    /// <summary>The strip under the toolbar shows.</summary>
    public bool ShowsStrip => _isIndicatorVisible && _scope == BusyScope.Document;

    /// <summary>A spinner on the page or line shows.</summary>
    public bool ShowsPageSpinner => _isIndicatorVisible && _scope == BusyScope.Page;

    /// <summary>Starts an operation. Dispose the result when the work ends, whatever the outcome.</summary>
    public Operation Begin(string label, bool blocksEditing = true, BusyScope scope = BusyScope.Document,
                           int pageIndex = -1, PdfRect? area = null)
    {
        var operation = new Operation(this, label, blocksEditing, scope, pageIndex, area);
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
                                     BusyScope scope = BusyScope.Document, int pageIndex = -1, PdfRect? area = null)
    {
        using var operation = Begin(label, blocksEditing, scope, pageIndex, area);
        return await work(operation);
    }

    /// <inheritdoc cref="RunAsync{T}"/>
    public async Task RunAsync(string label, Func<Operation, Task> work, bool blocksEditing = true,
                               BusyScope scope = BusyScope.Document, int pageIndex = -1, PdfRect? area = null)
    {
        using var operation = Begin(label, blocksEditing, scope, pageIndex, area);
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
        await Task.Delay(_showAfter).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _generation || _operations.Count == 0 || _indicatorShown)
                return;
            _indicatorShown = true;
            _shownAt = _clock.Elapsed;
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
                    var shownFor = _clock.Elapsed - _shownAt;
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
        await Task.Delay(delay).ConfigureAwait(false);
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
        bool busy, working, visible;
        string label;
        BusyScope scope;
        int pageIndex;
        PdfRect? area;
        lock (_gate)
        {
            var top = _operations.Count > 0 ? _operations[^1] : _lastShown;
            busy = _operations.Any(o => o.BlocksEditing);
            working = _operations.Count > 0;
            visible = _indicatorShown;
            label = top?.Label ?? "";
            scope = top?.Scope ?? BusyScope.Document;
            pageIndex = top?.PageIndex ?? -1;
            area = top?.Area;
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

        internal Operation(BusyState owner, string label, bool blocksEditing, BusyScope scope, int pageIndex, PdfRect? area)
        {
            _owner = owner;
            Label = label;
            BlocksEditing = blocksEditing;
            Scope = scope;
            PageIndex = pageIndex;
            Area = area;
        }

        public string Label { get; private set; }
        public bool BlocksEditing { get; }
        public BusyScope Scope { get; }
        public int PageIndex { get; }
        public PdfRect? Area { get; }

        /// <summary>"Saving…" becomes "Checking the saved file…". Safe from any thread.</summary>
        public void SetLabel(string label)
        {
            lock (_owner._gate)
                Label = label;
            _owner.Publish();
        }

        public void Dispose() => _owner.End(this);
    }
}
