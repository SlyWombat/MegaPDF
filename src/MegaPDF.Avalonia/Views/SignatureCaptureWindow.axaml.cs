using Avalonia.Controls;
using MegaPDF.Core.Imaging;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Captures a drawn signature. Returns the cleaned bitmap and the name the user gave
/// it, or null if they cancelled.
/// </summary>
public partial class SignatureCaptureWindow : Window
{
    private readonly SignaturePad _pad = new();

    internal SignatureBitmap? Result { get; private set; }
    internal string ResultName { get; private set; } = "Signature";

    public SignatureCaptureWindow()
    {
        InitializeComponent();

        PadHost.Children.Add(_pad);

        // Nothing to save until something has been drawn — a blank signature in the
        // library is only ever a mistake.
        _pad.StrokesChanged += (_, _) => SaveButton.IsEnabled = !_pad.IsEmpty;

        ClearButton.Click += (_, _) =>
        {
            _pad.Clear();
            SaveButton.IsEnabled = false;
        };

        CancelButton.Click += (_, _) => Close();

        SaveButton.Click += (_, _) =>
        {
            Result = _pad.ToSignature();
            var typed = NameBox.Text;
            ResultName = string.IsNullOrWhiteSpace(typed) ? "Signature" : typed.Trim();
            Close();
        };
    }
}
