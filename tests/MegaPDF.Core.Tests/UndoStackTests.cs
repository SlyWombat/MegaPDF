using MegaPDF.Core.Editing;
using Xunit;

namespace MegaPDF.Core.Tests;

public class UndoStackTests
{
    private sealed class CountingOperation : IEditOperation
    {
        public int Applied { get; private set; }
        public int Reverted { get; private set; }
        public string Description => "test";
        public void Apply() => Applied++;
        public void Revert() => Reverted++;
    }

    [Fact]
    public void Do_AppliesOperation_AndEnablesUndo()
    {
        var stack = new UndoStack();
        var op = new CountingOperation();

        stack.Do(op);

        Assert.Equal(1, op.Applied);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Undo_RevertsInReverseOrder_AndRedoReapplies()
    {
        var stack = new UndoStack();
        var first = new CountingOperation();
        var second = new CountingOperation();
        stack.Do(first);
        stack.Do(second);

        stack.Undo();
        Assert.Equal(1, second.Reverted);
        Assert.Equal(0, first.Reverted);
        Assert.True(stack.CanRedo);

        stack.Redo();
        Assert.Equal(2, second.Applied);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Do_AfterUndo_ClearsRedoHistory()
    {
        var stack = new UndoStack();
        stack.Do(new CountingOperation());
        stack.Undo();

        stack.Do(new CountingOperation());

        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoRedo_WhenEmpty_AreNoOps()
    {
        var stack = new UndoStack();
        stack.Undo();
        stack.Redo();
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Capacity_DropsOldestOperations()
    {
        var stack = new UndoStack(capacity: 2);
        var oldest = new CountingOperation();
        stack.Do(oldest);
        stack.Do(new CountingOperation());
        stack.Do(new CountingOperation());

        stack.Undo();
        stack.Undo();
        stack.Undo(); // beyond capacity — oldest was dropped, so this is a no-op

        Assert.Equal(0, oldest.Reverted);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void Changed_RaisedOnDoUndoRedo()
    {
        var stack = new UndoStack();
        var raised = 0;
        stack.Changed += (_, _) => raised++;

        stack.Do(new CountingOperation());
        stack.Undo();
        stack.Redo();

        Assert.Equal(3, raised);
    }
}
