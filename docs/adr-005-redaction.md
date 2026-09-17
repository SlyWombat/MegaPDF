# ADR-005: Redaction — marks that cannot ship, positions that cannot drift, and an apply that fails closed

**Status:** accepted, 2026-09-17. Dave decided redaction is implemented properly for 2.0,
on every platform (#173).

## Context

MegaPDF has had Whiteout since 1.0. It appends a white filled path over the page
(`megapdf_add_whiteout`, SDD §6.2 contract 5). Everything under it stays in the file:
selectable, copyable, searchable, and extractable by any other reader, including ours once
the whiteout is removed. Someone who "whites out" an account number and emails the PDF has
emailed the account number.

This is the best-documented failure in the whole format — court filings, government
releases, and Acrobat's own users who skip Sanitize. Acrobat charges for the fix and still
leaks. So the feature is worth having, and it is only worth having if it is right: a
redaction tool that is *usually* right is worse than none, because it converts a careful
user into a confident one.

Three decisions shaped everything else.

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

**Alternative rejected:** a page-object mark, removed at apply time, like a whiteout. It
would have matched contract 5 and been less code. It also would have shipped the exact bug
we are fixing the first time anyone saved without applying.

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

## Consequences

- Undo cannot resurrect redacted content. Apply frees every `megapdf_detached` handle the
  document holds — those keep removed page objects alive so an undo can put them back
  byte-identical (contract 5), which after a redaction is precisely what must not happen.
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

## Status of the PDFium series

The writer needed nothing: a full `FPDF_SaveAsCopy` already drops objects the page no
longer references, measured on a page whose only image was replaced. What PDFium has no API
for is what the series adds, at 0026 onward — setting a page object's clip path, copying a
shared form XObject, removing `/Info` and the XMP packet, and removing a form field's value
with its widget.
