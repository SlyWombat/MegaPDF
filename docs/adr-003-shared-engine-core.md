# ADR-003: Shared engine core vs. hand-written policy per platform

**Status: ACCEPTED — Option B, a C++17 core behind a C ABI, on all three platforms,
2026-09-13.** Tracking issue: #33. Phase 1 (PR #37, #38) is complete; the decisions
below govern phase 2 (#105–#111), phase 3 (#112) and phase 4 (#113, #114).
Phase 2 progress: #105 (core-owned documents, CropBox geometry, text search) landed
2026-09-13 — the ABI is `megapdf_open` / `megapdf_load_page` / `megapdf_search_page`
as decided below, and the corpus search totals were identical before and after.
#106 (text runs and visual lines) landed the same day: `megapdf_text_load` with
lines built on first use, a `MEGAPDF_TEXT_BOXES_ONLY` load for text-box listings, and
`core/tests/expected/text_runs.txt` — the desktop engine's answer for every fixture,
captured before the port — as the regression gate. #107 (AcroForm fields) followed:
`megapdf_form_fields_load`, `megapdf_form_click`, `megapdf_form_set_text` and
`megapdf_form_commit` (the kill-focus-before-save rule), which left the phones with
a filter and a struct copy where they had form code. #108 (stamps and MegaPDF_Id
marks) moved check marks in all three styles, image stamps with alpha, the stamp list,
native-resolution image read-back, removal by index or id, and the extract-remove-re-add
move under a stable id into the core. #109 (whiteouts, text boxes, detached objects)
followed: `megapdf_add_whiteout` / `megapdf_whiteouts`, `megapdf_add_text_box` with the
three-face rule and the mark params, `megapdf_restyle_text_box` (the #45 id-preserving
restyle anchored on the bottom-left corner), find/move/remove by id including the
`text:untagged#N` handle for pre-id boxes, and `megapdf_detach_object` /
`megapdf_restore_object` / `megapdf_discard_detached`, with the document freeing any
detached object still held at close (decision 1 made concrete). #110 (save, flatten,
images) moved `megapdf_save` (full rewrite through a caller write callback, form edits
committed first), `megapdf_flatten_all`, the image list, `megapdf_render_image`,
`megapdf_replace_image_jpeg` and `megapdf_shrink_images` — the shrink-for-email
decision rules with the JPEG encoder injected — into the core; file I/O and the atomic
replace stay per platform.
(Numbered 003: ADR-002 became the macOS desktop decision while this sat on its
branch as a draft.)

## Decision & spike results

Engine *policy* — everything between the platform UI and PDFium's C API — is
written once, in C++17, in `core/`, and each platform binds to it: P/Invoke from
`MegaPDF.Core` (Windows and macOS), JNI from the Android engine module, Swift C
interop on iOS. Native UI stays native. What was triplicated is what is shared;
nothing else changes.

Phase 1 proved the pipeline on the one operation that already had exact-rect
assertions on every platform, the drawn-checkbox heuristic (SDD §6.2 contract 2),
so the fixtures decided the result rather than opinion.

| | Result |
|---|---|
| Android instrumented tests | **21 passed** on 2026-08-16; **28 passed** on the 2026-09-13 rebase (the suite grew), same fixtures now exercising the shared implementation |
| iOS tests | **37 passed** on 2026-08-16; **48 passed** on 2026-09-13, including `CheckboxTests.testDrawnSquareDetectedMarkAddedAndRoundTrips` and the #98 search canary |
| Desktop (Windows + macOS, `MegaPDF.Core`) | **162 Core tests passed** on GPD-DAVE and on the Mac mini with `DetectCheckboxSquares` forwarding to the core; `DrawnCheckboxTests` and `SearchCropBoxTests` unchanged |
| Android APK size | +2,140 bytes (+0.014%) on 2026-08-16; +2,583 bytes on the debug-APK artifact after the rebase |
| Build wiring, Android | one CMake line; compiles into the existing `.so`, no second binary, no runtime ABI boundary |
| Build wiring, iOS | a source entry in `project.yml`, a one-line bridging header, `HEADER_SEARCH_PATHS` |
| Build wiring, desktop | `core/CMakeLists.txt` → `megapdf_core.dll` (win-x64; pdfium import library generated from the DLL's export table) and a universal `libmegapdf_core.dylib` (links the fetched pdfium, loads it via `@loader_path`); `MegaPDF.Core.csproj` runs `tools/build-core.*` when the artifact is missing or stale and copies it next to pdfium, so it reaches the Windows app, the MSIX and the Mac bundle the way pdfium does |
| Net code | 154 lines of core replacing **three** hand-written copies, all deleted |
| CI | `ci.yml` builds the core as its own step on `windows-latest`; the macOS app workflow builds it for both RIDs and runs the Core suite against it; no measurable time change |

The Windows leg, which the draft called a distribution decision, resolved itself:
the developer machine and the runners both have MSVC 14.44, Windows SDK 10.0.26100,
CMake and Ninja, so the core is **built from source everywhere and no binary of our
own code is vendored**. A committed prebuilt would have been the "somebody
remembers to rebuild it" failure waiting to happen, and would have let the C# side
silently lag the phones.

## Context

#30 is the argument. pdfium reports content in MediaBox coordinates but renders the
CropBox; #28 fixed the resulting offset on Windows and Android, and **iOS was
missed and shipped wrong**. One root cause, three required fixes, and the third did
not happen — not a discipline failure, but what an architecture with three
hand-written copies of the same policy asks for.

That triplication is survivable for small closed-form contracts. It is not
survivable for in-place text editing, which SDD §4.3 calls "the product's dominant
schedule risk" and which is mostly *subtle policy*: subset-font coverage
approximated by scanning sibling objects, substitution that must preserve matrix,
colour, marks and z-order, undo via detach-and-restore, and pdfium quirks such as
`FPDFTextObj_GetText` reporting bytes where the header says wide chars. It kept
growing while this draft waited: the 2026-09-13 corpus stress run (#92) put a
render-size clamp and a preview-first render into the desktops (#93–#95) and the
phones got neither (#115).

## The ABI rules (kept from phase 1)

- **C only.** No C++ types cross the boundary; no exceptions escape.
- **Caller-owned buffers**, count-then-fill. Allocation stays on the binding's side,
  which is what keeps JNI and P/Invoke marshalling boring.
- **Coordinates come back in crop space**, bottom-left origin, PDF points. The
  conversion being missed is #30 exactly, so it happens once, in the core.
- The header includes nothing from pdfium; a binding needs one file.

## Decisions for phase 2 onward (what the draft left open)

1. **The core owns documents.** Phase 1 passes the binding's `FPDF_PAGE` through as
   `void*`, which leaves document loading, the form-fill environment, page lifetime
   and serialisation on each platform. Text editing cannot be written that way:
   detach-and-restore undo keeps native objects alive between calls, substitution
   needs the document's font state, and every mutation must run under one lock.
   From #105 the ABI is `megapdf_open(bytes, length, password) → doc`,
   `megapdf_load_page(doc, index) → page`, `megapdf_close(doc)`; the form-fill
   environment and page cache live inside the core; detached objects are opaque
   integer ids owned by the document and freed with it. Bindings pass bytes and
   receive handles and never see an `FPDF_*` type again. The phase-1 `void* page`
   entry stays as a shim until the checkbox contract is re-bound to the new handles
   (#105), then goes.
2. **Threading is the core's.** PDFium is not thread-safe; that is a property of the
   library, not of Android's dispatcher or C#'s lock. Every ABI call serialises on a
   mutex inside the core. Bindings may keep their own thread discipline (the Kotlin
   single-thread executor, the Swift actor, `PdfiumLibrary.Lock`) as an extra layer,
   but correctness does not depend on it.
3. **No silent failure.** Every entry returns a status code (`0` ok, negative error)
   or a count; `megapdf_last_error()` returns a message for the calling thread.
   Nothing throws across the boundary and nothing crashes on a PDFium refusal —
   the 3 GB bitmap in #93 is a status, not a process death.
4. **What stays per platform.** Bitmap allocation and presentation (WriteableBitmap,
   Android `Bitmap`, `CGContext`), file I/O and the atomic-replace / verify-before-
   overwrite protocol, pickers, the recovery journal's storage, and all UI. What
   moves: every decision in between, including render *policy* — the pixel-size
   clamp, the render flags, form-field drawing — even though the pixels are written
   into a platform buffer (#111).
5. **Tests live where the code lives.** A C++ test target over `core/` (#104) runs
   on all three CI OSes against `tools/gen_test_fixtures.py` output and the #98
   schematic, with AddressSanitizer on Linux. The per-platform suites keep their
   assertions as the parity gate; the corpus stress harness (`tools/stress`)
   exercises the core on 4,337 real files through the `IPdfEngine` adapter.
6. **Migration order.** Contracts move one at a time, each behind the tests that
   already assert it, in the order they are needed by text editing: geometry and
   search (#105), text runs and lines (#106), form fields (#107), stamps and marks
   (#108), whiteout and text boxes with detached-object undo (#109), save/flatten/
   images (#110); render policy (#111) in parallel. Body-text editing is then
   written once (#112) and the phones grow only their editing UI (#113, #114).
7. **Same pinned PDFium everywhere** (152.x from bblanchon/pdfium-binaries), one
   header set (the Android tree's, vendored) for every native build, and
   `tools/gen_third_party_notices.py` re-run whenever the pin moves. The iOS core is
   compiled into the app binary, never a loose dylib (14 rejected builds taught
   that); Android keeps the 16 KB page-size linker flag.

## Options considered

**A — Status quo, hand-port the text layer to Kotlin and Swift.** No new machinery;
each platform idiomatic. Rejected: it triples the hardest logic in the product at
the moment it stops being closed-form, and #30 is what that already cost on a
simple contract.

**B — Shared native core with a C ABI, native UI on top.** Chosen, for the numbers
above: the build cost measured in bytes and single lines, and a CropBox-class bug
becomes structurally hard to miss.

**C — Kotlin Multiplatform for the shared layer.** Rejected: drags a KMP runtime
into the iOS binary, still needs C interop to reach PDFium, and does nothing for
the C# side, so the policy would be shared across two platforms and duplicated
for the third.

**D — Host `MegaPDF.Core` itself on the phones (.NET for iOS / Android).** Not in
the draft; considered during the 2026-09-13 review because Core already runs
unmodified on macOS. Rejected for now: it puts a .NET runtime into two Swift and
Kotlin apps that do not need one. It is the fallback if the C ABI's packaging ever
fights back on a platform, and would be measured before hand-porting anything a
third time.

## Consequences

- New engine policy is written **once**, in `core/`, and the bindings are
  marshalling. Text editing on the phones becomes one implementation plus two UIs.
- C++ memory safety is ours. Mitigated by the allocation rules, a small surface,
  and ASan in CI; not eliminated.
- Debugging crosses an FFI boundary on three platforms. Mitigated by decision 3.
- For the length of phase 2 the phones run the core for some contracts while the
  desktops still run `PdfiumEngine.cs` for others. The per-platform fixture
  assertions are what make that interval safe; they are not to be thinned.
- **SDD §6.1's "native per platform" now means native UI.** The layer below the UI
  and above PDFium is shared by design; MAUI and Uno stay rejected; the product
  principles are untouched.
