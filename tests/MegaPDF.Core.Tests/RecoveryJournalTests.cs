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

    private string WriteFormPdf()
    {
        var path = Path.Combine(_dir, $"form-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithForm());
        return path;
    }
}
