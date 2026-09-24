using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using Xunit;

namespace MegaPDF.Core.Tests;

public class RecoveryJournalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-recovery-tests-").FullName;
    private readonly string _journalDir;
    private readonly PdfiumEngine _engine = new();

    public RecoveryJournalTests()
    {
        _journalDir = Path.Combine(_dir, "journals");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void CleanClose_LeavesNothingRecoverable()
    {
        var docPath = WriteFormPdf();
        using (var journal = new RecoveryJournal(_journalDir))
        {
            journal.BeginSession(docPath);
            journal.Record(new CheckToggleEntry(0, "Agree"));
            journal.EndSession();
        }

        using var scanner = new RecoveryJournal(_journalDir);
        Assert.Empty(scanner.FindRecoverableSessions());
    }

    [Fact]
    public void SavedSession_IsNotRecoverable()
    {
        var docPath = WriteFormPdf();
        using var journal = new RecoveryJournal(_journalDir);
        journal.BeginSession(docPath);
        journal.Record(new CheckToggleEntry(0, "Agree"));
        journal.MarkSaved(docPath);
        // Simulated crash after save: writer abandoned without EndSession.

        using var scanner = new RecoveryJournal(_journalDir);
        Assert.Empty(scanner.FindRecoverableSessions());
    }

    [Fact]
    public void Crash_ThenReplay_ReproducesEdits()
    {
        var docPath = WriteFormPdf();

        // Session that "crashes" (journal never ended).
        {
            using var journal = new RecoveryJournal(_journalDir);
            journal.BeginSession(docPath);
            journal.Record(new FormTextEntry(0, "FullName", "Pat Q. Administrator"));
            journal.Record(new CheckToggleEntry(0, "Agree"));
            journal.Record(new AddMarkEntry(0, 200, 300, 18, 18, "mark:recovered"));
            // No EndSession — the writer is simply dropped, like a killed process.
        }

        using var scanner = new RecoveryJournal(_journalDir);
        var session = Assert.Single(scanner.FindRecoverableSessions());
        Assert.Equal(docPath, session.DocumentPath);
        Assert.Equal(3, session.EntryCount);

        var entries = RecoveryJournal.LoadEntries(session.JournalPath);
        using var doc = _engine.Open(docPath);
        var applied = JournalReplayer.Replay(doc, entries);
        Assert.Equal(3, applied);

        using var page = doc.GetPage(0);
        Assert.Equal("Pat Q. Administrator", page.GetFormFields().Single(f => f.Kind == FormFieldKind.Text).Value);
        Assert.True(page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox).IsChecked);
        Assert.Contains(page.GetStamps(), s => s.Id == "mark:recovered");
    }

    [Fact]
    public void UndoneEdit_ReplaysToUndoneState()
    {
        var docPath = WriteFormPdf();
        using var doc = _engine.Open(docPath);
        var stack = new UndoStack();
        PdfFormField box;
        using (var page = doc.GetPage(0))
            box = page.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox);

        var op = new CheckboxToggleOperation(doc, 0, box);
        var journal = new List<JournalEntry>();
        stack.Do(op);
        journal.Add(op.ToJournalEntry(inverse: false));
        stack.Undo();
        journal.Add(op.ToJournalEntry(inverse: true));

        // Replaying toggle + inverse toggle onto a fresh copy lands unchecked.
        using var fresh = _engine.Open(docPath);
        JournalReplayer.Replay(fresh, journal);
        using var freshPage = fresh.GetPage(0);
        Assert.False(freshPage.GetFormFields().Single(f => f.Kind == FormFieldKind.Checkbox).IsChecked);
    }

    [Fact]
    public void SignatureEntry_RoundTripsPixelsThroughJournal()
    {
        var docPath = WriteFormPdf();
        var bgra = new byte[8 * 4 * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 0x80; bgra[i + 3] = 0xFF; }

        using (var journal = new RecoveryJournal(_journalDir))
        {
            journal.BeginSession(docPath);
            journal.Record(new AddSignatureEntry(0, 100, 400, 90, 45, "sig:recovered", JournalBlob.Pack(bgra), 8, 4));
        }

        using var scanner = new RecoveryJournal(_journalDir);
        var session = Assert.Single(scanner.FindRecoverableSessions());
        var entries = RecoveryJournal.LoadEntries(session.JournalPath);

        using var doc = _engine.Open(docPath);
        Assert.Equal(1, JournalReplayer.Replay(doc, entries));
        using var page = doc.GetPage(0);
        Assert.Contains(page.GetStamps(), s => s.Id == "sig:recovered");
    }

    [Fact]
    public void TwoInstancesWithOneDocument_KeepTheirOwnJournals_AndCloseWithoutThrowing()
    {
        // #145: two app instances with the same PDF open shared one journal file; the second
        // truncated the first's entries, and closing crashed on the delete.
        var docPath = WriteFormPdf();
        using var first = new RecoveryJournal(_journalDir);
        using var second = new RecoveryJournal(_journalDir);
        first.BeginSession(docPath);
        first.Record(new CheckToggleEntry(0, "Agree"));
        second.BeginSession(docPath);
        second.Record(new FormTextEntry(0, "FullName", "Second instance"));

        Assert.NotNull(first.JournalPath);
        Assert.NotNull(second.JournalPath);
        Assert.NotEqual(first.JournalPath, second.JournalPath);

        // A journal still being written is nobody's crashed session.
        using (var scanner = new RecoveryJournal(_journalDir))
            Assert.Empty(scanner.FindRecoverableSessions());

        second.EndSession();
        second.EndSession();
        first.MarkSaved(docPath);
        first.Record(new CheckToggleEntry(0, "Agree"));

        // The first instance then dies without a consented close: its edit is still recoverable.
        first.Dispose();
        using var after = new RecoveryJournal(_journalDir);
        var session = Assert.Single(after.FindRecoverableSessions());
        Assert.Equal(1, session.EntryCount);
    }

    [Fact]
    public void TwoLiveSessionsOnDifferentPaths_InOneProcess_AThirdInstancesScanSeesNeither()
    {
        // #348 phase 1: with tabs, one process can hold several live sessions at once
        // (one RecoveryJournal per open tab). The scan that finds crashed sessions moves
        // from "per window" to "once per app launch", so it must still see none of the
        // journals that this same process is actively writing — the exclusive lock that
        // already excludes another *process*'s live journal excludes another *tab*'s too.
        var docA = WriteFormPdf();
        var docB = WriteFormPdf();
        using var tabA = new RecoveryJournal(_journalDir);
        using var tabB = new RecoveryJournal(_journalDir);
        tabA.BeginSession(docA);
        tabA.Record(new CheckToggleEntry(0, "Agree"));
        tabB.BeginSession(docB);
        tabB.Record(new FormTextEntry(0, "FullName", "Tab B"));

        using var scanner = new RecoveryJournal(_journalDir);
        Assert.Empty(scanner.FindRecoverableSessions());
    }

    [Fact]
    public void EndSession_OnOneTab_LeavesAnotherTabsJournalFileUntouched()
    {
        // #348 phase 1: closing one dirty tab (Save/Don't Save answered, or a clean close)
        // must not disturb a sibling tab's still-live journal — a Cancel on tab 2's close
        // question, or simply tab 1 finishing first, must leave tab 2 fully recoverable.
        var docA = WriteFormPdf();
        var docB = WriteFormPdf();
        using var tabA = new RecoveryJournal(_journalDir);
        using var tabB = new RecoveryJournal(_journalDir);
        tabA.BeginSession(docA);
        tabA.Record(new CheckToggleEntry(0, "Agree"));
        tabB.BeginSession(docB);
        tabB.Record(new FormTextEntry(0, "FullName", "Tab B"));
        var tabAPath = tabA.JournalPath!;
        var tabBPath = tabB.JournalPath!;

        tabA.EndSession(); // tab A closed cleanly (or its unsaved changes were discarded)

        Assert.False(File.Exists(tabAPath)); // tab A's own journal is gone
        Assert.True(File.Exists(tabBPath)); // tab B's file was not touched by A's close

        // Tab B is still a live session for anyone else scanning, and still crashes
        // recoverably once it, too, is abandoned without EndSession — with exactly the
        // one entry it recorded, proving A's close never wrote through B's handle.
        using (var scanner = new RecoveryJournal(_journalDir))
            Assert.Empty(scanner.FindRecoverableSessions());
        tabB.Dispose();
        using var after = new RecoveryJournal(_journalDir);
        var session = Assert.Single(after.FindRecoverableSessions());
        Assert.Equal(docB, session.DocumentPath);
        Assert.Equal(1, session.EntryCount);
    }

    [Fact]
    public void AJournalAnotherProcessHolds_IsSkipped_NeverTruncated()
    {
        var docPath = WriteFormPdf();
        string heldPath;
        using (var crashed = new RecoveryJournal(_journalDir))
        {
            crashed.BeginSession(docPath);
            crashed.Record(new CheckToggleEntry(0, "Agree"));
            heldPath = crashed.JournalPath!;
        } // Dispose without EndSession: the file stays, as after a crash

        var before = File.ReadAllBytes(heldPath);
        using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var journal = new RecoveryJournal(_journalDir);
            journal.BeginSession(docPath);
            Assert.NotNull(journal.JournalPath);
            Assert.NotEqual(heldPath, journal.JournalPath);
            journal.Record(new FormTextEntry(0, "FullName", "Beside it"));

            // Discarding or ending around a held file does not throw.
            RecoveryJournal.Discard(heldPath);
            journal.EndSession();
            Assert.Null(journal.JournalPath);
        }
        Assert.Equal(before, File.ReadAllBytes(heldPath));
    }

    [Fact]
    public void Dispose_WithoutEndSession_KeepsTheJournal()
    {
        // D1 (#145): an exit nobody agreed to must not delete the only record of unsaved edits.
        var docPath = WriteFormPdf();
        using (var journal = new RecoveryJournal(_journalDir))
        {
            journal.BeginSession(docPath);
            journal.Record(new CheckToggleEntry(0, "Agree"));
        }
        using var scanner = new RecoveryJournal(_journalDir);
        Assert.Single(scanner.FindRecoverableSessions());
    }

    [Fact]
    public void TornFinalLine_KeepsEarlierEntries()
    {
        var docPath = WriteFormPdf();
        string journalPath;
        using (var journal = new RecoveryJournal(_journalDir))
        {
            journal.BeginSession(docPath);
            journal.Record(new CheckToggleEntry(0, "Agree"));
        }
        using (var scanner = new RecoveryJournal(_journalDir))
            journalPath = Assert.Single(scanner.FindRecoverableSessions()).JournalPath;

        // Simulate a crash mid-append.
        File.AppendAllText(journalPath, "{\"$op\":\"formText\",\"PageIndex\":0,\"Fi");

        var entries = RecoveryJournal.LoadEntries(journalPath);
        Assert.Single(entries);
        Assert.IsType<CheckToggleEntry>(entries[0]);
    }

    [Fact]
    public void ProtectedDocument_IsNeverJournaled_EvenAcrossASaveAndACrash()
    {
        // #135: journal entries carry document text; a document opened with a password
        // must not leave it on disk unencrypted.
        var docPath = WriteFormPdf();
        using (var journal = new RecoveryJournal(_journalDir))
        {
            journal.BeginSession(docPath, contentIsProtected: true);
            journal.Record(new FormTextEntry(0, "FullName", "Pat Q. Administrator"));
            journal.MarkSaved(docPath);
            journal.Record(new FormTextEntry(0, "FullName", "Still private after the save"));
            // Simulated crash: the session is never ended.
        }

        Assert.Empty(Directory.GetFiles(_journalDir));
        using var scanner = new RecoveryJournal(_journalDir);
        Assert.Empty(scanner.FindRecoverableSessions());
    }

    [Fact]
    public void ProtectedSession_RemovesAnOlderJournal_OnceItsEntriesHaveBeenRead()
    {
        // #133, #135: an earlier version journaled protected documents. A restore reads the
        // entries first; opening the document then must not leave that plaintext behind.
        var docPath = WriteFormPdf();
        using (var old = new RecoveryJournal(_journalDir))
        {
            old.BeginSession(docPath);
            old.Record(new CheckToggleEntry(0, "Agree"));
        }
        string journalPath;
        using (var scanner = new RecoveryJournal(_journalDir))
            journalPath = Assert.Single(scanner.FindRecoverableSessions()).JournalPath;

        var entries = RecoveryJournal.LoadEntries(journalPath);
        using var journal = new RecoveryJournal(_journalDir);
        journal.BeginSession(docPath, contentIsProtected: true);

        Assert.IsType<CheckToggleEntry>(Assert.Single(entries));
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void UnprotectedSession_AfterAProtectedOne_JournalsAgain()
    {
        var protectedDoc = WriteFormPdf();
        var plainDoc = WriteFormPdf();
        using (var journal = new RecoveryJournal(_journalDir))
        {
            journal.BeginSession(protectedDoc, contentIsProtected: true);
            journal.BeginSession(plainDoc);
            journal.Record(new CheckToggleEntry(0, "Agree"));
        } // a crash: the writer lets go, the journal stays (a live journal is never offered, #145)

        using var scanner = new RecoveryJournal(_journalDir);
        Assert.Equal(plainDoc, Assert.Single(scanner.FindRecoverableSessions()).DocumentPath);
    }

    private string WriteFormPdf()
    {
        var path = Path.Combine(_dir, $"form-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithForm());
        return path;
    }
}
