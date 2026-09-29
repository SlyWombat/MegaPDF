namespace MegaPDF.Core.Viewing;

/// <summary>What one press of Escape does, in reading mode or out of it.</summary>
public enum ReadingModeStep
{
    /// <summary>None of reading mode's levels applies — Escape means whatever it meant before.</summary>
    Nothing,

    /// <summary>The find bar is open over the reading view; close it and stay where we are.</summary>
    CloseFind,

    /// <summary>Full screen is on; drop it, and stay in reading mode.</summary>
    LeaveFullScreen,

    /// <summary>Reading mode is on with nothing above it; put the chrome back.</summary>
    LeaveReadingMode,
}

/// <summary>
/// The two decisions reading mode (#168) makes that are not UI, so that they can be
/// stated once and tested off a UI thread.
///
/// Both come from <c>docs/reading-mode-plan.md</c> §7, which names them as the risks:
/// an Escape that skips a level costs a person a tool state they did not mean to drop,
/// and a floating bar that fades while a screen reader is running is a control nobody
/// can get back. Neither depends on a window, a toolkit or a platform — only on four
/// booleans — and neither is reachable by a test that has to open a window, which on
/// Windows is a test CI cannot run at all (#462).
///
/// The WinUI window (#504/#510) calls both. The Avalonia leg (#505/#511) landed first
/// and carries the same ladder inline in <c>MainWindow.ReadingMode.cs</c>; if the two
/// ever need to agree on more than they do today, this is where the agreement goes.
/// Keeping it here rather than in one app is also what lets the ladder be asserted on
/// every push on three operating systems, which is the whole point of putting it here.
/// </summary>
public static class ReadingMode
{
    /// <summary>
    /// One level back, and exactly one: the find bar, then full screen, then reading
    /// mode, then nothing new (plan §7, #504).
    ///
    /// The order is the whole of it. Escape in reading mode usually means "give me the
    /// toolbar back", and every level that is skipped — or swallowed — is a state the
    /// person did not ask to lose. <see cref="ReadingModeStep.Nothing"/> is not "do
    /// nothing": it is "this key was not ours", and the caller lets Escape go on to do
    /// what it always did (cancel an armed tool, drop a selection, leave a focused
    /// region).
    /// </summary>
    public static ReadingModeStep NextEscape(bool isFindOpen, bool isFullScreen, bool isReadingMode) =>
        isFindOpen ? ReadingModeStep.CloseFind
        : isFullScreen ? ReadingModeStep.LeaveFullScreen
        : isReadingMode ? ReadingModeStep.LeaveReadingMode
        : ReadingModeStep.Nothing;

    /// <summary>
    /// Whether the floating bar is allowed to fade right now (plan §2, §7).
    ///
    /// Both reasons to keep it are the same reason: the person cannot get it back by
    /// waggling the mouse. A screen-reader user is not pointing at anything, and a bar
    /// that faded out from under the Tab key would be a control you can reach once.
    /// </summary>
    public static bool BarMayFade(bool screenReaderRunning, bool barHasKeyboardFocus) =>
        !screenReaderRunning && !barHasKeyboardFocus;
}
