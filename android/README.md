# MegaPDF for Android

Native Kotlin + Jetpack Compose implementation of MegaPDF's mobile v1 scope:
**fill-check-sign** — view/zoom, tap-to-check checkboxes, signature library and
placement, save. No text editing in v1. Roadmap: issues #12–#19.

Past that line: **find in document** (#26, SDD §3.6) — case-insensitive literal
search across the whole document, matches highlighted with the current one in a
different colour — and an **About** dialog on the home screen carrying the version,
the credits, and the bundled `THIRD-PARTY-NOTICES.txt` (SDD §4.3).

## Ground rules

- **No code sharing with the .NET app.** `src/MegaPDF.Core` is the *behavioral
  reference* this port is written against, not a dependency. When in doubt about
  behavior, read `PdfiumEngine.cs` — it is the spec.
- **The cross-platform behavioral contracts in SDD §6.2 are binding**: the
  `MegaPDF_Id` stamp-tagging scheme (`mark:`/`sig:` prefixes), the drawn-checkbox
  detection heuristic, and the signature cleanup pixel math must match Windows
  exactly, verified by the shared fixtures in `tests/`.
- Same product principles as desktop: no accounts, no cloud, no network — all
  processing is local. The manifest declares **zero permissions**; keep it that way
  (SAF and the Photo Picker don't need any).
- PDFium is pinned to the same major version as `libs/pdfium/win-x64` (152.x,
  bblanchon/pdfium-binaries).

## Layout

```
app/     Compose UI (viewer, checkboxes, signatures, save flows)
engine/  Kotlin engine API + C++ JNI shim over PDFium (single-threaded dispatcher;
         PDFium is not thread-safe)
```

## Building

Requires JDK 17 and the Android SDK (NDK + CMake 3.22.1 are pulled automatically).

```
./gradlew :app:assembleDebug :app:testDebugUnitTest :engine:testDebugUnitTest
```

CI builds run in `.github/workflows/android-ci.yml`, path-filtered to `android/**`.
