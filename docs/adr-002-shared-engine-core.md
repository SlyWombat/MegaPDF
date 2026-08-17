# ADR-002: Shared engine core vs. hand-written policy per platform

**Status: PROPOSED — Android and iOS legs measured and green (PR #37); the Windows
leg is unbuilt and its cost is not code but packaging (#38).** Tracking issue: #33.

## Decision & spike results

Phase 1 moved the drawn-checkbox heuristic (SDD §6.2 contract 2) out of the Swift
and C++ shims into `core/megapdf_core.{h,cpp}`, one implementation behind a C ABI,
and bound Android and iOS to it. It was chosen because it already had exact-rect
assertions on all three platforms, so the fixtures decide the result rather than
opinion.

Measured on PR #37 against `8703621`:

| | Result |
|---|---|
| Android instrumented tests | **21 passed**, unchanged count, same fixtures now exercising the shared implementation |
| iOS tests | **37 passed**, including `CheckboxTests.testDrawnSquareDetectedMarkAddedAndRoundTrips` |
| Android APK size | 15,781,313 → 15,783,453 bytes — **+2,140 bytes (+0.014%)** |
| Build wiring, Android | **one CMake line** (source + include dir); compiles into the existing `.so`, so no second binary ships and there is no runtime ABI boundary |
| Build wiring, iOS | source entry in `project.yml`, a one-line bridging header, `HEADER_SEARCH_PATHS` |
| Net code | 154 lines of core (with its documentation) replacing two hand-written copies; both deleted |
| CI time | no measurable change (within run-to-run noise) |

**The ABI shape that made both bindings trivial**, and which any further migration
should keep:

- **C only.** No C++ types cross, no exceptions escape.
- **Handles are bare `void*`** — the caller passes its `FPDF_PAGE` straight
  through, and the header includes nothing from pdfium, so binding code needs
  that one file and nothing else.
- **Caller-owned buffers**, count-then-fill. This is what keeps JNI and P/Invoke
  marshalling boring.
- **Coordinates come back in crop space.** Deliberate: the conversion being missed
  is #30 exactly, so it now happens once in the core instead of in each binding.

A side effect worth naming: Android's shim got *simpler*. `engine.cpp` has always
described itself as "marshalling only, no policy"; after this it finally is.

## Context

#30 is the argument. pdfium reports content in MediaBox coordinates but renders the
CropBox; #28 fixed the resulting offset on Windows and Android, and **iOS was
missed and shipped wrong**. One root cause, three required fixes, and the third did
not happen — not a discipline failure, but what an architecture with three
hand-written copies of the same policy asks for.

That triplication is survivable for small closed-form contracts. It is not
survivable for in-place text editing, which SDD §4.3 already calls "the product's
dominant schedule risk" and which is mostly *subtle policy* — subset-font coverage
approximated by scanning sibling objects, substitution that must preserve matrix,
colour, marks and z-order, undo via detach-and-restore, and pdfium quirks such as
`FPDFTextObj_GetText` reporting bytes where the header says wide chars. Writing
that three times means three quirk sets and three chances to miss the next
CropBox.

## What the spike did not settle: Windows

The mobile legs were cheap because both platforms already compile C++ and link
PDFium. Windows does neither — it P/Invokes a prebuilt `pdfium.dll` and has **no
native build step at all**. Two concrete obstacles surfaced while attempting it:

1. **No import library is vendored.** `libs/pdfium/win-x64/` holds only
   `pdfium.dll`, so linking a core DLL against it requires generating one:
   `dumpbin /exports` → `.def` → `lib /def /machine:x64`. That works (461 symbols,
   verified locally) but it is a build step someone must own.
2. **The developer machine cannot build it.** The installed VS 2017 Build Tools
   carry the MSVC compiler but **no Windows SDK** — `INCLUDE` contains only the
   MSVC directory, and the compile fails on `stddef.h`. So a native build step
   added to the Windows pipeline would break local builds of the shipping product
   until the SDK is installed.

That turns the Windows leg into a **distribution decision, not a coding one**:

- **Vendor a prebuilt `megapdf_core.dll`** the way PDFium is vendored. Local builds
  keep working with no new toolchain, at the cost of a committed binary of our own
  source that a human must remember to rebuild.
- **Build from source in the pipeline** and require the Windows SDK locally.
  Honest and reproducible; it changes what a contributor needs installed to build
  the only shipping product, which is already three releases behind on the Store
  (#31).

Neither is obviously right, and the choice belongs to whoever maintains the Windows
release. It is written up in #38 with the recipe that worked.

## Consequences if this is accepted

- New engine policy is written **once**, in `core/`, and the bindings stay
  marshalling. Text editing (#34's deferred half) becomes a single implementation
  rather than three.
- A CropBox-class bug becomes structurally hard: the conversion lives in the core.
- The cost is a C++ codebase that all three platforms depend on, debugging that
  crosses an FFI boundary, and memory safety that is now ours. The `+2 KB` and
  one-CMake-line results say the *build* cost is small; the *ownership* cost is
  real and does not show up in a measurement.
- **SDD §6.1 is unchanged.** Native UI per platform, zero shared UI code; MAUI and
  Uno stay rejected. What is shared is the layer below the UI and above PDFium,
  which was already duplicated three ways.

## Recommendation

Accept for the mobile platforms now (PR #37 is green), and treat Windows as a
separate decision once #38's packaging question is answered. Do not migrate more
operations until Windows is settled — a core that two of three platforms use is a
fourth copy waiting to happen.
