using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace MegaPDF.App;

/// <summary>
/// External-open handling for #348 phase 2: a second launch — an Explorer double-click, an
/// "Open with", a redirected command line — lands in this process's already-running window
/// instead of spawning a second one. <see cref="Program"/> decides *whether* this process is
/// the one that keeps running (<c>AppInstance.FindOrRegisterForKey</c>) and wires <see
/// cref="OnActivatedFromAnotherInstance"/> to the instance's <c>Activated</c> event before
/// <c>Application.Start</c> runs; everything here is what happens once an activation — this
/// process's own launch, or one redirected from another — actually needs routing to a tab.
/// </summary>
public partial class App
{
    // --- Every live MainWindow in this process ---
    // Phase 1 only ever needed "the" window; "New Window" (Ctrl+Shift+N) made that untrue,
    // and a redirected activation needs somewhere to land even when there is more than one.

    private static readonly List<MainWindow> _windows = [];
    private static MainWindow? _mostRecentlyActiveWindow;

    /// <summary>Called once from each <see cref="MainWindow"/>'s constructor.</summary>
    internal static void RegisterWindow(MainWindow window)
    {
        _windows.Add(window);
        _mostRecentlyActiveWindow ??= window;
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _mostRecentlyActiveWindow = window;
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_mostRecentlyActiveWindow == window)
                _mostRecentlyActiveWindow = _windows.LastOrDefault();
        };
    }

    // --- Burst serialization (plan §6 item 7) ---
    // Explorer with N files selected launches N near-simultaneous processes, each of which
    // redirects here as its own Activated call. DocumentViewModel.OpenDocumentAsync returning
    // silently when its own Busy is set guards one document at a time, not a burst hitting the
    // router at once — without this gate, two near-simultaneous opens of the *same* path could
    // both fail to see each other's new tab (its DocumentPath isn't set until the open
    // finishes) and open it twice. Serialising the whole find-or-activate-or-open decision,
    // not just each document's own busy flag, is what actually closes that gap.
    private static readonly SemaphoreSlim _openGate = new(1, 1);

    /// <summary>
    /// The process-wide find-or-activate entry point: activates an existing tab for a path
    /// that is already open in *any* window, otherwise opens it into
    /// <paramref name="preferredWindow"/> (falling back to the most recently active window,
    /// or a brand-new one if none exists yet). Every external open funnels through this one
    /// at a time — association launches, a redirected drag-and-drop, a burst of Explorer
    /// selections — never two at once.
    /// </summary>
    internal static async Task OpenExternalPathsAsync(IReadOnlyList<string> paths, MainWindow? preferredWindow = null)
    {
        if (paths.Count == 0)
            return;

        await _openGate.WaitAsync();
        try
        {
            foreach (var path in paths)
                await OpenExternalPathAsync(path, preferredWindow);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private static async Task OpenExternalPathAsync(string path, MainWindow? preferredWindow)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return; // a malformed path from a redirected launch is dropped, not thrown
        }

        MainWindow? owner = null;
        DocumentViewModel? existing = null;
        foreach (var window in _windows)
        {
            existing = window.Shell.Documents.FirstOrDefault(d =>
                d.DocumentPath is { } open && Core.Recovery.LaunchedDocument.SameFile(open, full));
            if (existing is not null)
            {
                owner = window;
                break;
            }
        }

        owner ??= preferredWindow ?? _mostRecentlyActiveWindow ?? _windows.FirstOrDefault();
        if (owner is null)
        {
            owner = new MainWindow();
            owner.Activate();
        }

        if (existing is not null)
            owner.Shell.Active = existing;
        else
            await owner.Shell.OpenInTabAsync(full);

        // Windows will not let a redirected launch steal the foreground outright, and that is
        // the correct, Notepad-like behaviour (plan §6 item 11) — window.Activate() plus the
        // tab strip's own selection is enough; SetForegroundWindow-style tricks are not used.
        owner.Activate();
    }

    // --- Activation-argument extraction (plan §3a) ---
    // Shared by the cold-start path (OnLaunched) and a redirected activation
    // (OnActivatedFromAnotherInstance), so both read File/Launch activation args the same way
    // instead of OnLaunched keeping its own separate, narrower argv[1]-only read.

    internal static IReadOnlyList<string> ExtractPdfPaths(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.File && args.Data is FileActivatedEventArgs fileArgs)
        {
            return fileArgs.Files.OfType<IStorageFile>()
                .Select(f => f.Path)
                .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        if (args.Kind == ExtendedActivationKind.Launch && args.Data is Windows.ApplicationModel.Activation.LaunchActivatedEventArgs launchArgs)
        {
            // The raw command line, split the way CommandLineToArgvW would (see CommandLine.cs)
            // — this is what actually delivers a path with a space in it intact.
            return CommandLine.Parse(launchArgs.Arguments)
                .Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return [];
    }

    // --- Redirected activation ---
    // Program.cs subscribes OnActivatedFromAnotherInstance on AppInstance.GetCurrent() before
    // Application.Start runs, so a second launch that redirects here the instant after this
    // process registers is never missed — subscribing later (e.g. at the end of OnLaunched)
    // would leave a gap during the splash delay. The event fires on a worker thread (per the
    // AppLifecycle docs), so everything here that touches the UI marshals through the
    // dispatcher queue captured once this process's App actually has one.

    private static DispatcherQueue? _uiDispatcherQueue;
    private static readonly List<AppActivationArguments> _pendingActivations = [];
    private static readonly object _pendingActivationsLock = new();

    /// <summary>
    /// Called from <see cref="App"/>'s constructor — the first point a dispatcher queue
    /// exists — and replays any activation that raced in before that (subscribing happens in
    /// Program.cs, ahead of Application.Start, specifically so that gap is as small as
    /// possible; this is what closes the remainder of it rather than dropping the activation).
    /// </summary>
    private static void DrainPendingActivations(DispatcherQueue queue)
    {
        List<AppActivationArguments> pending;
        lock (_pendingActivationsLock)
        {
            _uiDispatcherQueue = queue;
            pending = [.. _pendingActivations];
            _pendingActivations.Clear();
        }
        foreach (var args in pending)
            queue.TryEnqueue(() => _ = RouteActivationAsync(args));
    }

    internal static void OnActivatedFromAnotherInstance(object? sender, AppActivationArguments e)
    {
        DispatcherQueue? queue;
        lock (_pendingActivationsLock)
        {
            queue = _uiDispatcherQueue;
            if (queue is null)
            {
                _pendingActivations.Add(e);
                return;
            }
        }
        queue.TryEnqueue(() => _ = RouteActivationAsync(e));
    }

    private static async Task RouteActivationAsync(AppActivationArguments e)
    {
        var paths = ExtractPdfPaths(e);
        if (paths.Count > 0)
            await OpenExternalPathsAsync(paths);
    }
}
