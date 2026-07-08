using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace MegaPDF.App;

/// <summary>
/// The visual host for one page. Exists to expose the protected cursor so hover
/// affordances can communicate capability (SDD §2.2: the cursor is the mode).
/// </summary>
public sealed partial class PageCanvas : Grid
{
    private InputSystemCursorShape? _currentShape;

    public void SetCursorShape(InputSystemCursorShape? shape)
    {
        if (shape == _currentShape)
            return;
        _currentShape = shape;
        ProtectedCursor = shape is null ? null : InputSystemCursor.Create(shape.Value);
    }
}
