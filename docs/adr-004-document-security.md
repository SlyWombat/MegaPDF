# ADR-004: Password-protected documents — opening, permissions, and setting or removing security

**Status:** accepted, 2026-09-14. Dave asked for protected documents to be supported
properly (#131) and for setting and removing passwords to be part of it.

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
   reports what the open may do, and every platform maps the bits to its tools:

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

## Proposed amendment: assemble and copy for page tools (pending Dave's decision — #558)

**Status: proposed, not accepted.** Decisions 1–10 above are settled; this section is a
recommendation for Dave to approve, amend, or reject. Nothing below is implemented or
changed because of this section — it is the write-up #558 asked for, so that whichever way
it is decided, four platforms build the same rule instead of four readings of it.

### Where this comes from

Page tools (§3.10 F9, #174) shipped on Android, then on Windows and Avalonia (Mac/Linux),
over 2026-09-29 and -30. Decision 2's table above maps five permission bits to MegaPDF's
existing tools; it has no row for **assemble** (P-bit 11) or for what **copy** (P-bit 5)
governs beyond the word "copying," because nothing before #174 took pages out of, or
rearranged pages within, a document. #558 was opened the moment Android's page tools
landed, naming the gap: Android now reads the assemble bit and no other platform did.

That gap closed faster than #558 anticipated. By the time this is written, it is stale on
three of the four platforms:

- The shared engine core already refuses the call. Contract 10's own header comment
  (`core/megapdf_core.h`, the block above `megapdf_page_rotate`) states the rule it
  enforces: every call that changes the document needs `MEGAPDF_PERMIT_ASSEMBLE` **or**
  `MEGAPDF_PERMIT_MODIFY`; `megapdf_pages_extract` needs `MEGAPDF_PERMIT_COPY`; otherwise
  `MEGAPDF_ERR_RESTRICTED`. This is not a proposal — it is what the C++ core has done since
  #174's engine half landed, underneath every platform's page-tools UI.
- Android's `DocumentCapabilities.kt`, and the shared `MegaPDF.Core`
  `DocumentCapabilities.cs` that both Windows and Avalonia consume, each independently
  added `canAssemblePages`/`CanAssemblePages` (assemble **or** modify) and
  `canExtractPages`/`CanExtractPages` (copy), for the same reason: so a button is never
  offered that the core would only refuse. The three files were written within about two
  hours of each other and agree exactly, without anyone having stated the rule out loud
  first — they read it off the core's own comment.
- **iPhone and iPad have no page-tools UI yet** (tracked separately for 2.2/#174); the one
  iOS function that calls contract 10 today (`extractPages`, `PdfEngine+Pages.swift`) exists
  for a field-hierarchy test, not a shipped feature, and consults no permission at all. iOS
  is not a fourth disagreement — it has nothing built to agree or disagree with.

So the four platforms already agree, in effect, except that nobody has written the rule
down as a decision, and iOS still needs it stated before it builds page tools. What follows
proposes making the accidental agreement the recorded rule, rather than inventing a new one.

### The recommendation

| Permission bit (ISO 32000-2 Table 22) | Governs |
|---|---|
| **Assemble** (P-bit 11) **or Modify** (P-bit 4) | Rotate, delete, reorder, insert a blank page, combine (import pages from another file) — any operation that changes this document's own pages or page order. Either bit is sufficient, matching the spec's own text ("assemble the document … even if bit 4 is clear") and what the core and three platforms already enforce. |
| **Copy** (P-bit 5) | Extract (save a selection of pages as a new file). Extraction changes nothing in the source; it manufactures a new file holding a copy of some of the original content, which is what the copy bit is for. A document may permit copying without permitting modification (a read-only handout, fine to excerpt but not to restructure) or the reverse (a form meant to be filled and reassembled but not copied from) — treating extraction as its own bit rather than folding it into assemble/modify is what keeps those two documents distinguishable. |
| **Accessibility** (P-bit 10) | Nothing, deliberately. No MegaPDF operation identifies itself as acting on behalf of assistive technology, so there is no correct place to grant this bit's narrower exception. Extraction keeps needing the stronger Copy bit for everyone, screen reader or not, rather than adding a bit we have no reliable way to attribute. |
| **Print, high quality** (P-bit 12) | Nothing separately. MegaPDF's Print command has no quality tiers; it continues to gate on **Print** (P-bit 3) alone, as decision 2 above already does. A separate high-quality path is not recommended unless a feature actually needs one. |

This is the whole rule: two new rows added to decision 2's table, nothing else. Once
approved, it is small work per platform — the permission set is already read at open time
on all four (decision 2), and Android/Windows/Avalonia already do exactly this.

### Said plainly, because it should be

These bits are advisory. Any tool holding the owner password can clear them outright, and
plenty of PDF tools ignore them entirely — nothing about the standard security handler
makes P-bit 11 or P-bit 5 into real access control. MegaPDF honours them because the
document's author asked it to, the same reasoning decision 2 above already gives for
modify, fill-forms, annotate, copy and print. This amendment does not make the bits
enforceable; it extends the set of MegaPDF operations that ask the question before acting.

The principle and its wording both already exist: MegaPDF already refuses to edit a
document when modify is withheld, and says so — a typed refusal
(`MEGAPDF_ERR_RESTRICTED`, decision 3) explained in the UI, never a silently disabled
button. This proposal is that same behaviour, extended to two operations (assembly,
extraction) that had not asked the question before #174 gave them something to ask about.

### Out of scope here, flagged for a separate decision

**F8 Text out (§3.9)** — the structure/Markdown/CLI extraction — also takes content out of
an open document and consults no permission bit on any platform today. Whether that should
require Copy (it is arguably "extract text and graphics," the same ISO 32000-2 wording the
Copy bit uses) is a related question this proposal does not answer: F8 never writes a new
PDF, has no equivalent of "the source is unaffected" to point to for reassurance the way
page extraction does, and mixing that question into #558 risks blocking the smaller,
already-converged-on page-tools rule while it is decided. Recommend a separate issue once
this one is resolved.

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
