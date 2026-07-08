namespace MegaPDF.Core.Editing;

/// <summary>
/// A reversible edit (SDD §4.2 undo model). Operations capture their target and
/// all state needed to apply and revert; the recovery journal serializes the same
/// operations, so undo and crash recovery share one implementation.
/// </summary>
public interface IEditOperation
{
    /// <summary>Plain-language description for UI ("Undo text edit"), per SDD §2.2.</summary>
    string Description { get; }

    void Apply();
    void Revert();
}
