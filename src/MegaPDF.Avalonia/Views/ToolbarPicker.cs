using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The toolbar's font and size pickers: a ComboBox, styled as one, whose accessibility
/// peer leaves out the parts of its template that have no accessible meaning (#144).
///
/// Fluent's ComboBox template is wrapped in a DataValidationErrors, whose peer is a
/// NoneAutomationPeer. UI Automation skips it, because it is not a control element, but
/// the Mac's accessibility tree lists every child regardless, so VoiceOver found an
/// unnamed "unknown" element beside each picker. It cannot be named (a NoneAutomationPeer
/// takes no name) and cannot be hidden without hiding the picker, so it is left out.
/// </summary>
public sealed class ToolbarPicker : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new PickerAutomationPeer(this);

    private sealed class PickerAutomationPeer(ComboBox owner) : ComboBoxAutomationPeer(owner)
    {
        protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() =>
            base.GetChildrenCore()?.Where(child => child is not NoneAutomationPeer).ToList();
    }
}
