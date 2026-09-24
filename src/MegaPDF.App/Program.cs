using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MegaPDF.App;

/// <summary>
/// Custom Main (#348 phase 2; <c>DisableXamlGeneratedMain</c> in MegaPDF.App.csproj). The
/// generated Main starts a brand-new App/MainWindow for every launch unconditionally, which
/// is exactly what made every Explorer double-click and "Open with" spawn its own process —
/// the issue's premise. This one decides, before <c>Application.Start</c> runs, whether this
/// launch should hand its activation to an already-running instance instead.
///
/// Diagnostic/automation launches (<c>--screenshot</c>, <c>--engine-check</c>, and everything
/// else the self-test/capture rig passes) are exempt outright — see
/// <see cref="IsAutomationLaunch"/> — and always get their own process, never redirected: the
/// capture rig and CI self-tests must be able to run standalone at any time, including while
/// a person's own window is already open (plan §6 item 10). This is load-bearing: redirecting
/// one of these into a running instance would silently break every screenshot and self-test.
/// </summary>
public static class Program
{
    /// <summary>
    /// Every argument that means "this is a diagnostic/automation launch, not a person opening
    /// a document — run standalone no matter what". Kept in sync by hand with every argument
    /// App.xaml.cs and Screenshot.cs read at launch (<c>Screenshot.ArgumentAfter</c> callers);
    /// there is no single source list to derive this from, since each one is read straight off
    /// <c>Environment.GetCommandLineArgs()</c> where it is used.
    /// </summary>
    private static readonly string[] AutomationArguments =
    [
        "--screenshot", "--screenshot-state", "--engine-check", "--hold",
        "--window", "--theme", "--language", "--term", "--scale",
    ];

    private static bool IsAutomationLaunch(string[] args) =>
        args.Any(a => Array.IndexOf(AutomationArguments, a) >= 0);

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!IsAutomationLaunch(args))
        {
            var keyInstance = AppInstance.FindOrRegisterForKey("MegaPDF.Main");
            if (!keyInstance.IsCurrent)
            {
                // Someone else already holds the key: hand this launch's activation to them
                // and exit instead of opening a second window (#348 phase 2, plan §3a).
                var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                RedirectActivationTo(activatedArgs, keyInstance);
                return;
            }

            // Subscribed before Application.Start runs, not at the end of OnLaunched — an
            // activation redirected here the instant after FindOrRegisterForKey returns must
            // not be missed during the splash delay that follows (ordering matters: an
            // activation can race the app's own startup otherwise).
            keyInstance.Activated += App.OnActivatedFromAnotherInstance;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// Hands this launch's activation to <paramref name="keyInstance"/> and waits for delivery
    /// to complete before this process exits. <c>RedirectActivationToAsync(...).AsTask().Wait()</c>
    /// on this STA thread deadlocks — the calling thread is the one WinRT needs free to pump
    /// the redirect through, and blocking it with a plain <c>.Wait()</c> starves that pump — so
    /// the wait is done with <c>CoWaitForMultipleObjects</c> instead, the pattern the Windows
    /// App SDK's own AppLifecycle/DesktopInstancing sample uses for exactly this reason.
    /// </summary>
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        using var redirected = new ManualResetEvent(false);
        _ = Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            _ = redirected.Set();
        });

        var handle = redirected.SafeWaitHandle.DangerousGetHandle();
        _ = CoWaitForMultipleObjects(0, Infinite, 1, [handle], out _);
    }

    private const uint Infinite = 0xFFFFFFFF;

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);
}
