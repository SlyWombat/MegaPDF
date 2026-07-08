namespace MegaPDF.Core.Editing;

/// <summary>
/// Bounded undo/redo stack (SDD §4.2: command pattern, 500 ops).
/// Not thread-safe; owned by the document's edit session.
/// </summary>
public sealed class UndoStack(int capacity = UndoStack.DefaultCapacity)
{
    public const int DefaultCapacity = 500;

    private readonly List<IEditOperation> _done = [];
    private readonly Stack<IEditOperation> _undone = new();

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    /// <summary>The operation the next Undo/Redo would act on, or null. Lets callers refresh affected UI.</summary>
    public IEditOperation? PeekUndo => _done.Count > 0 ? _done[^1] : null;
    public IEditOperation? PeekRedo => _undone.Count > 0 ? _undone.Peek() : null;

    /// <summary>Raised whenever CanUndo/CanRedo may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Applies the operation and records it. Clears the redo history.</summary>
    public void Do(IEditOperation operation)
    {
        operation.Apply();
        _done.Add(operation);
        if (_done.Count > capacity)
            _done.RemoveAt(0);
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo)
            return;
        var op = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        op.Revert();
        _undone.Push(op);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo)
            return;
        var op = _undone.Pop();
        op.Apply();
        _done.Add(op);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
