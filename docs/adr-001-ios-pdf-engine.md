# ADR-001: iOS PDF engine — PDFKit vs PDFium

**Status: ACCEPTED — PDFium (Option B), 2026-08-08.** (iOS M0 issue: #20)

## Decision & spike results

The spike (`ios/spike/PdfKitSpike.swift`, run in the `pdfkit-spike` CI job
against the `stamped.pdf` fixture) measured, via raw CGPDF dictionary dumps:

- `read-custom-key=true` — PDFKit reads `MegaPDF_Id` from PDFium-style stamps.
- `key-survives-save=true`, `write-custom-key=true` — the id contract holds.
- **`appearance-streams-preserved=false`** — PDFKit rewrote both stamps'
  `/AP` streams on save (33→65 and 72→104 bytes).

The AP rewriting is disqualifying in combination with criterion 2: desktop and
Android move/resize signatures by locating the *image object inside the
stamp's appearance* (`FPDFAnnot_GetObjectCount`/`GetObject` →
`FPDFImageObj_GetRenderedBitmap`); a PDFKit re-save that restructures that
content would strand stamps placed on other platforms. And criterion 2 (the
drawn-checkbox heuristic) has no PDFKit API surface at all. **iOS therefore
uses a PDFium xcframework** (bblanchon, pinned 152.x like Windows/Android),
porting the Android C++ shim logic to Swift's C interop. PDFKit may still be
used for incidental viewing niceties, but never to *write* documents.

## Context

Android reimplemented the fill-check-sign engine surface over PDFium's C API
(#13–#18), pinned to the same PDFium major (152.x) as Windows. iOS must choose
between Apple's native **PDFKit** and a **PDFium xcframework** (bblanchon
publishes iOS builds of the same pinned version). No code is shared either way
(SDD §6.1); the question is engine *behavior* parity vs platform leverage.

## The parity criteria the choice must satisfy

These are the SDD §6.2 contracts, now concretely exercised by the Android
implementation and its fixtures (`tools/gen_test_fixtures.py`, asserted in
`android/engine/src/androidTest/`):

1. **Stamp interop.** Read *and* write annotations tagged with the custom key
   `MegaPDF_Id` (`mark:`/`sig:` prefixes). A document signed on Windows or
   Android must show its stamps as selectable/movable/removable on iOS, and
   vice versa. Includes the native-resolution image readback used for
   move/resize (#17).
2. **Drawn-checkbox heuristic.** Detect stroked-not-filled path objects,
   6–24 pt, squareness within 25%, and place the ✗ mark stamp with the exact
   desktop geometry (10% inset, 0x202020 stroke, width max(1.2, w·0.11)).
   The shared fixture rects must assert identically.
3. **AcroForm checkboxes.** Toggle checkbox/radio widgets keeping `/V`, `/AS`,
   and radio-group siblings consistent; state must survive save and render
   correctly in third-party viewers.
4. **Save fidelity.** Full-rewrite save whose output reopens in desktop
   MegaPDF, Acrobat, and Drive/Files previews.

## Option A — PDFKit (Apple native)

*For:* zero native-binary weight (~9 MB saved vs the AAB's PDFium payload);
`PDFView` gives scrolling/zoom/tiling for free (Android built this by hand in
#14); AcroForm widget interaction is built in; system-quality text rendering;
no NDK/JNI-equivalent layer to maintain.

*Against / unknowns to spike:*
- **Criterion 2 is fully manual**: PDFKit exposes no page content stream
  object model, so the drawn-square heuristic can't be implemented over
  PDFKit APIs at all — it would need a second engine (or Core Graphics PDF
  parsing) just for detection, eroding the simplicity win.
- Custom annotation keys (criterion 1) exist
  (`PDFAnnotation.value(forAnnotationKey:)` / `setValue(_:forAnnotationKey:)`)
  but round-trip fidelity with PDFium-authored stamp streams is unproven —
  PDFKit is known to rewrite annotation appearance streams on save.
- Save is `PDFDocument.write`, whose output differs structurally from
  PDFium's; cross-platform fixture asserts would need loosening.

## Option B — PDFium xcframework (engine parity)

*For:* criteria 1–4 are already proven by the Android implementation — the
C++ shim logic ports nearly line-for-line (Swift's C interop replaces JNI, and
is simpler); one engine behavior across all three platforms; the Android
fixtures/tests translate directly.

*Against:* rebuild the render/gesture stack (PDFView equivalents: tiled
zoomable page views — the #14 work again, in SwiftUI); ~10 MB binary; keep the
xcframework vendored and version-pinned; App Store review has no issue with
PDFium but static linking (`__Internal`-style) is the well-trodden path.

## Leaning (not a decision)

The Android experience moved the needle toward **Option B**: the engine layer
was the *smallest* part of the port (≈600 lines of C++ + Kotlin), while
criterion 2 is effectively impossible in pure PDFKit and criterion 1 is risky
there. The counterweight is `PDFView`, which replaces the largest chunk of UI
work. A credible hybrid — PDFKit for *viewing*, PDFium for *hit-testing and
editing* the same bytes — doubles engine surface and risks divergent
coordinate/geometry behavior; treat it as a fallback, not the plan.

**Spike before deciding** (1–2 days on CI): load `tools/gen_test_fixtures.py`
fixtures in PDFKit; check (a) `MegaPDF_Id` keys written by Android survive
PDFKit save, (b) whether appearance streams are rewritten, (c) what content
inspection PDFKit allows. If (a) or (b) fails, Option B wins by default.
