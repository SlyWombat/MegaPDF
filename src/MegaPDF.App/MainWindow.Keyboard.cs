using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MegaPDF.App;

/// <summary>
/// What is left of the window's own keyboard-focus policy once the page focus ring and
/// its Tab/Enter/Escape handling moved to <see cref="DocumentView"/> (#348 phase 1):
/// keeping keyboard focus off a disabled or busy toolbar button (#169), and the
/// self-test/screenshot-rig helpers that read or move focus around the toolbar.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeWindowKeyboard()
    {
        // #169: keyboard focus left on a toolbar button must not stay live behind work on
        // the page, a busy state or a dialog.
        Toolbar.LosingFocus += OnToolbarLosingFocus;
        DialogGate.ShowingChanged += showing => Toolbar.IsEnabled = !showing;
    }

    /// <summary>True for an element in the toolbar row (not its More menu, which is a popup).</summary>
    private bool IsInToolbar(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == Toolbar)
                return true;
        }
        return false;
    }

    /// <summary>
    /// A toolbar button that loses focus because it was disabled (#145's busy state, or a
    /// mode ending) hands it to the next tab stop in the row, which is usually Undo; Space
    /// or Enter then undid work with nothing on screen to say Undo had focus (#169).
    /// Focus goes to the active tab's pages instead.
    /// </summary>
    private void OnToolbarLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (args.OldFocusedElement is not Control { IsEnabled: false } old || !IsInToolbar(old))
            return;
        if (args.NewFocusedElement is DependencyObject next && !IsInToolbar(next))
            return;
        ActiveDocumentView?.ParkFocusFromToolbar(args);
    }

    /// <summary>For the `focus` screenshot state: the element that has keyboard focus, by automation id.</summary>
    internal string FocusedAutomationId() =>
        Content.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused
            ? AutomationProperties.GetAutomationId(focused) is { Length: > 0 } id ? id : focused.GetType().Name
            : "(none)";

    /// <summary>
    /// Gives a named toolbar button keyboard focus, for the focus self-check. False when the
    /// button is not on the bar at this width -- a narrow window moves commands into the
    /// overflow, where nothing can be focused (#222); the caller picks another one.
    /// </summary>
    internal bool FocusToolbarButtonForTest(string automationId)
    {
        foreach (var command in Toolbar.PrimaryCommands)
        {
            if (command is Control control && AutomationProperties.GetAutomationId(control) == automationId)
            {
                if (command is AppBarButton { IsInOverflow: true } or AppBarElementContainer { IsInOverflow: true })
                    return false;
                return control.Focus(FocusState.Keyboard);
            }
        }
        return false;
    }

    /// <summary>The automation id of a toolbar button that is on the bar, enabled and focusable
    /// at this width, starting with the caller's preference (#222).</summary>
    internal string? FocusAnyToolbarButtonForTest(params string[] preferred)
    {
        foreach (var id in preferred)
            if (FocusToolbarButtonForTest(id))
                return id;
        foreach (var command in Toolbar.PrimaryCommands)
        {
            if (command is AppBarButton { IsInOverflow: false, IsEnabled: true } button
                && AutomationProperties.GetAutomationId(button) is { Length: > 0 } id
                && button.Focus(FocusState.Keyboard))
                return id;
        }
        return null;
    }

    internal void ClickPagesForTest() => ActiveDocumentView?.ClickPagesForTest();

    /// <summary>The `click` self-test pose (#401): a page click opens the editor and ticks a box.</summary>
    internal Task<bool> ClickFirstRegionsForTest() =>
        ActiveDocumentView?.ClickFirstRegionsForTest() ?? Task.FromResult(false);
    internal bool IsToolbarEnabled => Toolbar.IsEnabled;
}
