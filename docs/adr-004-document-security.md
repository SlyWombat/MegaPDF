# ADR-004: Password-protected documents — opening, permissions, and setting or removing security

**Status:** accepted, 2026-09-14. Dave asked for protected documents to be supported
properly (#131) and for setting and removing passwords to be part of it.

**Amended 2026-09-30** by decision 11 (#558): honouring a permission no longer means
refusing. Decisions 1-10 stand as written; decision 11 changes what decision 2's "honoured"
*does* — it explains and offers to continue, where it used to refuse.

## Context

Before #131, all four apps could open a document that needs a password, and nothing
else. Saving one failed on every platform (#132). The desktops' crash-recovery journal
wrote its text to disk in plain JSON (#135). Restoring a protected session on the Mac
lost the edits (#133), and shrink-for-email could not open it (#134). No platform read
the permission bits, so the restrictions of an owner-password document were ignored.

PDFium could keep a document's security when saving, or remove it
(`FPDF_REMOVE_SECURITY`). It could not give a document new security: its revision 5/6
creation branch never initialised a crypto handler, and nothing wrote the AES-256 owner
entries. MegaPDF's patch 0010 adds `FPDF_SaveAsCopyWithSecurity`, verified by qpdf as
well as by PDFium itself.

## Decisions

1. **The password lives with the open document, in memory only.** The shared core keeps
   what the document was opened with and wipes it in `megapdf_close()` (#132). It never
   reaches recents, settings, logs, crash reports or the recovery journal.

2. **Permissions are honoured.** Acrobat, Preview and Chrome all do this, and a document
   whose owner restricted it should not become an editing loophole. `megapdf_security_info()`
   reports what the open may do, and every platform maps the bits to its tools.
   **Amended by decision 11 (#558):** honouring them means saying what the author asked and
   then letting the person decide, not refusing. The table below is still the map; what a
   withheld bit *does* is now decision 11's.

   | permission | what it gates |
   |---|---|
   | modify | editing or deleting the document's own text, whiteout, shrink-for-email, text boxes |
   | fill forms | form fields, check marks on printed boxes, signatures and stamps, text boxes |
   | annotate | the same as fill forms (annotate implies form filling in ISO 32000) |
   | copy | copying text |
   | print | printing |

   A form that allows filling lets people fill in everything it offers: its fields, check
   marks, signatures and text boxes. Changing the document itself (its text, whiteout,
   shrink) needs modify. Dave's direction, 2026-09-14: "a form filling pdf should allow
   filling in all of the fields that are open to them".

   Saving is not gated: a restricted open has nothing it may change. Save a copy stays
   available.

3. **A restricted open says so, and offers the owner password.** Opening with the owner
   password gives full access; the app reopens the document that way. Removing or
   changing security is never offered without full access, and the core refuses it too
   (`MEGAPDF_ERR_RESTRICTED`).

4. **New security is AES-256 only** (standard handler, revision 6): the strongest the
   standard handler has, and the only one patch 0010 writes.

   Patch 0030 is what makes that true of the file on disk. Before it, PDFium chose the new
   encryption dictionary's object number in two places that could disagree, and for 30% of
   the corpus the trailer named an object that was never written: the copy was enciphered
   and every reader but MegaPDF called it unencrypted, so its streams would not decode
   (#246). The verified save did not catch it, because it checks the copy by opening it
   with the new credential and PDFium opens an unencrypted document whatever it is handed.
   The core test now reads the copy's own bytes and insists the `/Encrypt` reference names
   an object that is in the file.

5. **The Password command sets one password.** It opens the document with every
   permission: the owner password is the same as the user password. Setting restrictions
   with a separate owner password is not offered in the apps yet; the core API
   (`megapdf_save_with_security`) already takes both passwords and the permissions, so it
   is UI work when wanted.

6. **Setting, changing and removing security is a save.** The app writes the file through
   the platform's verified save. It checks the copy by opening it with the new password, or
   without one when security was removed, and then reopens the saved file, so the open
   document, its credentials and its permissions match what is on disk.

7. **Crash recovery is off for documents opened with a password** (#135). There is no
   encrypted journal, so there is no key to manage, and nothing of the document reaches
   disk except the saved file. The desktops say so when such a document opens.
   Owner-password-only documents open without a password and are still journaled: anyone
   holding the file can read them.

8. **Unsupported security gets its own message.** A document using a security handler
   PDFium cannot open (a certificate handler, say) is not "corrupt" and not "wrong
   password"; the apps say which it is.

9. **Passwords cross every boundary as UTF-8.** PDFium converts to Latin-1 for revisions
   2–4. The fixture matrix (`tools/gen_security_fixtures.sh`) includes non-ASCII passwords
   at revision 3 and revision 6. On Android the new security calls pass UTF-8 bytes rather
   than jstrings, because JNI's modified UTF-8 encodes characters outside the BMP
   differently.

10. **"Remove protection" writes a plain file** (#241, Dave's direction 2026-09-18): no
    `/Encrypt`, strings and streams in the clear, and nothing of the security handler
    anywhere in the file — not even as an object nothing points at.

    Until PDFium patch 0029 the trailer was clean and the bytes really were in the clear,
    but the encryption dictionary itself survived in the body as an orphan object with its
    `/O`, `/U`, `/OE`, `/UE`, `/Perms` and the old `/P`. That is the credential verifier
    material, in a file the user asked to have protection removed from: the original
    credential could still be attacked offline from it. Upstream cleared `encrypt_dict_`,
    which governs the trailer, but the body is written from the objects reachable from the
    *parser's* trailer, and that one still named the dictionary. When the document's
    cross-reference is a stream, that stream *is* the trailer and has an object number of
    its own, so it came across as well — still saying `/Encrypt N 0 R`.

    The core test `test_remove_protection` removes protection from each of eight handlers
    over a fixture with two pages of text, a filled text field, a checked checkbox and two
    annotations — six with a classic cross-reference table, two with object streams and a
    cross-reference stream — and asserts the copy carries no `/Encrypt`, no
    `/Filter /Standard`, no `/Perms` and no `/StdCF`, and that every page's text, fields,
    annotations and pixels are exactly what they were. The .NET `SecurityTests` assert the
    same through `VerifiedSave`, the route the desktops take. CI then has qpdf and
    pdftotext — readers that are not PDFium — confirm each copy is not encrypted and still
    readable.

11. **A permission the author withheld is an informed choice, not a wall** (#558, Dave's
    decision 2026-09-30). When the document's permissions forbid what someone is about to
    do, the app says that the author asked it not be done, and offers to continue anyway.
    This replaces decision 2's flat refusal on editing and #554's new block on assembly.

    **Why, in Dave's words:** these bits are unenforceable, and the person in front of the
    app may well be the author. A refusal treats an advisory flag as a lock, which it is
    not, and leaves someone stuck with their own document. Ignoring the flag throws away
    information the author deliberately put there. Saying it out loud and then deferring to
    the person does both jobs.

    Five things follow, settled here so four platforms build one rule rather than four
    readings of it. Android built them first (#558, wip/558-permission-override); the issue
    comment on #558 is the shape the other three copy.

    **11a. Which bits we read.** Unchanged from what ships today — this decision changes the
    *response*, not the set. Three bits are consulted, and nothing is added:

    | permission bit | governs | since |
    |---|---|---|
    | **Modify** (P-bit 4), **Fill forms** (P-bit 9), **Annotate** (P-bit 6) | the document's own text, redaction, whiteout, text boxes, signatures, stamps, check marks, form fields | decision 2 (#131) |
    | **Assemble** (P-bit 11) **or Modify** | rotate, delete, reorder, insert a blank page, combine | #174 / #554 |
    | **Copy** (P-bit 5) | extract — save a selection of pages as a new file | #174 (core and Android both) |

    **Print** (P-bit 3) is read where decision 2 already read it and gains nothing here:
    MegaPDF's Print hands the document to the OS, which asks it again. **Accessibility**
    (P-bit 10) is read nowhere: no MegaPDF operation can honestly claim to be acting for
    assistive technology, so there is no correct place to grant its narrower exception.
    **Print high quality** (P-bit 12) is read nowhere; there are no quality tiers to gate.

    On Copy, a correction to the question as #558 asked it: the copy bit is not currently
    unconsulted. `megapdf_pages_extract` has required it since #174's engine half, and
    Android's *Save pages as…* has been gated on it since. "Extraction stays ungated" would
    therefore have meant deleting a shipped check, which is the one thing this decision's own
    reasoning argues against — it would throw away what the author asked. So extraction keeps
    the bit and gets the same question as the other two. That is not a widening: the set of
    bits read is exactly what shipped, and only the response changed, which is what 11 says.

    **11b. One rule for every bit read.** Explain, then offer to continue. No per-bit
    variations, no "this one is more serious".

    **11c. A class of operation is a permission bit** — not a command, and not the whole
    document. That is the grain the person is asked at: once per document per class, kept in
    memory for as long as the document is open and never persisted. The bit, because the bit
    is what the author actually set, so it is the only thing the question can honestly name
    and the only grain at which the answer transfers. Someone who has decided to go on
    editing a restricted document has decided about *editing* — not about one keystroke,
    which is why a prompt per change would be nagging rather than consent, and not about
    rearranging its pages, which the author said separately and may have meant differently.
    Three classes, matching 11a's three rows. A cancel is not an answer to remember.

    **11d. The wording reports a request and never implies enforceability.** "The author of
    this document asked that its contents not be changed", then "That's a request rather than
    a lock, so it's your call." Never *restricted*, *locked*, *protected* or *not allowed*,
    and no mention of the owner password — a password is not what is needed here, and naming
    it was the old refusal's way of saying a wall was there. `docs/localisation-glossary.md`
    carries the English and the French.

    **11e. What is *not* offered, because it is not advisory.** Setting, changing or removing
    the password still needs full access and still says so (decision 3, decision 5): without
    the real credential there is nothing to re-encrypt a copy with, so there is nothing to
    continue *to*. Nor does a choice about this document reach a second file whose pages are
    being imported into it — `megapdf_pages_import` keeps refusing a source that forbids
    copying, because the person asserted something about the document they opened and may be
    the author of, not about a file handed to it. And selecting something is not changing it:
    a tap that only selects is neither refused nor asked about.

    **The core carries it, so no platform can get it wrong on its own.** The C++ core enforces
    the same advisory bits underneath all four apps (`PageToolsPreflight`, and the modify bit
    `megapdf_redact_apply` needs), so a Continue that only an app knew about would come back
    `MEGAPDF_ERR_RESTRICTED` — the wall again, one tap later. `megapdf_security_override()`
    (#558) is how an app says the person was told and chose to continue: in memory, for that
    open only, reaching exactly the bits `PermissionsOf()` is consulted for.
    `megapdf_security_info()` goes on reporting the permissions the file carries, so the
    author's request survives the override and survives a save — which keeps the document's
    existing security. Nothing is written into the document, and nothing is remembered: the
    next open asks again.

## Still open, flagged for a separate decision

**F8 Text out (§3.9)** — the structure/Markdown/CLI extraction — also takes content out of
an open document and consults no permission bit on any platform today. Whether that should
require Copy (it is arguably "extract text and graphics," the same ISO 32000-2 wording the
Copy bit uses) is a related question #558 did not answer and decision 11 does not
either: F8 never writes a new PDF, and has no equivalent of "the source is unaffected" to
point to the way page extraction does. Decision 11 settles the shape of the question for any
bit MegaPDF reads; it does not make F8 read one. A separate issue when it is wanted.

## Consequences

- The apps need MegaPDF's PDFium from patch 0010 on; from 0029 on for the removal to
  be complete, and from 0030 on for decision 4 to hold at all on a document whose
  highest object number is one nothing refers to (#246, `libs/pdfium/RELEASE`).
- Every handler from RC4-40 to AES-256, an owner-only restricted document, non-ASCII
  passwords and cleartext metadata are committed fixtures (`tests/MegaPDF.Core.Tests/Fixtures/security`),
  exercised by the core tests on every CI OS and by the desktop tests; the phone tests use
  the owner-only fixture and a copy saved with new security. The `remove-*.pdf` family is
  the same six handlers over `secure-source.pdf`, the fixture with fields and annotations
  that #241's removal test compares against.
- The stress harness counts protected documents as their own outcome, and can open them
  from a private unlock list.
- Decision 11 adds one core export, `megapdf_security_override()`, and needs nothing of
  PDFium that decisions 1-10 did not. Each platform's share is small and is listed on #558:
  the question, the once-per-document-per-class remembering, the strings, and one call into
  the core when the person says yes. Android is done (`PermissionPromptTest`,
  `PermissionOverrideTest`, and the core's own `owner-only.pdf` assertions); iOS, Windows and
  Avalonia are outstanding, and #558 stays open until all four have it.
