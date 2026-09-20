# ADR-005: Redaction — marks that cannot ship, positions that cannot drift, and an apply that fails closed

**Status:** accepted, 2026-09-17. Dave decided redaction is implemented properly for 2.0,
on every platform (#173).

## Context

MegaPDF has had Whiteout since 1.0. It appends a white filled path over the page
(`megapdf_add_whiteout`, engine contract 5 — whiteouts, text boxes and detached objects;
§6.2 has no whiteout contract, and its contract 5 is the one this ADR adds). Everything
under it stays in the file:
selectable, copyable, searchable, and extractable by any other reader, including ours once
the whiteout is removed. Someone who "whites out" an account number and emails the PDF has
emailed the account number.

This is the best-documented failure in the whole format — court filings, government
releases, and Acrobat's own users who skip Sanitize. Acrobat charges for the fix and still
leaks. So the feature is worth having, and it is only worth having if it is right: a
redaction tool that is *usually* right is worse than none, because it converts a careful
user into a confident one.

Three decisions shaped everything else, and a fourth followed three days later out
of #329 — the first three are about what a redaction *does*, and the fourth is
about what a person can do to a mark that is not done yet.

## Decision 1: a redaction mark is never written to the file

Marks live in the core, on the open document, and are not page objects. `megapdf_save`
on a document carrying marks writes a document with no marks in it.

**Why.** Acrobat's marks are annotations. A file saved with unapplied marks therefore
carries both the content and a set of rectangles announcing where the interesting content
is. That is the failure this feature exists to prevent, shipped inside the feature. Making
a mark unserialisable makes it structurally impossible rather than a rule every one of
four apps has to remember on every save path.

**What we gave up.** Marks do not survive closing the document, and the apps have to draw
them themselves rather than getting them free from the page render. Both turned out to be
cheap: every app already has an overlay layer for find highlights and selection handles,
and the core hands over the rectangles. In exchange, marking costs no content
regeneration, invalidates no layout verdict (#137), and is therefore instant on a page of
thousands of objects — where a page-object mark would have cost seconds per word marked.

The first of those two is a rule the apps owe rather than a property they get for free:
the core forgets the marks when the document closes, and each app keeps its own copy of
them to draw, so each has to forget its copy at the same moment. Decision 4 is what makes
that binding on the apps, and #329 is what it cost to leave it implicit.

**Alternative rejected:** a page-object mark, removed at apply time, like a whiteout. It
would have matched the whiteout contract and been less code. It also would have shipped
the exact bug we are fixing the first time anyone saved without applying.

## Decision 2: a partly covered run is rebuilt from recorded character positions

A text object only partly inside a redacted area is replaced by a new one drawing the
surviving glyphs, each placed with `FPDFText_SetPositions` at the origin the page's text
layer reported for it, projected into text space. It is then read back and checked glyph by
glyph, and refused if any is more than 0.05 pt from where it was.

**Why.** The naive approach is to set the surviving text and let the font's widths lay it
out. That is wrong on any document that uses `Tc`, `Tw`, `TJ` kerning or horizontal scaling
— which is most documents a word processor produced. The glyphs outside the area would move,
and the whole promise of the feature is that only the marked area changes.
`FPDFText_SetPositions` takes per-character offsets, so the positions are *reproduced* from
what was read rather than *re-derived*. The #118 layout guard then rehearses the whole
redaction on a copy of the page, with the marked areas masked out of the render compare, so
what it proves is exactly what #173 asks: nothing outside the area moved.

**What we gave up.** A subset font that cannot re-encode a character it has just drawn
makes the rewrite fail, and we refuse rather than fall back. See decision 3.

**A consequence worth stating, because it looks like a bug.** A glyph cannot be half
removed. One straddling the edge of the area is removed whole, and takes its part outside
the mark with it — which is *every* glyph of rotated text, whose axis-aligned box is far
larger than its ink. The box drawn covers the mark, not the removal: growing the box to
cover the overhang would cover content that is still in the file, which is the whiteout
mistake again. So the report says how far the removal reached
(`megapdf_redaction_applied_areas`), and the tests and the corpus battery judge "unchanged
outside" against that rather than against the mark.

## Decision 3: apply fails closed, for the whole document

`megapdf_redact_apply` plans every marked page read-only, rehearses each on a scratch copy,
and only then executes. A refusal at either of the first two stages returns
`MEGAPDF_ERR_REDACT` with the page and the reason, and **the document is not touched** —
not that page, not any other. A failure during execution, which the rehearsal says cannot
happen, *poisons* the document: `megapdf_save` and every `megapdf_save_*` on it return
`MEGAPDF_ERR_REDACT` from then on, and it can only be closed.

**Why.** The alternative is redacting what we can and reporting the rest, which is how most
tools behave and is exactly how people get hurt: a file that is 90% redacted looks 100%
redacted. Partial success has no safe presentation. And a half-applied document that can
still be saved is a file on disk with some content removed and some not, which no warning
dialog can undo.

**What we gave up.** Documents we refuse outright today: one with a path or shading
crossing the edge of an area, one with a shared form XObject reaching into an area, and one
whose surviving glyphs cannot be redrawn in their own font (a Type 3 font answers no to
everything). The first two are PDFium gaps, and the patch series closes them; the third is
genuine and stays.

**What made this affordable.** The guard was already there. #118's dry run — rewrite on a
copy of the page, save, reopen, compare the render and every text object — is the rehearsal
stage with one addition, a mask over the marked areas. The budget, the renders and the
0.5 pt run check are unchanged, so a redaction is judged by exactly the machinery that
already decides whether a text edit is safe.

## Decision 4: a mark can be taken back, and does not outlive the document

Added 2026-09-20 for #329, which found both halves promised and neither delivered.

- **A mark has an id, and it is stable.** Stable for the mark's life and never reused by
  the document, which is what lets an app address a mark it drew earlier and an undo
  address the same one afterwards. Mark operations live in the undo history beside the
  text-box operations that address objects the same way, and removing a mark that is
  already gone counts as success, so an undo cannot fail on one that is no longer there.
- **One gesture is one undo step.** A drag that marks three lines is a single entry in the
  history, and Undo after it takes all three back. Redo puts back exactly the rectangles
  the user saw rather than re-running the selection, which would be derived from the page
  as it is *now* — and the rectangles are what was actually marked.
- **A mark can be removed.** Tapping one selects it, and it carries the same move / resize
  / remove chrome a signature carries — except that a redaction area is a rectangle by
  nature, so the corner grip moves each side on its own rather than holding the aspect
  ratio. **✕** on the selection removes it, **Remove mark** is the accessibility action on
  the mark itself, and **Clear all marks** in the ⋯ menu takes every mark on every page as
  one step, offered only while there is something to clear. Removal is an edit, so Undo
  puts the mark back where it was.
- **A mark is not a change.** Marking, moving and removing declare
  `changesDocument = false`: no unsaved dot, no recovery-journal entry, no page check
  (#137), no re-render. What changes is the overlay, and the overlay is the app's.
- **Marks are document-scoped.** Closing a document clears them, and the next document
  opens with none. This is decision 1's "marks do not survive closing" made concrete for
  the apps, which is where it can actually be broken.

**Why.** The SDD has promised marks that are "movable, removable and undoable" since 1.4,
and only the moving was real: `markTextForRedaction` was a mutating engine call that the
apps also used as a test, so it bypassed the undo history entirely — mark three times and
Undo once left two on the page. The survivorship half is the sharper one. The core drops
its marks when the document closes, but each app keeps its own copy of them to draw, and
nothing made the app's copy go with the core's. Reopen a document whose redaction was
abandoned and the marks are still on screen and still armed, and the next Save applies
them. In a feature whose entire promise is that nothing happens until the user says so, a
mark that cannot be taken off is the wrong kind of broken: it is the user's own earlier
intent, executed later without being asked.

**What we gave up.** Redo cannot re-derive what a gesture marked, so the rectangles are
recorded with the history entry — a small amount of duplicate state in exchange for a redo
that cannot disagree with the screen. And the desktops stage behind the phones: Android
and iOS have this as of 2.1, and Windows and Avalonia in their next release, where the
work rides with the mark chrome those apps already have. §3.8 F7 behaviour 7 carries the
same note for readers who never open this file.

## Consequences

- Undo cannot resurrect redacted content. Apply frees every `megapdf_detached` handle the
  document holds — those keep removed page objects alive so an undo can put them back
  byte-identical (engine contract 5), which after a redaction is precisely what must not
  happen.
  Restoring such a handle returns `MEGAPDF_ERR_REDACT` and says why, rather than quietly
  succeeding as a no-op and letting an app believe the undo worked.
- The recovery journal (#145) is truncated and rewritten before the save completes, and a
  journal entry written before a redaction is discarded rather than replayed.
- Redaction needs the **modify** permission (ADR-004 decision 2). It changes the document.
- Verification is by independent tools, not by asking PDFium whether PDFium removed
  something: `tools/leakcheck` hunts the removed strings through PDFium's text extraction,
  every stream `qpdf --qdf --decode-level=all` writes out, the raw bytes as ASCII, UTF-16LE,
  UTF-16BE and PDF hex digits, `/Info`, XMP, the outline, every annotation, and the pixels.
  The core test suite and the corpus battery run the same header, so they cannot disagree
  about what a leak is.

## Where this does not reach

Two things are refused rather than done badly. Both say which page and why, and both are a
refusal of the whole document (decision 3), so nothing is half-redacted.

**A picture that cannot be written back the way it was stored.** The pixels under an area
are overwritten in the image's own samples, read with `FPDFImageObj_GetBitmap` and written
with `FPDFImageObj_SetBitmap`. That pair only speaks 8-bit colour, so a scan stored at one
bit per pixel — fax G4, JBIG2 — comes back as 8-bit gray. Measured on a corpus page whose
image is a 2384x3440 JBIG2 scan: writing it back **unchanged** moves 90.9% of the page's
pixels, and 11.9% once the inverted polarity is corrected. The rest is the conversion
itself, a halftone screen resampled as gray rather than as bilevel. So the layout guard
refuses, and because the page rewrote a picture it says so: *the picture could not be
written back without changing how it looks elsewhere on the page*. A small bilevel image
round-trips inside the guard's budget and is not refused — the `image-ccitt` and
`image-jbig2` fixtures redact clean — which is why this is measured per document instead of
being guessed from the bit depth.

There is no cheap patch for it. `FPDFImageObj_GetImageDataDecoded` sounds like the way out,
but PDFium only undoes Flate, LZW and RunLength there; for a CCITT, DCT or JBIG2 image it
returns the raw encoded stream untouched (measured: decoded == raw, byte for byte, on all
three). Patching samples in their own format would mean carrying both a decoder and an
encoder for those filters through the API, which is a larger change than #173 should make.
Recorded as a limit instead, and the message tells the reader what to do: cover it with
whiteout, or send the page as an image.

**A form XObject drawn in more than one place.** Patch 0027 can copy one so a redaction
changes only the page in hand, but PDFium cannot write an edited form's objects back into
its stream, so the copy would carry the unredacted content. Refused as `shared-form`. This
needs an upstream change, not a patch of ours.

Measured rates over 301 corpus documents with random text and image areas on every page:
194 redacted, 106 refused — 61 the picture, 35 the layout guard on text, 1 the shared form.
Scanned documents are where redaction matters most, so the picture case is the one to fix
next, and it is the whole reason the refusal names a picture instead of a page.

## Status of the PDFium series

The writer needed nothing: a full `FPDF_SaveAsCopy` already drops objects the page no
longer references, measured on a page whose only image was replaced. What PDFium has no API
for is what the series adds, at 0026 onward — setting a page object's clip path, copying a
shared form XObject, removing `/Info` and the XMP packet, and removing a form field's value
with its widget.
