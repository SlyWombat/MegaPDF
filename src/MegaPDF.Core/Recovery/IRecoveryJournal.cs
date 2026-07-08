using MegaPDF.Core.Editing;

namespace MegaPDF.Core.Recovery;

/// <summary>
/// Session journal for crash recovery (SDD §3.4): records unsaved edit operations
/// under %LOCALAPPDATA%\MegaPDF\Recovery; after an unclean exit the next launch
/// offers a one-click restore. Serializes the same IEditOperation objects the
/// undo stack holds (SDD §4.2). Format is versioned and backward-compatible (SDD §5.3).
/// </summary>
public interface IRecoveryJournal
{
    void BeginSession(string documentPath);
    void Record(IEditOperation operation);
    void MarkSaved();
    void EndSession();

    /// <summary>Journals left behind by unclean exits, offered for restore on launch.</summary>
    IReadOnlyList<string> FindRecoverableSessions();
}
