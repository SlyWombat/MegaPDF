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

    /// <summary>
    /// Every operation the stack still holds, done and undone. For the operations that own a
    /// native handle the core keeps alive for them — a detached object, or since #174 a deleted
    /// page — so an app clearing its history can hand those back rather than leave them held
    /// until the document closes.
    /// </summary>
    public IEnumerable<IEditOperation> All => _done.Concat(_undone);

    /// <summary>Applies the operation and records it. Clears the redo history.</summary>
    public void Do(IEditOperation operation)
    {
        operation.Apply();
        RebindRedactionMarks(operation);
        _done.Add(operation);
        if (_done.Count > capacity)
            _done.RemoveAt(0);
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Records an operation the caller has already carried out, without applying it again.
    ///
    /// For the marks a redaction gesture makes (#329): the engine call that answers "did
    /// this drag cover text?" *makes* the marks as it answers, so the work is done by the
    /// time the operation exists and applying it would mark the page twice. The caller
    /// reads back what was made and records the operation that can take it back.
    /// </summary>
    public void Record(IEditOperation operation)
    {
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
        try
        {
            op.Revert();
        }
        catch
        {
            _done.Add(op);   // keep the history honest: a revert that threw did not happen
            throw;
        }
        _undone.Push(op);
        RebindRedactionMarks(op);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo)
            return;
        var op = _undone.Pop();
        try
        {
            op.Apply();
        }
        catch
        {
            _undone.Push(op);
            throw;
        }
        _done.Add(op);
        RebindRedactionMarks(op);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hands every other operation the mark ids the one just applied or reverted re-marked
    /// under (#429).
    ///
    /// A redaction mark cannot come back under the id it had: the core hands out a fresh one and
    /// never reuses the old. So the operation that re-marked is the only one that knows the
    /// mark's new id, and every operation still holding the old one — the move recorded before a
    /// removal, the marking under it, the clear that swept it up — would otherwise name a mark
    /// the core no longer has, and quietly do nothing when its turn came.
    /// </summary>
    private void RebindRedactionMarks(IEditOperation source)
    {
        if (source is not IRedactionMarkEdit edit || edit.LastRenames.Count == 0)
            return;
        var renames = edit.LastRenames;
        foreach (var op in _done.Concat(_undone))
        {
            if (!ReferenceEquals(op, source) && op is IRedactionMarkEdit other)
                other.Rebind(renames);
        }
    }
}
