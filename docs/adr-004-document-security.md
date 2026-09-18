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
