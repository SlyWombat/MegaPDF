# ADR-002: macOS desktop app — UI framework and code sharing

**Status: PROPOSED — 2026-08-27.** Leaning: **Option B (.NET 8 + Avalonia over
`MegaPDF.Core`)**. Decide after the spike in §6.

## Context

Windows has a full-scope desktop app (`src/MegaPDF.App`, WinUI 3). Apple users
have only the iOS/iPadOS app, which is the reduced fill-check-sign scope and, on
Apple Silicon, can at best run scaled as a "Designed for iPad" app. Stakeholder
decision 2026-08-27: **a native Mac desktop app at full Windows feature parity**
— text editing, whiteout, shrink-for-email, printing, find, recovery journal,
signature library.

Two constraints frame the choice, both confirmed with the stakeholder:

- **No Mac hardware.** Development runs Mac-less through GitHub Actions macOS
  runners, exactly as iOS does today (SDD §6.1). This is the tighter of the two.
  iOS survived it because the app is essentially one scrolling view covered by
  `ios-screenshots.yml`; a desktop app has menus, multi-window, keyboard
  navigation, printing and drag-and-drop, and iterating all of that blind
  through CI round-trips is a materially different proposition.
- **Full parity, not mobile scope.** This decides *which* codebase gets shared,
  because `MegaPDF.Core` is the reference implementation that *defines* the
  §6.2 cross-platform contracts.

**This ADR requires an SDD amendment on acceptance.** §1.4 lists "macOS/Linux
desktop versions" as an explicit v1 non-goal — true on every option below.
§6.1's "native per platform, no shared code" does **not** bar Option B: that
section states it "governs the new platforms only" and that "`src/` remains
.NET/Windows". Core is already Windows's engine; the no-sharing rule was
written about the mobile ports. The argument for each option is made on merits
below, not on doctrine.

## Facts established before writing this (2026-08-27)

1. **The PDFium pin holds on macOS.** bblanchon publishes `pdfium-mac-univ`,
   `pdfium-mac-arm64` and `pdfium-mac-x64` at the exact pinned build
   (`chromium/7934`, `libs/pdfium/win-x64/VERSION` → MAJOR=152 BUILD=7934).
   Verified further: `pdfium-mac-univ` is a genuine 2-slice fat Mach-O
   (x86_64 + arm64, 14.6 MB), its `VERSION` file is byte-identical to the
   Windows pin, and its `licenses/` filenames match `win-x64`'s exactly — so
   `THIRD-PARTY-NOTICES` would not change. The §6.1 "same PDFium major on all
   platforms" doctrine survives day one, in one download.
2. **`MegaPDF.Core` is already portable, at the cost of one project-file edit.**
   Pure `net8.0`, no Windows references (`System.*` + P/Invoke only), and
   `PdfiumNative.Dll` is the bare name `"pdfium"` — .NET's probing resolves that
   to `libpdfium.dylib` on macOS with no *code* change. 3,651 LOC, ~85 tests.
   The one edit: `MegaPDF.Core.csproj` copies `libs/pdfium/win-x64/pdfium.dll`
   unconditionally, so on macOS it would copy a Windows DLL and no dylib. That
   item needs an OS condition. Engine code itself is untouched.
3. **`MainViewModel` is barely coupled to WinUI.** 1,199 LOC with roughly 17
   framework touch points: `SolidColorBrush` (2), `Visibility` (6),
   `ContentDialog` (2), `FileSavePicker` (2), `FileOpenPicker`, `BitmapImage`,
   `WriteableBitmap`, `TextBlock`, `Application.Current`. Every one has a direct
   Avalonia counterpart. This is the single biggest input to the estimate.
4. **The App layer's Windows-only pieces are small and known.**
   `PdfPrinter` (127, `Windows.Graphics.Printing`), `SignatureImageProcessor`
   (121, `Windows.Graphics.Imaging`), `UpdateChecker` (114, uses
   `Windows.Management.Deployment` only to self-disable on MSIX builds),
   `PageCanvas` (21, `ProtectedCursor`), `MainWindow.xaml`/`.xaml.cs` (1,906).
5. **The test project itself is portable; three of its tests are not.**
   `MegaPDF.Core.Tests` targets `net8.0`, references Core only, and builds its
   fixtures in memory (`SamplePdf.cs`) rather than reading committed PDFs — so
   it needs nothing fetched. But it pulls `System.Drawing.Common` 8.0.8 (its own
   csproj comment: "Windows-only, tests-only") for JPEG encoding in
   `ImageCompressionTests`. On .NET 8 that package throws
   `PlatformNotSupportedException` off Windows, so **3 tests will fail on macOS
   for reasons unrelated to the engine.** Expect them to fail; they need a
   cross-platform encoder (SkiaSharp/ImageSharp) or a macOS skip.
6. **§6.2 contract 3 has no .NET test.** `SignatureImageProcessor` — the SDD's
   own named reference for the signature cleanup pixel math — lives in
   `src/MegaPDF.App`, is Windows-only (`Windows.Graphics.Imaging`), and the App
   layer has no test project at all. Searching `tests/` for the processor or its
   luminance threshold returns nothing. Android's
   `SignatureImageProcessorTest.kt` is the only executable oracle for this
   contract. It is therefore the contract most exposed to silent drift on a
   Mac port, and the one the engine spike does **not** cover.
7. **A portability nit.** Core's stores resolve
   `Environment.SpecialFolder.LocalApplicationData`, which on macOS is
   `~/.local/share`, not the conventional `~/Library/Application Support`.
   Needs a platform branch in `AppSettings`, `RecentFiles`, `SignatureLibrary`
   and `RecoveryJournal` — four call sites, all already parameterised.

## Criteria the choice must satisfy

1. **§6.2 contracts unchanged.** Stamp identity, drawn-checkbox heuristic,
   signature cleanup pixel math, text-box identity/font/anchor. A Mac app that
   re-derives these in a fourth language is a fourth chance to drift.
2. **Full Windows feature parity**, per the scope decision above.
3. **Developable without a Mac**, with CI as the only macOS execution
   environment.
4. **Native Mac feel** — menu bar, standard shortcuts, system file dialogs,
   printing, retina rendering. The stakeholder explicitly rejected a scaled
   iPad app.
5. **Distributable** — notarized, and ideally Mac App Store capable.

## Option A — Swift + SwiftUI/AppKit

*For:* the most genuinely native result; shares ~825 LOC of iOS engine code
(`ios/MegaPDF/Engine/PdfEngine*.swift`), already contract-conformant; reuses the
existing Apple developer account, XcodeGen setup and `fetch-pdfium.sh` pattern.

*Against:* fails criterion 3 hardest — every UI iteration is a CI round-trip
with no local run. Fails criterion 2 economically: the iOS engine covers
fill-check-sign only, so whiteout, text editing, shrink-for-email, incremental
save and the recovery journal must all be re-ported from Core into Swift, and
criterion 1 says each of those is a fresh opportunity to diverge. Largest total
build of the four.

## Option B — .NET 8 + Avalonia over `MegaPDF.Core`

*For:* takes criteria 1 and 2 nearly for free — Core ships with the `mac-univ`
dylib for zero engine-code change (one OS-conditioned copy item, fact 2), so the
contracts are not re-implemented, they are *the same code*. Uniquely good on
criterion 3: the entire UI is developed and run **on Windows**, with macOS
entering only to bundle, sign and notarize — and that half must run on a macOS
runner, since `codesign` and `notarytool` are macOS-only. `MainViewModel` ports
with ~17 edits (fact 3). Avalonia renders through Skia identically on both
platforms, so one UI codebase could eventually replace the WinUI one rather than
sitting beside it.

*Against:* a fourth UI framework in the stack, and a new one for this team.
Avalonia's Mac chrome is good but not AppKit — native menu bar and system
dialogs work, though some Mac idioms need explicit wiring. `MainWindow.xaml` +
code-behind (~1,900 LOC) is a rewrite, not a port: WinUI and Avalonia XAML are
different dialects. Printing and image processing need non-Windows replacements
(see §7). Risk of the Mac app looking like a Windows app with round corners if
the Mac idioms aren't deliberately designed in.

## Option C — .NET 8 + Uno Platform over `MegaPDF.Core`

*For:* same Core sharing as B, and Uno consumes WinUI XAML directly, so
`MainWindow.xaml` might port rather than be rewritten.

*Against:* the code-behind is where the Windows coupling actually lives
(`Windows.Graphics.Printing`, `Windows.Storage.Pickers`,
`Microsoft.UI.Windowing`, `Windows.Management.Deployment`, four
`WindowsRuntime` marshalling imports) — so the promised XAML reuse saves the
cheaper half. Heavier toolchain, and macOS is not its strongest target. The
XAML-reuse claim should be tested in the spike before it's believed.

## Option D — Mac Catalyst from the iOS app

*For:* nearly free — `pdfium-ios-catalyst-arm64`/`-x64` slices exist at the
pinned build, and the existing app would run.

*Against:* **dismissed.** It delivers mobile scope (criterion 2 fails outright)
in the recognisably non-native Catalyst idiom (criterion 4), which is the exact
outcome the stakeholder rejected when they asked for "a native app, like
Windows". Worth keeping only as a same-week stopgap if Apple-side demand needs
answering before a real Mac app can ship.

## Leaning (not a decision)

**Option B.** Criterion 3 is the constraint that actually bites, and B is the
only option where the daily development loop runs on hardware that exists.
Criteria 1 and 2 compound the point: the parity scope means the *engine* is the
majority of the value, and B ships the engine without rewriting it. The
counterweight — a fourth UI framework, and ~1,900 LOC of XAML to rebuild — is
real but bounded, and it buys a UI layer that could later serve Windows too.

## 6. Spike before deciding (target: 1–2 days on CI)

Implemented as `.github/workflows/macos-spike.yml` (`workflow_dispatch`, plus
`pull_request` on the spike paths) with `tools/fetch-pdfium-mac.sh` and
`spike/macos-shell/`. The jobs are deliberately independent — no `needs:` — so
the exploratory half can never obscure the load-bearing answer.

1. **Engine spike (the load-bearing one).** Fetch `pdfium-mac-univ` at the
   pinned build, place `libpdfium.dylib` beside the test assembly, and run **the
   test project explicitly** — not the solution:

   ```
   dotnet test tests/MegaPDF.Core.Tests/MegaPDF.Core.Tests.csproj
   ```

   `dotnet test MegaPDF.sln` is what `ci.yml` runs and it would **fail on macOS
   for the wrong reason**: the solution includes `MegaPDF.App`, which is
   `WinExe`/`net8.0-windows10.0.19041.0` and will not restore off Windows.
   Order matters — build, *then* place the dylib, *then* `dotnet test
   --no-build`, because bare-name probing resolves relative to the assembly
   directory. If probing fails anyway, `DYLD_LIBRARY_PATH` is the fallback; it
   is deliberately not set up front, so it can't mask a real finding.

   That project asserts §6.2 contracts 1, 2 and 4 — `SignatureStampTests`,
   `DrawnCheckboxTests`, `WhiteoutAndTextBoxTests`, `AcroFormTests`,
   `FontSubstitutionTests`. **Contract 3 is not covered** (fact 6), and
   `ImageCompressionTests`' 3 tests are excluded from the gate and run
   separately as non-gating evidence (fact 5). Everything else green means the
   engine ports for zero engine-code change, and Options B/C become mostly a UI
   exercise. Any *other* failure is a fact both this ADR and the §6.1 pinning
   doctrine must absorb.

   The fetch script verifies the macOS slice's `VERSION` against
   `libs/pdfium/win-x64/VERSION` and fails the job on mismatch, which makes the
   §6.1 pinning doctrine machine-enforced rather than a comment.

2. **Shell spike.** A code-only (no XAML) Avalonia app that renders page 1 of
   `stamped.pdf` through `PdfiumEngine`. Two halves, deliberately split to prove
   the Option B development loop:
   - **On Windows:** build and run it, asserting the render through
     `Avalonia.Headless` — more reliable in CI than launching a GUI app, and it
     is the loop that would be used daily.
   - **On macOS:** publish `osx-arm64`, assemble a minimal `.app`, ad-hoc
     `codesign`, launch headless. Its real question is **where the dylib has to
     live**: .NET's bare-name probing wants it beside the assembly
     (`Contents/MacOS/`), while Apple convention and library validation for a
     notarized app want it in `Contents/Frameworks/` signed with the same
     identity. The job tries the former and logs what signing makes of it. **No
     specific outcome is asserted** — expect to iterate; the log is the
     deliverable, and it feeds the notarization work item in §7.

   The engine assertion runs before the Avalonia one and a surface failure is
   non-fatal, so a version wobble in Avalonia's headless API cannot cost us the
   engine answer.

If Option C is to stay live, add a third job attempting `MainWindow.xaml` under
Uno; if it doesn't build near-unmodified, C's only advantage over B is gone.

**If job 1 fails confusingly:** `pdfium-linux-x64` exists at the same build, so a
one-off `ubuntu-latest` run of the same suite discriminates "pdfium interop
problem" from "macOS-specific problem" in minutes. Triage tool only — Linux is a
§1.4 non-goal and does not belong in the committed workflow.

## 7. Work items this creates regardless of option (not blockers)

- **Signing and notarization is new plumbing.** `docs/mobile-app-blueprint.md`
  mints `IOS_APP_STORE` profiles via the ASC API. macOS needs a **Developer ID
  Application** certificate plus `notarytool` stapling for direct download, or
  a Mac App Store profile for the Store — same Team ID, different certificate
  type and different pipeline. The iOS automation does not carry over as-is.
- **Hardened runtime and library validation.** A notarized app loading a
  vendored `libpdfium.dylib` must have the dylib signed with the *same*
  Developer ID and placed in `Contents/Frameworks/`, or else carry
  `com.apple.security.cs.disable-library-validation`. Given the "Dylib Law"
  landmine already recorded in `docs/mobile-app-blueprint.md`, budget for this
  rather than discovering it at notarization. Spike job 2 produces the first
  evidence.
- **Distribution shape.** Windows ships both Store (MSIX) and direct download
  with a self-disabling updater. Mac needs the equivalent decision: `.dmg` +
  Sparkle-style updates, Mac App Store, or both. `UpdateChecker`'s MSIX
  self-disable logic needs a Mac analogue.
- **Printing replacement** for `PdfPrinter`'s `Windows.Graphics.Printing`.
- **Image processing replacement** for `SignatureImageProcessor`'s
  `Windows.Graphics.Imaging` (SkiaSharp or ImageSharp). It must reproduce §6.2
  contract 3's pixel math exactly — and per fact 6 **there are no .NET tests to
  port it against; they must be written**, using Android's
  `SignatureImageProcessorTest.kt` as the oracle. Do this as part of the port,
  not after: it is the contract with no cross-platform fixture PDF to catch
  drift later.
- **A cross-platform JPEG encoder for the tests** to retire
  `System.Drawing.Common` (fact 5), so the suite can be green on macOS CI.
- **Vendor-vs-fetch for the shipping app.** The spike *fetches* the dylib
  (`tools/fetch-pdfium-mac.sh`) rather than committing 14.6 MB to answer a
  question — the iOS tree already establishes fetch-and-gitignore as an accepted
  pattern. If the shipping app vendors `libs/pdfium/mac-univ/` instead, re-run
  `tools/gen_third_party_notices.py` and commit the result in the same change:
  the `notices` CI job hard-fails on drift. Per fact 1 the notices content would
  not actually change, but the job checks regardless.
- **Path convention branch** for `~/Library/Application Support` (fact 7).
- **CI path filters.** `ci.yml`'s `paths-ignore` now also lists `spike/**`, so
  the spike tree cannot fire the Windows product CI. A future `macos/**` app
  tree needs the same treatment plus its own workflow.
- **SDD amendments:** §1.4 non-goal, and a §6 subsection for desktop platforms.
