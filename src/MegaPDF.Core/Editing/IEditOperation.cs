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

    /// <summary>
    /// Whether applying this leaves the file different from what is on disk: the document
    /// becomes unsaved, the page is re-rendered, and the crash journal records it. True for
    /// everything that edits the document, which is why it defaults that way — the safe
    /// answer is the one that keeps the unsaved flag honest.
    ///
    /// A redaction **mark** is the exception (#173): it lives in the core and is never
    /// written to the file (ADR-005 decision 1), so marking, moving and removing one must
    /// not raise the unsaved flag, must not write a recovery entry and must not spend a
    /// re-render — the overlay draw is the whole visible change (ADR-005 decision 4).
    /// The same rule the mobile apps carry as <c>changesDocument</c> (#329).
    /// </summary>
    bool ChangesTheFile => true;

    void Apply();
    void Revert();
}
