# Test matrix: features × platforms (#330)

**Generated — do not edit.** The source is `tests/matrix/coverage.toml`;
`tools/qa/check-matrix.py` validates it against the tree and rewrites this file, and
the `qa-matrix` workflow fails if either has drifted. Read that script's header for why
the matrix has this shape rather than the feature × action × state × platform grid #330
first asked for.

Each cell says two things and both are checked against the code:

| code | means |
|---|---|
| `UI` | a test in CI drives this platform's own UI or view-model path |
| `eng` | only the shared engine/core is tested — this platform's path to it is not |
| `—` | present and reachable, nothing automated |
| `n/a` | the feature is deliberately not on this platform, with a reason |
| `GAP` | the feature is **missing** where it should exist — the #571 shape |
| `+m` | a by-hand step in `TESTING.md` / `docs/RELEASING.md` covers it too |

225 pairings: 129 UI, 61 eng, 12 —, 14 n/a, 9 GAP.

This map is about *what is covered*, not whether the covering tests pass — that is the
other jobs' business — and it does not replace the by-hand pass in `docs/RELEASING.md`
§2.3, which is still the release gate.

## The gaps, worst first

This is the part worth reading. A map that only restated what passes would not be
worth checking in.

### Missing feature — absent where it should exist (9)

Nothing is failing; nothing is looking. #571 (Linux had an About window with no route to it) and #3 (Android had no whiteout at all) were both this.

| feature | platform | what is missing | issue |
|---|---|---|---|
| Whiteout (cover) (`whiteout`) | ios | no whiteout tool. The reason given in the engine source is that mobile's feature set excludes it — which stopped being true when Android got one on 2026-09-30. | — |
| A withheld permission is said out loud, and the person may continue (`permission-override`) | ios | iOS page tools landed in #570 consulting no permission bit at all, so this is the one platform where the question has to arrive with the detection rather than replace it. | #558 |
| A withheld permission is said out loud, and the person may continue (`permission-override`) | linux | same as macOS. | #558 |
| A withheld permission is said out loud, and the person may continue (`permission-override`) | macos | same as Windows — the shared MegaPDF.Core DocumentCapabilities both desktops consume is the half that has to change, so these two land together. | #558 |
| A withheld permission is said out loud, and the person may continue (`permission-override`) | windows | still refuses. DocumentCapabilities.cs reads the bits and the toolbar greys out what the author withheld; megapdf_security_override() is in the core and unbound in CoreNative.cs. | #558 |
| Print (`print`) | android | no PrintManager anywhere in android/app. Same shape as iOS. | — |
| Print (`print`) | ios | no UIPrintInteractionController anywhere in ios/MegaPDF. AirPrint is what a phone user would reach for and there is no route to it. | — |
| Shrink for email (`shrink`) | android | no shrink on Android either, for the same reason and with the same silence. | — |
| Shrink for email (`shrink`) | ios | no shrink on iOS. Nothing in the source says it was decided against, and a phone mailing a scan is the case it exists for. | — |

### Present, nothing automated (12)

The feature is reachable and no test in CI touches it.

| feature | platform | what is missing | issue |
|---|---|---|---|
| About, the version, and the third-party notices (`about-notices`) | ios | no test opens About or the notices on iOS. | — |
| Keyboard-only operation (`keyboard-only`) | ios | iPad keyboard commands are declared and no test presses one, on either destination. | — |
| The build a user installs (MSIX, App Store sandbox, .deb, Snap, Flatpak, tarball) (`packaged-install`) | android | CI assembles a debug APK and tests it; the release AAB is only verified by hand. | — |
| The build a user installs (MSIX, App Store sandbox, .deb, Snap, Flatpak, tarball) (`packaged-install`) | ios | CI builds and tests on the simulator only; nothing checks the shipped archive beyond the release workflow's own packaging. | — |
| Device rotation (`device-rotation`) | android | a blind spot, and rotation on Android destroys and recreates the activity, which is where unsaved edits and an armed tool would go missing. | — |
| Device rotation (`device-rotation`) | ios | a blind spot. The app declares every orientation and no test rotates anything, on the iPhone or the iPad destination. | — |
| Very large documents (1, 2.5 and 4.5 GB) (`large-files`) | android | a blind spot: no large-file test of any size on Android, in CI or by hand. | — |
| Background, foreground and process death (`process-death`) | android | a blind spot, and the one the 2.0.1 save-after-reboot bug came through: the persisted URI grant outlives the process and nothing tests that it does. UiAutomator can drive process death on the emulator CI already runs. | — |
| Background, foreground and process death (`process-death`) | ios | a blind spot. No test backgrounds the app, and nothing checks what survives being killed while a document is open with edits. | — |
| Closing with unsaved changes (`unsaved-prompt`) | windows | no self-test state closes a dirty document; the per-tab Cancel case TESTING.md describes is by hand only. | — |
| Print (`print`) | macos | the self-test checks the command's enablement and nothing else; no print is ever performed in CI. | — |
| Print (`print`) | windows | nothing automated touches printing on Windows: no self-test state, no core test of PdfPrinter, and tools/windows-qa's print flow is manual. | — |

### Engine only — this platform's own path is untested (61)

The shared core proves the operation. Nothing proves this platform reaches it correctly, which is where #401 and #412 lived.

| feature | platform | what is missing | issue |
|---|---|---|---|
| About, the version, and the third-party notices (`about-notices`) | android | the notices' paragraph formatting is unit-tested; no instrumented test opens the dialog. | — |
| English, fr-CA and fr-FR (`localisation`) | android | nothing on Android runs in French at all — no locale-qualified instrumented run, unlike iOS. The catalogue is checked from the .NET side. | — |
| English, fr-CA and fr-FR (`localisation`) | windows | catalogue completeness for all four platforms is asserted; no Windows run happens in French, and the French-width pass in tools/windows-qa is manual. | — |
| The build a user installs (MSIX, App Store sandbox, .deb, Snap, Flatpak, tarball) (`packaged-install`) | macos | the App Store build runs --self-test out of the bundle; the sandboxed, signed, installed app is only ever run by hand on the Mac mini. | — |
| The build a user installs (MSIX, App Store sandbox, .deb, Snap, Flatpak, tarball) (`packaged-install`) | windows | the MSIX's identity, architecture and resource map are checked; nothing runs the installed MSIX in CI, which is where #401 was. | — |
| Screen-reader names and announcements (`screen-reader`) | android | touch-exploration arithmetic and notice formatting; no instrumented test reads a content description back, and TalkBack is by hand. | — |
| Protected, restricted, XFA and damaged documents (`special-documents`) | windows | the gating rules are tested; no self-test state opens a restricted or XFA document in the UI. | — |
| Light and dark app chrome (`theme`) | android | no instrumented test runs in dark mode. | — |
| Light and dark app chrome (`theme`) | linux | as macOS. | — |
| Light and dark app chrome (`theme`) | macos | --self-test never switches theme; the --theme flag exists for by-hand screenshot poses only. | — |
| Light and dark app chrome (`theme`) | windows | token parity across platforms is asserted; no self-test state switches theme. The page-colour tint in `reading` is a different thing. | — |
| Add text (`add-text`) | ios | geometry, style and round-trip at the engine; no UI test places a box. | — |
| Edit the document's own text (`body-text-edit`) | android | engine only — iOS has a live UI test for the same feature (#113/#114) and Android does not. | — |
| Edit the document's own text (`body-text-edit`) | windows | the engine side is the best-tested part of the app; no Windows self-test state retypes a line in the UI. | — |
| Checkboxes, real and drawn (`checkboxes`) | ios | no mark-style setting on iOS and no UI test taps a box. | — |
| Form text fields (`form-fields`) | ios | the engine and the gating are tested; DemoFlowUITests fills a field live but is excluded from CI as a video script. | — |
| Make a signature (draw, type, photo) and the library (`signature-capture`) | android | no instrumented test opens the signatures sheet at all. | — |
| Make a signature (draw, type, photo) and the library (`signature-capture`) | ios | cleanup and store only; the capture sheets are driven by DemoFlowUITests, which is excluded from CI. | — |
| Make a signature (draw, type, photo) and the library (`signature-capture`) | linux | same as macOS. | — |
| Make a signature (draw, type, photo) and the library (`signature-capture`) | macos | the self-test deliberately synthesises raw pixels rather than decoding a PNG, so the real image path is never run. | — |
| Place, move, resize and remove a signature (`signature-place`) | android | the stamp round-trips on a device; no Compose test places one through the UI. | — |
| Place, move, resize and remove a signature (`signature-place`) | ios | engine placement only. | — |
| Recovery after a crash (`crash-recovery`) | windows | the journal and its replay are well covered; the restore dialog is never shown in CI, and the three-tabs-restored case TESTING.md describes is by hand only. | — |
| Very large documents (1, 2.5 and 4.5 GB) (`large-files`) | ios | a real 1 GB open, search and save-a-copy with timings — and excluded from CI because the fixture is 1 GB. tools/ios-files-e2e.sh is how it runs. | — |
| Very large documents (1, 2.5 and 4.5 GB) (`large-files`) | linux | as Windows; the corpus batteries on k3 are where real large documents are seen. | — |
| Very large documents (1, 2.5 and 4.5 GB) (`large-files`) | macos | as Windows; no large fixture in CI. | — |
| Very large documents (1, 2.5 and 4.5 GB) (`large-files`) | windows | the cross-reference checks run on every push on the ordinary fixtures; the 2.5 and 4.5 GB cases skip themselves unless MEGAPDF_LARGE_FIXTURES is set, so CI never runs them. tools/windows-qa has the by-hand large-file pass. | — |
| Undo and redo (`undo-redo`) | ios | cross-feature undo is unit-tested; no UI test presses the Undo button. | — |
| Closing with unsaved changes (`unsaved-prompt`) | ios | the share-with-unsaved-changes branch is tested; closing dirty is not driven in the UI. | — |
| Open from outside the app (Explorer/Finder/Files/share, drag and drop) (`open-external`) | ios | the unit test mocks the hand-off; OpenWithVerificationUITests drives the real thing and is excluded from CI (MegaPDF is not listed in the share sheet on the Mac mini runner). | — |
| Open from outside the app (Explorer/Finder/Files/share, drag and drop) (`open-external`) | windows | only the don't-reopen-what-is-already-open rule is tested; the redirect race has a by-hand script (tools/windows-qa/redirect-toolbar-check.ps1, #427) that CI does not run. | — |
| Open from the file picker (`open-picker`) | android | no instrumented test taps Open PDF; ACTION_OPEN_DOCUMENT is only driven for importing pages. | — |
| Open from the file picker (`open-picker`) | linux | the portal picker is screenshotted by hand (docs/qa/linux-portal-file-dialog.png), never driven. | — |
| Open from the file picker (`open-picker`) | macos | the picker itself is never driven; the engine's open is. | — |
| Open from the file picker (`open-picker`) | windows | the picker itself is never driven; the engine's open is. | — |
| Recents list (`open-recents`) | android | store and grant logic only — no Compose test taps a recent row, which is the path the 2.0.1 save-after-reboot bug came in by. | — |
| Recents list (`open-recents`) | windows | the list's storage and display names are tested; no Windows self-test state opens from Recents or exercises the jump list. | — |
| Redact — apply on save, and prove the words are gone (`redact-apply`) | android | the engine's apply is proved on a device, including image pixels; no Compose test taps through to the save-a-copy-or-overwrite question. | — |
| Redact — apply on save, and prove the words are gone (`redact-apply`) | windows | RedactionApplyTests arrived with this map, because the cell was empty: the .NET apply path the Windows app takes had no test at all. It hunts for the word in the saved bytes the way tools/leakcheck does rather than asking the engine. No Windows self-test state marks and saves, so the UI above it is still uncovered. | — |
| Redact — mark text or an area, move, resize, remove, clear all (`redact-mark`) | ios | the unit suite is thorough (mark, area with no text, move, remove, undo, redo); the UI test that checks the Redact row is armed is excluded from CI because the row is missing on the simulator (#405). | #405 |
| Redact — the refusal path ("nothing was removed") (`redact-refusal`) | android | the engine refuses and leaves the file alone; no Compose test reads the refusal message back. | — |
| Redact — the refusal path ("nothing was removed") (`redact-refusal`) | ios | the refusal leaves the document unchanged; the banner itself is not driven. | — |
| Redact — the refusal path ("nothing was removed") (`redact-refusal`) | linux | same as macOS. | — |
| Redact — the refusal path ("nothing was removed") (`redact-refusal`) | macos | the self-test applies successfully; it never makes the engine refuse, so the message the user would read is unchecked in ci.yml. | — |
| Redact — the refusal path ("nothing was removed") (`redact-refusal`) | windows | no .NET test produces a real refusal — the native suite and tools/stress/redaction-battery.sh are the only places one is seen. What this map's own test does cover is the quieter half: a mark over empty space must report zero removed and leave what is beside it alone, so a zero cannot mean nothing looked (the #567 shape). | — |
| Export as Markdown (`export-markdown`) | windows | the goldens and the extension-to-kind rule are tested; nothing checks the PDF is left untouched except by hand (#386). | — |
| Password — set, change, remove, and opening a protected document (`password`) | android | no instrumented test opens the password sheet. | — |
| Password — set, change, remove, and opening a protected document (`password`) | ios | set-password and wrong-password-then-remove are real UI tests, and both are excluded from CI — the only platform with a UI test for this and it does not run. | — |
| Password — set, change, remove, and opening a protected document (`password`) | linux | same as macOS. | — |
| Password — set, change, remove, and opening a protected document (`password`) | macos | the self-test checks a password prompt survives a tab switch but never sets, changes or removes a password through the UI. | — |
| Progress and Stop on long operations (`progress-cancel`) | ios | the timing state machine only; no UI test sees a busy strip or presses Stop. | — |
| Save a copy / Save As (`save-a-copy`) | windows | no self-test state drives Save As; the picker is the OS's. | — |
| Save (overwrite in place) (`save-in-place`) | ios | FilesEndToEndUITests saves through the real picker and is excluded from CI. | — |
| Share (`share`) | ios | ShareVerificationUITests drives the real sheet and is excluded from CI for Files-app flakiness. | — |
| Page colours (normal / sepia / night) (`page-colours`) | ios | the stored setting and its descriptions are tested; ReadingModeUITests checks the tint descriptions but not the rendered pixels. | — |
| Render and scroll (`render-scroll`) | android | the render window's page-keeping arithmetic is unit-tested; no instrumented test scrolls. | — |
| Render and scroll (`render-scroll`) | ios | the UI tests render incidentally; no test asserts what scrolling does. | — |
| Render and scroll (`render-scroll`) | windows | every self-test state renders a document to get started, so a dead viewer would fail them; nothing asserts scroll position or lazy page arrival. | — |
| Find in document (`search`) | android | engine parity only; no Compose test types in the search field. #98's canary lives in the engine suite, not the UI. | — |
| Find in document (`search`) | ios | engine parity against the shared fixtures; no UI test opens the find bar. | — |
| Find in document (`search`) | windows | the progress state stops a long search; the find bar's own counter, wrap and Esc are by hand only. The `find` and `find-zoomed` harness states exist but CI does not run them. | — |

## The matrix

### Opening a document

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Open from the file picker** | eng+m | eng+m | eng+m | UI+m | eng+m |
| **Recents list** | eng | UI | UI | UI | eng |
| **Open from outside the app (Explorer/Finder/Files/share, drag and drop)** | eng+m | UI | UI | eng+m | UI |
| **Several documents in one window (tabs)** | UI+m | UI+m | UI+m | n/a | n/a |

### Viewing

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Render and scroll** | eng | UI | UI | eng | eng |
| **Zoom (buttons, keyboard, wheel, pinch) and fit** | UI | UI | UI | UI | UI |
| **Find in document** | eng+m | UI+m | UI+m | eng | eng |
| **Reading mode** | UI | UI | UI | UI | UI |
| **Page colours (normal / sepia / night)** | UI | UI | UI | eng | UI |
| **Page thumbnails pane** | UI | UI | UI | UI | UI |

### Filling in and marking up

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Form text fields** | UI+m | UI | UI | eng | UI |
| **Checkboxes, real and drawn** | UI+m | UI | UI | eng | UI |
| **Make a signature (draw, type, photo) and the library** | UI+m | eng+m | eng+m | eng | eng |
| **Place, move, resize and remove a signature** | UI+m | UI | UI | eng | eng |
| **Add text** | UI+m | UI | UI | eng | UI |
| **Edit the document's own text** | eng+m | UI | UI | UI | eng |
| **Whiteout (cover)** | UI+m | UI | UI | GAP | UI |

### Redaction

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Redact — mark text or an area, move, resize, remove, clear all** | UI+m | UI+m | UI+m | eng | UI |
| **Redact — apply on save, and prove the words are gone** | eng+m | UI+m | UI+m | UI | eng |
| **Redact — the refusal path ("nothing was removed")** | eng | eng | eng | eng | eng |

### Page tools

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Rotate and delete pages** | UI | UI | UI | UI | UI |
| **Reorder pages, insert a blank page, insert pages from a file** | UI | UI | UI | UI | UI |
| **Extract pages to a new file** | UI | UI | UI | UI | UI |

### Saving and output

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Save (overwrite in place)** | UI+m | UI+m | UI+m | eng | UI |
| **Save a copy / Save As** | eng+m | UI+m | UI+m | UI | UI |
| **Export as Markdown** | eng+m | UI+m | UI+m | UI | UI |
| **Share** | n/a | n/a | n/a | eng+m | UI+m |
| **Password — set, change, remove, and opening a protected document** | UI | eng | eng | eng | eng |
| **A withheld permission is said out loud, and the person may continue** | GAP | GAP | GAP | GAP | UI |
| **Shrink for email** | UI+m | UI+m | UI+m | GAP | GAP |
| **Print** | —+m | —+m | UI+m | GAP | GAP |
| **Progress and Stop on long operations** | UI | UI | UI | eng | UI |

### Lifecycle and state

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **Undo and redo** | UI | UI | UI | eng | UI |
| **Closing with unsaved changes** | —+m | UI+m | UI+m | eng | UI |
| **Recovery after a crash** | eng+m | UI+m | UI+m | n/a | n/a |
| **Background, foreground and process death** | n/a | n/a | n/a | — | — |
| **Device rotation** | n/a | n/a | n/a | — | — |
| **Very large documents (1, 2.5 and 4.5 GB)** | eng+m | eng | eng | eng | — |

### Cross-cutting

| feature | Windows | macOS | Linux | iOS/iPadOS | Android |
|---|---|---|---|---|---|
| **English, fr-CA and fr-FR** | eng+m | UI | UI | UI | eng |
| **Light and dark app chrome** | eng | eng | eng | UI | eng |
| **Screen-reader names and announcements** | UI | UI | UI | UI | eng |
| **Keyboard-only operation** | UI | UI | UI | — | n/a |
| **About, the version, and the third-party notices** | UI+m | UI | UI | —+m | eng |
| **Protected, restricted, XFA and damaged documents** | eng | UI | UI | UI | UI |
| **The build a user installs (MSIX, App Store sandbox, .deb, Snap, Flatpak, tarball)** | eng+m | eng+m | UI+m | —+m | —+m |

## Cell by cell

What each cell rests on. Every `entry` and every test reference below is resolved
against the tree by `tools/qa/check-matrix.py` — a path or symbol that stops existing
fails CI rather than rotting here.

### Open from the file picker (`open-picker`)

The platform's own picker. No automated test on any platform drives it: every suite hands
the path or the stream in directly, which is the right call (it is the OS's dialog, not
ours) but does mean the first step of every session is only ever checked by hand.

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::OpenButton`. tests: `tests/MegaPDF.Core.Tests/PdfiumEngineTests.cs::Open`. by hand: `TESTING.md::Open any PDF (Open button`. the picker itself is never driven; the engine's open is.
- **macOS** — eng+m. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Command("OpenButton"`. tests: `tests/MegaPDF.Core.Tests/PdfiumEngineTests.cs::Open`. by hand: `docs/RELEASING.md::open two documents (tabs, one window)`. the picker itself is never driven; the engine's open is.
- **Linux** — eng+m. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Command("OpenButton"`. tests: `tests/MegaPDF.Core.Tests/PdfiumEngineTests.cs::Open`. by hand: `docs/RELEASING.md::open two documents (tabs, one window)`. the portal picker is screenshotted by hand (docs/qa/linux-portal-file-dialog.png), never driven.
- **iOS/iPadOS** — UI+m. coverage: UI/VM. reachable from `ios/MegaPDF/HomeView.swift::Button("Open PDF")`. tests: `ios/MegaPDFUITests/FilesEndToEndUITests.swift::test1`. by hand: `docs/RELEASING.md::Files → share sheet → MegaPDF`. FilesEndToEndUITests drives the real Files picker but is excluded from CI (needs a 1 GB fixture); tools/ios-files-e2e.sh runs it by hand.
- **Android** — eng+m. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/HomeScreen.kt::R.string.open_pdf`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/PdfEngineTest.kt::open`. by hand: `docs/RELEASING.md::Files → Open with`. no instrumented test taps Open PDF; ACTION_OPEN_DOCUMENT is only driven for importing pages.

### Recents list (`open-recents`)

- **Windows** — eng. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::RecentHeading`. tests: `tests/MegaPDF.Core.Tests/RecentFilesTests.cs::RecentFiles`, `tests/MegaPDF.Core.Tests/RecentLocationTests.cs::RecentLocation`. the list's storage and display names are tested; no Windows self-test state opens from Recents or exercises the jump list.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::RecentList`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckSingleInstanceRouting`. the self-test checks the recents rows the shell builds, including the no-paths-shown rule and the screen-reader name.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::RecentList`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckSingleInstanceRouting`. same as macOS; the Show-in-file-manager row itself is never invoked.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/HomeView.swift::Remove from Recents`. tests: `ios/MegaPDFUITests/RecentsAccessibilityUITests.swift::testEveryRecentRowAnnouncesWhereItsFileLives`, `ios/MegaPDFTests/RecentsStoreTests.swift::Recents`, `ios/MegaPDFTests/RecentLocationTests.swift::Location`. the Remove-from-Recents case in the same file is excluded from CI as flaky.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/HomeScreen.kt::R.string.recent`. tests: `android/app/src/test/java/com/megapdf/android/RecentFilesStoreTest.kt::class`, `android/app/src/test/java/com/megapdf/android/RecentLocationTest.kt::class`, `android/app/src/test/java/com/megapdf/android/UriGrantsTest.kt::class`. store and grant logic only — no Compose test taps a recent row, which is the path the 2.0.1 save-after-reboot bug came in by.

### Open from outside the app (Explorer/Finder/Files/share, drag and drop) (`open-external`)

A file arriving from somewhere else: a double-click in the file manager, an Open with, a
share sheet, a drop on the window. #412 and #401 were both in this neighbourhood, and on
the desktops it also means landing in the window that is already running.

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/App.Activation.cs::OnActivatedFromAnotherInstance`. tests: `tests/MegaPDF.Core.Tests/LaunchedDocumentTests.cs::Launched`. by hand: `TESTING.md::File Explorer while MegaPDF is running lands in the window you already have`. only the don't-reopen-what-is-already-open rule is tested; the redirect race has a by-hand script (tools/windows-qa/redirect-toolbar-check.ps1, #427) that CI does not run.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Platform/SingleInstance.cs::Listen`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckLaunchWithArguments`, `src/MegaPDF.Avalonia/Program.cs::CheckSingleInstanceRouting`, `src/MegaPDF.Avalonia/Program.cs::CheckDuplicateHandover`. handover and routing are covered; a real drop on the window is not — MainWindow.axaml.cs's DragDrop handlers are never raised by any test.
- **Linux** — UI. coverage: UI/VM. reachable from `tools/linux/megapdf.desktop::MimeType=application/pdf`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckLaunchWithArguments`, `src/MegaPDF.Avalonia/Program.cs::CheckSingleInstanceRouting`, `src/MegaPDF.Avalonia/Program.cs::CheckDuplicateHandover`. the desktop entry's validity is a CI step of its own; the drop handlers are untested as on macOS.
- **iOS/iPadOS** — eng+m. coverage: engine only. reachable from `ios/project.yml::LSSupportsOpeningDocumentsInPlace`. tests: `ios/MegaPDFTests/ShareAndExternalOpenTests.swift::ExternalOpen`. by hand: `docs/RELEASING.md::Files → share sheet → MegaPDF`. the unit test mocks the hand-off; OpenWithVerificationUITests drives the real thing and is excluded from CI (MegaPDF is not listed in the share sheet on the Mac mini runner).
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/AndroidManifest.xml::android.intent.action.VIEW`. tests: `android/app/src/androidTest/java/com/megapdf/android/OpenWithTest.kt::class`, `android/app/src/test/java/com/megapdf/android/ViewIntentTest.kt::class`. the strongest coverage of this on any platform: cold start and onNewIntent, file:// and content://, with the unsaved-changes prompt.

### Several documents in one window (tabs) (`tabs`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::DocumentsTabView`. tests: `.github/workflows/ci.yml::Run-Check "close-tabs"`, `.github/workflows/ci.yml::Run-Check "reading"`. by hand: `TESTING.md::**Tabs**`. close-tabs asserts the documents are really disposed; reading opens a second tab. Per-tab undo/find/zoom isolation is checked by hand only.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::TabStrip`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckTabs`, `src/MegaPDF.Avalonia/Program.cs::CheckEmptyWindow`, `src/MegaPDF.Avalonia/Program.cs::CheckMultiFileOpen`, `src/MegaPDF.Avalonia/Program.cs::CheckTabTransitionSafety`, `src/MegaPDF.Avalonia/Program.cs::CheckWindowTitle`, `src/MegaPDF.Avalonia/Program.cs::CheckCloseDuringRender`. by hand: `TESTING.md::**Tabs**`. the deepest tab coverage anywhere: mid-gesture switches, scroll and selection survival, close during render, the window with no tab.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::TabStrip`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckTabs`, `src/MegaPDF.Avalonia/Program.cs::CheckEmptyWindow`, `src/MegaPDF.Avalonia/Program.cs::CheckMultiFileOpen`, `src/MegaPDF.Avalonia/Program.cs::CheckTabTransitionSafety`, `src/MegaPDF.Avalonia/Program.cs::CheckWindowTitle`, `src/MegaPDF.Avalonia/Program.cs::CheckCloseDuringRender`. by hand: `TESTING.md::**Tabs**`. same harness as macOS.
- **iOS/iPadOS** — n/a. absent (deliberate). one document at a time; the phones open into the viewer, and iPadOS multitasking is the platform's own answer.
- **Android** — n/a. absent (deliberate). one document at a time, as on iOS.

### Render and scroll (`render-scroll`)

- **Windows** — eng. coverage: engine only. reachable from `src/MegaPDF.App/DocumentView.xaml::PagesScroll`. tests: `tests/MegaPDF.Core.Tests/PdfiumEngineTests.cs::Render`, `tests/MegaPDF.Core.Tests/RenderLimitsTests.cs::RenderLimits`, `tests/MegaPDF.Core.Tests/CorpusTests.cs::Corpus`, `tests/MegaPDF.Core.Tests/UserUnitTests.cs::UserUnit`, `tests/MegaPDF.Core.Tests/SearchCropBoxTests.cs::CropBox`. every self-test state renders a document to get started, so a dead viewer would fail them; nothing asserts scroll position or lazy page arrival.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::PageScroll`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckMinimumWindow`, `tests/MegaPDF.Core.Tests/RenderLimitsTests.cs::RenderLimits`, `tests/MegaPDF.Core.Tests/CorpusTests.cs::Corpus`. layout is asserted in a real window at the declared minimum size (#237); scroll itself only through the tab-transition checks.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::PageScroll`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckMinimumWindow`, `tests/MegaPDF.Core.Tests/RenderLimitsTests.cs::RenderLimits`, `tests/MegaPDF.Core.Tests/CorpusTests.cs::Corpus`. plus the CI steps that start the real UI under Xvfb and at a 150% display scale, which are not part of --self-test.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::ScrollView`. tests: `ios/MegaPDFTests/PdfEngineTests.swift::testRenderProducesInk`, `ios/MegaPDFTests/CropBoxTests.swift::CropBox`, `ios/MegaPDFTests/UserUnitTests.swift::UserUnit`. the UI tests render incidentally; no test asserts what scrolling does.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.page_n`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/PdfEngineTest.kt::render`, `android/engine/src/androidTest/java/com/megapdf/engine/UserUnitTest.kt::class`, `android/app/src/test/java/com/megapdf/android/RenderWindowTest.kt::class`. the render window's page-keeping arithmetic is unit-tested; no instrumented test scrolls.

### Zoom (buttons, keyboard, wheel, pinch) and fit (`zoom`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::ZoomInButton`. tests: `.github/workflows/ci.yml::Run-Check "zoom-anchor"`. keyboard zoom, the 300% clamp and Ctrl+wheel all assert the anchor point has not moved (#528).
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Zoom.cs::ZoomIn`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckZoomAnchor`. real wheel and pinch events injected into a headless window.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Zoom.cs::ZoomIn`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckZoomAnchor`. same harness as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::PinchZoom`. tests: `ios/MegaPDFUITests/ViewerZoomUITests.swift::class`, `ios/MegaPDFTests/ZoomAnchorTests.swift::class`. the one feature CI exercises on both an iPhone and an iPad destination.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::pointerInput(Unit)`. tests: `android/app/src/androidTest/java/com/megapdf/android/PinchZoomTest.kt::class`, `android/app/src/androidTest/java/com/megapdf/android/PinchAnchorTest.kt::class`, `android/app/src/test/java/com/megapdf/android/PinchAnchorMathTest.kt::class`. pinch centroid and double-tap-to-fit on a device.

### Find in document (`search`)

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::FindButton`. tests: `tests/MegaPDF.Core.Tests/SearchParityTests.cs::Search`, `tests/MegaPDF.Core.Tests/SearchCropBoxTests.cs::CropBox`, `tests/MegaPDF.Core.Tests/MatchScrollTests.cs::MatchScroll`, `.github/workflows/ci.yml::Run-Check "progress"`. by hand: `TESTING.md::**Find**`. the progress state stops a long search; the find bar's own counter, wrap and Esc are by hand only. The `find` and `find-zoomed` harness states exist but CI does not run them.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.FindBar.cs::WireFindBar`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckMinimumWindow`, `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `tests/MegaPDF.Core.Tests/SearchParityTests.cs::Search`. by hand: `TESTING.md::**Find**`. case-insensitivity, no-match and wrap are driven through the view model; the bar's layout and button names in three languages by CheckMinimumWindow.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.FindBar.cs::WireFindBar`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckMinimumWindow`, `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `tests/MegaPDF.Core.Tests/SearchParityTests.cs::Search`. by hand: `TESTING.md::**Find**`. same harness as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::Find in document`. tests: `ios/MegaPDFTests/SearchTests.swift::class`. engine parity against the shared fixtures; no UI test opens the find bar.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.close_search`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/TextSearchTest.kt::class`. engine parity only; no Compose test types in the search field. #98's canary lives in the engine suite, not the UI.

### Reading mode (`reading-mode`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::ReadingModeButton`. tests: `.github/workflows/ci.yml::Run-Check "reading"`, `tests/MegaPDF.Core.Tests/ReadingModeTests.cs::ReadingMode`. the richest single state in the Windows harness: accelerators, the Escape ladder, tab order, the floating bar's screen-reader rule, and entering on a new tab.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.ReadingMode.cs::EnterReadingMode`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckReadingMode`, `tests/MegaPDF.Core.Tests/ReadingModeTests.cs::ReadingMode`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.ReadingMode.cs::EnterReadingMode`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckReadingMode`, `tests/MegaPDF.Core.Tests/ReadingModeTests.cs::ReadingMode`.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ReadingBar.swift::ReadingBar`. tests: `ios/MegaPDFUITests/ReadingModeUITests.swift::class`, `ios/MegaPDFTests/ReadingModeTests.swift::class`. run on both the iPhone and the iPad destination, including the iPad tool-strip entry point.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.reading_mode`. tests: `android/app/src/androidTest/java/com/megapdf/android/ReadingModeTest.kt::class`, `android/app/src/test/java/com/megapdf/android/ReadingModeMathTest.kt::class`.

### Page colours (normal / sepia / night) (`page-colours`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::PageColoursChoice`. tests: `.github/workflows/ci.yml::Run-Check "reading"`. asserts the tint really reaches the pixels, persists, and does not force an eager render.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.ReadingMode.cs::PageTint.Sepia`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckReadingMode`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.ReadingMode.cs::PageTint.Sepia`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckReadingMode`.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/SettingsView.swift::pageTint`. tests: `ios/MegaPDFTests/ReadingModeTests.swift::tint`. the stored setting and its descriptions are tested; ReadingModeUITests checks the tint descriptions but not the rendered pixels.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/SettingsScreen.kt::R.string.page_colours`. tests: `android/app/src/androidTest/java/com/megapdf/android/ReadingModeTest.kt::night`, `android/engine/src/test/java/com/megapdf/engine/PageTintTest.kt::class`. the instrumented test checks night-tinted pixels.

### Page thumbnails pane (`pages-pane`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::PagesPaneButton`. tests: `.github/workflows/ci.yml::Run-Check "pages"`.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.PageThumbnailsMenuItem`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.PageThumbnailsMenuItem`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::PagesPanel(model: model,`. tests: `ios/MegaPDFUITests/PageToolsUITests.swift::testThePhonesGridIsASheetThatLeavesThePageOnScreen`. arrived with #570 — a sheet on a compact width and a sidebar on a regular one, both asserted in CI on their own destinations.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.pages`. tests: `android/app/src/androidTest/java/com/megapdf/android/PageToolsTest.kt::class`.

### Form text fields (`form-fields`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/DocumentView.xaml.cs::OnPageTapped`. tests: `.github/workflows/ci.yml::Run-Check "fill"`, `tests/MegaPDF.Core.Tests/AcroFormTests.cs::Field`. by hand: `TESTING.md::**Form fields**`. #590: `fill` opens formtext.pdf's real AcroForm text widget (fixture.pdf has none), types into it and checks the value survives save and reopen. `click` ticks fixture.pdf's drawn square, not a form field — the row's old note crediting it for this was wrong (#590).
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::HandlePageClick`. tests: `tests/MegaPDF.Core.Tests/AcroFormTests.cs::Field`. the self-test's fill-check-sign block drives the view model's hit test and round-trips the value through PDFium.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::HandlePageClick`. tests: `tests/MegaPDF.Core.Tests/AcroFormTests.cs::Field`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerModel.swift::formFields`. tests: `ios/MegaPDFTests/CheckboxTests.swift::class`, `ios/MegaPDFTests/DocumentCapabilitiesTests.swift::class`. the engine and the gating are tested; DemoFlowUITests fills a field live but is excluded from CI as a video script.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.edit_text`. tests: `android/app/src/androidTest/java/com/megapdf/android/EditingTest.kt::class`, `android/app/src/test/java/com/megapdf/android/DocumentCapabilitiesTest.kt::class`.

### Checkboxes, real and drawn (`checkboxes`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::MarkStyleChoice`. tests: `.github/workflows/ci.yml::Run-Check "fill"`, `tests/MegaPDF.Core.Tests/DrawnCheckboxTests.cs::Drawn`, `tests/MegaPDF.Core.Tests/SettingsAndMarkStyleTests.cs::MarkStyle`. by hand: `TESTING.md::**Checkboxes**`. #590: `fill` ticks forms.pdf's real AcroForm checkbox widget and undoes it; `click` (still not run by CI) ticks fixture.pdf's drawn square, the other kind this row covers.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::CheckboxToggleOperation`. tests: `tests/MegaPDF.Core.Tests/DrawnCheckboxTests.cs::Drawn`, `tests/MegaPDF.Core.Tests/SettingsAndMarkStyleTests.cs::MarkStyle`. the self-test ticks both a real AcroForm box and a drawn square through the view model.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::CheckboxToggleOperation`. tests: `tests/MegaPDF.Core.Tests/DrawnCheckboxTests.cs::Drawn`, `tests/MegaPDF.Core.Tests/SettingsAndMarkStyleTests.cs::MarkStyle`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerModel.swift::detectCheckboxSquares`. tests: `ios/MegaPDFTests/CheckboxTests.swift::class`. no mark-style setting on iOS and no UI test taps a box.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/SettingsScreen.kt::R.string.settings`. tests: `android/app/src/androidTest/java/com/megapdf/android/EditingTest.kt::class`, `android/engine/src/androidTest/java/com/megapdf/engine/CheckboxTest.kt::class`.

### Make a signature (draw, type, photo) and the library (`signature-capture`)

The weakest row on the board. Every platform tests the image cleanup and the library's
storage; not one test on any platform drives the draw pad, the type field or the photo
import, which is where #99–#101 lived.

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::SignaturesToolbarButton`. tests: `.github/workflows/ci.yml::Run-Check "sign"`, `tests/MegaPDF.Core.Tests/SignatureCleanupTests.cs::Cleanup`, `tests/MegaPDF.Core.Tests/SignatureLibraryTests.cs::Library`. by hand: `TESTING.md::**Signatures**`. #590: `sign` now runs in CI — the library opens with a seeded card and its thumbnail decodes. `sign-missing` still exists and CI still does not run it. Nothing drives the pad, the typed field or a photo import.
- **macOS** — eng+m. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Toolbar.cs::SignButton`. tests: `tests/MegaPDF.Core.Tests/SignatureCleanupTests.cs::Cleanup`, `tests/MegaPDF.Core.Tests/SignatureLibraryTests.cs::Library`. by hand: `TESTING.md::**Signatures**`. the self-test deliberately synthesises raw pixels rather than decoding a PNG, so the real image path is never run.
- **Linux** — eng+m. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Toolbar.cs::SignButton`. tests: `tests/MegaPDF.Core.Tests/SignatureCleanupTests.cs::Cleanup`, `tests/MegaPDF.Core.Tests/SignatureLibraryTests.cs::Library`. by hand: `TESTING.md::**Signatures**`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/SignatureViews.swift::DrawSignatureView`. tests: `ios/MegaPDFTests/SignatureTests.swift::Processor`, `ios/MegaPDFTests/SignatureStoreTests.swift::class`. cleanup and store only; the capture sheets are driven by DemoFlowUITests, which is excluded from CI.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/SignaturesSheet.kt::R.string.signatures`. tests: `android/app/src/test/java/com/megapdf/android/SignatureImageProcessorTest.kt::class`, `android/app/src/test/java/com/megapdf/android/SignatureLibraryStoreTest.kt::class`. no instrumented test opens the signatures sheet at all.

### Place, move, resize and remove a signature (`signature-place`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::SignaturesFlyout`. tests: `.github/workflows/ci.yml::Run-Check "sign"`, `tests/MegaPDF.Core.Tests/SignatureStampTests.cs::Stamp`, `tests/MegaPDF.Core.Tests/HitTargetGeometryTests.cs::HitTarget`. by hand: `TESTING.md::**Signatures**`. #590: `sign` places a card on the page, checks it is 180pt wide with its real aspect ratio preserved and centred on the click (the geometry SDD §3.3 promises), selects it for move/resize the same chrome `whiteout-text` already proved, drags it, and undoes it.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::PlaceSignature`. tests: `tests/MegaPDF.Core.Tests/SignatureStampTests.cs::Stamp`, `tests/MegaPDF.Core.Tests/HitTargetGeometryTests.cs::HitTarget`. the self-test places one and asserts its width, aspect and centring, and undoes it.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::PlaceSignature`. tests: `tests/MegaPDF.Core.Tests/SignatureStampTests.cs::Stamp`, `tests/MegaPDF.Core.Tests/HitTargetGeometryTests.cs::HitTarget`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::Label("Sign"`. tests: `ios/MegaPDFTests/SignatureTests.swift::stamp`. engine placement only.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.sign`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/SignatureStampTest.kt::class`, `android/app/src/test/java/com/megapdf/android/HitTargetGeometryTest.kt::class`. the stamp round-trips on a device; no Compose test places one through the UI.

### Add text (`add-text`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::AddTextButton`. tests: `.github/workflows/ci.yml::Run-Check "whiteout-text"`, `tests/MegaPDF.Core.Tests/WhiteoutAndTextBoxTests.cs::TextBox`, `tests/MegaPDF.Core.Tests/TextBoxSubstitutionTests.cs::Substitution`. by hand: `TESTING.md::**Add text**`. size chips, multi-line splitting and restyle-on-re-edit, since #3/#4.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.AddText`. tests: `tests/MegaPDF.Core.Tests/WhiteoutAndTextBoxTests.cs::TextBox`, `tests/MegaPDF.Core.Tests/TextBoxSubstitutionTests.cs::Substitution`, `tests/MegaPDF.Core.Tests/FontSubstitutionTests.cs::Font`. the self-test adds boxes, splits on Shift+Enter, restyles and undoes, through the view model.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.AddText`. tests: `tests/MegaPDF.Core.Tests/WhiteoutAndTextBoxTests.cs::TextBox`, `tests/MegaPDF.Core.Tests/TextBoxSubstitutionTests.cs::Substitution`, `tests/MegaPDF.Core.Tests/FontSubstitutionTests.cs::Font`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::Label("Add text"`. tests: `ios/MegaPDFTests/AddTextTests.swift::class`. geometry, style and round-trip at the engine; no UI test places a box.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.add_text`. tests: `android/app/src/androidTest/java/com/megapdf/android/TextBoxMultilineTest.kt::class`, `android/engine/src/androidTest/java/com/megapdf/engine/TextBoxTest.kt::class`, `android/engine/src/test/java/com/megapdf/engine/PdfWriteTextOptionsTest.kt::class`.

### Edit the document's own text (`body-text-edit`)

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::FontPicker`. tests: `tests/MegaPDF.Core.Tests/TextEditSpikeTests.cs::TextEdit`, `tests/MegaPDF.Core.Tests/LineEditingTests.cs::Line`, `tests/MegaPDF.Core.Tests/TextDeleteTests.cs::Delete`, `tests/MegaPDF.Core.Tests/SubstitutedEditUndoTests.cs::Undo`, `tests/MegaPDF.Core.Tests/LayoutGuardTests.cs::Guard`. by hand: `TESTING.md::**Text**`. the engine side is the best-tested part of the app; no Windows self-test state retypes a line in the UI.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::EditLine`. tests: `tests/MegaPDF.Core.Tests/TextEditSpikeTests.cs::TextEdit`, `tests/MegaPDF.Core.Tests/LineEditingTests.cs::Line`, `tests/MegaPDF.Core.Tests/TextDeleteTests.cs::Delete`, `tests/MegaPDF.Core.Tests/SubstitutedEditUndoTests.cs::Undo`, `tests/MegaPDF.Core.Tests/LayoutGuardTests.cs::Guard`. the self-test retypes a line through the view model and reopens the file.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::EditLine`. tests: `tests/MegaPDF.Core.Tests/TextEditSpikeTests.cs::TextEdit`, `tests/MegaPDF.Core.Tests/LineEditingTests.cs::Line`, `tests/MegaPDF.Core.Tests/TextDeleteTests.cs::Delete`, `tests/MegaPDF.Core.Tests/SubstitutedEditUndoTests.cs::Undo`, `tests/MegaPDF.Core.Tests/LayoutGuardTests.cs::Guard`. same as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/BodyTextSheet.swift::BodyTextSheet`. tests: `ios/MegaPDFUITests/BodyTextEditUITests.swift::class`, `ios/MegaPDFTests/BodyTextTests.swift::class`. the only live UI test of this on any platform, and it runs in CI.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.edit_text_hint`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/BodyTextTest.kt::class`. engine only — iOS has a live UI test for the same feature (#113/#114) and Android does not.

### Whiteout (cover) (`whiteout`)

The row that made the case for this map. Android had no whiteout tool at all until #3/#4
landed on 2026-09-30, while the issue that produced them discussed improving its chrome on
four platforms. iOS still has none, and the comment that explains why
(ios/MegaPDF/Engine/PdfEngine+Redaction.swift: "iOS has no whiteout — mobile's feature set
is fill, check, sign, find and add text") describes a mobile feature set that Android no
longer has.

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::WhiteoutButton`. tests: `.github/workflows/ci.yml::Run-Check "whiteout-text"`, `tests/MegaPDF.Core.Tests/WhiteoutAndTextBoxTests.cs::Whiteout`. by hand: `TESTING.md::**Whiteout**`.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.Cover`. tests: `tests/MegaPDF.Core.Tests/WhiteoutAndTextBoxTests.cs::Whiteout`. placed and round-tripped through the view model; the move/resize chrome the Windows state drives is not asserted here.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.Cover`. tests: `tests/MegaPDF.Core.Tests/WhiteoutAndTextBoxTests.cs::Whiteout`. same as macOS.
- **iOS/iPadOS** — GAP. absent (gap). no whiteout tool. The reason given in the engine source is that mobile's feature set excludes it — which stopped being true when Android got one on 2026-09-30.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.whiteout`. tests: `android/app/src/androidTest/java/com/megapdf/android/WhiteoutLifecycleTest.kt::class`. arrived with its own instrumented test (#3/#4), which is why it is the strongest mobile cell here.

### Redact — mark text or an area, move, resize, remove, clear all (`redact-mark`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::RedactButton`. tests: `.github/workflows/ci.yml::Run-Check "redact"`, `tests/MegaPDF.Core.Tests/RedactionMarkTests.cs::Mark`. by hand: `TESTING.md::**Redact**`. #590: `redact` now drags a mark in the real window, moves, resizes, removes, undoes, redoes, clears all and undoes the clear — the #329 lifecycle, through the window rather than only at the engine. #329.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.ToolbarRedact`. tests: `tests/MegaPDF.Core.Tests/RedactionMarkTests.cs::Mark`. by hand: `TESTING.md::**Redact**`. the self-test drags a mark, moves, resizes, removes, clears all and undoes each, and checks marking leaves the document clean (#329).
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.ToolbarRedact`. tests: `tests/MegaPDF.Core.Tests/RedactionMarkTests.cs::Mark`. by hand: `TESTING.md::**Redact**`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::viewerRedact`. tests: `ios/MegaPDFTests/RedactionTests.swift::class`. the unit suite is thorough (mark, area with no text, move, remove, undo, redo); the UI test that checks the Redact row is armed is excluded from CI because the row is missing on the simulator (#405). #405.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.redact`. tests: `android/app/src/androidTest/java/com/megapdf/android/RedactionMarkLifecycleTest.kt::class`, `android/app/src/test/java/com/megapdf/android/RedactionMarkHistoryTest.kt::class`.

### Redact — apply on save, and prove the words are gone (`redact-apply`)

The promise the feature exists to keep. It is proved by hunting for what was removed
(tools/leakcheck), not by asking the engine — but only in the native and mobile suites.
The .NET side has no test that calls ApplyRedactions at all.

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/DocumentViewModel.cs::ApplyRedactions`. tests: `core/tests/core_tests.cpp::test_redaction_fixtures`, `tests/MegaPDF.Core.Tests/RedactionApplyTests.cs::Apply_RemovesTheMarkedWord_FromTheExtractedTextAndFromTheBytes`. by hand: `TESTING.md::Open the saved copy **in Edge or Acrobat**`. RedactionApplyTests arrived with this map, because the cell was empty: the .NET apply path the Windows app takes had no test at all. It hunts for the word in the saved bytes the way tools/leakcheck does rather than asking the engine. No Windows self-test state marks and saves, so the UI above it is still uncovered.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::ApplyRedactions`. tests: `core/tests/core_tests.cpp::test_redaction_fixtures`. by hand: `TESTING.md::Open the saved copy **in Edge or Acrobat**`. the self-test applies and then checks the bytes of the saved file no longer contain the words — the one place the app's own apply path is exercised in ci.yml.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::ApplyRedactions`. tests: `core/tests/core_tests.cpp::test_redaction_fixtures`. by hand: `TESTING.md::Open the saved copy **in Edge or Acrobat**`. same as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::Overwrite the original`. tests: `ios/MegaPDFTests/RedactionTests.swift::apply`, `ios/MegaPDFUITests/RedactionUITests.swift::testSaveIsReachableWithAMarkAndAsksBeforeRemoving`.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.redact_overwrite`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/RedactionTest.kt::apply`. the engine's apply is proved on a device, including image pixels; no Compose test taps through to the save-a-copy-or-overwrite question.

### Redact — the refusal path ("nothing was removed") (`redact-refusal`)

The deliberate refusal: if some of what was marked cannot be taken apart safely, nothing
is removed and the reason is shown. Worth a row of its own because a refusal path that no
test reaches reads as a clean zero — which is what happened to the page-tools refusal
counters for two whole battery runs before #567's reporting fix.

- **Windows** — eng. coverage: engine only. reachable from `src/MegaPDF.App/DocumentViewModel.cs::RefusalReason`. tests: `core/tests/core_tests.cpp::refus`, `tests/MegaPDF.Core.Tests/RedactionApplyTests.cs::Apply_WithAMarkOverEmptySpace_RemovesNothing_AndLeavesTheTextAlone`. no .NET test produces a real refusal — the native suite and tools/stress/redaction-battery.sh are the only places one is seen. What this map's own test does cover is the quieter half: a mark over empty space must report zero removed and leave what is beside it alone, so a zero cannot mean nothing looked (the #567 shape).
- **macOS** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::RefusalReason`. tests: `core/tests/core_tests.cpp::refus`. the self-test applies successfully; it never makes the engine refuse, so the message the user would read is unchecked in ci.yml.
- **Linux** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::RefusalReason`. tests: `core/tests/core_tests.cpp::refus`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::redactionRefusal`. tests: `ios/MegaPDFTests/RedactionTests.swift::refus`. the refusal leaves the document unchanged; the banner itself is not driven.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerViewModel.kt::refusal`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/RedactionTest.kt::refus`. the engine refuses and leaves the file alone; no Compose test reads the refusal message back.

### Rotate and delete pages (`page-rotate-delete`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::RotateLeftItem`. tests: `.github/workflows/ci.yml::Run-Check "pages"`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Rotate`. 107 checks over eleven documents, including the renumbering core contract 10 owes and the journal replay.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.RotatePageLeft`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Rotate`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.RotatePageLeft`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Rotate`.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/PagesView.swift::Rotate Left`. tests: `ios/MegaPDFUITests/PageToolsUITests.swift::testRotatingASelectionIsOneChangeAndOneUndo`, `ios/MegaPDFTests/PageToolsTests.swift::testDeletingPagesTakesExactlyThoseAndUndoPutsThePagesThemselvesBack`, `ios/MegaPDFTests/PageToolsModelTests.swift::testTheSizesFollowARotationAndItsPictureIsRedrawn`, `ios/MegaPDFTests/PageCheckTests.swift::class`.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/PagesScreen.kt::R.string.rotate_left`. tests: `android/app/src/androidTest/java/com/megapdf/android/PageToolsTest.kt::rotat`, `android/engine/src/androidTest/java/com/megapdf/engine/PageToolsEngineTest.kt::class`, `android/app/src/test/java/com/megapdf/android/PageHistoryTest.kt::class`.

### Reorder pages, insert a blank page, insert pages from a file (`page-reorder-insert`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::InsertPagesFromFileItem`. tests: `.github/workflows/ci.yml::Run-Check "pages"`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Insert`. combine is checked including the field-name clash refusal.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.InsertPagesFromFileItem`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Insert`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.InsertPagesFromFileItem`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Insert`.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/PagesView.swift::Insert Pages from File…`. tests: `ios/MegaPDFTests/PageToolsTests.swift::testImportingPagesInsertsThemAndUndoTakesExactlyThoseOff`, `ios/MegaPDFTests/PageShiftTests.swift::testInsertedPagesMoveEverythingFromThereUp`, `ios/MegaPDFTests/PageToolsModelTests.swift::testABlankPageTakesTheSizeOfThePageInFrontOfIt`. reorder by dragging is a by-hand gate on this platform: PageDragUITests exists and CI does not run it.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/PagesScreen.kt::R.string.pages_add_from_file`. tests: `android/app/src/androidTest/java/com/megapdf/android/PageToolsTest.kt::insert`, `android/app/src/test/java/com/megapdf/android/PageShiftTest.kt::class`, `android/app/src/test/java/com/megapdf/android/PageRewriteWarningsTest.kt::class`, `android/app/src/test/java/com/megapdf/android/PageCheckGateTest.kt::class`.

### Extract pages to a new file (`page-extract`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::ExtractPagesItem`. tests: `.github/workflows/ci.yml::Run-Check "pages"`, `.github/workflows/ci.yml::Run-Check "progress"`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Extract`. extract is also where the progress state proves Stop leaves no partial file.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.ExtractPagesItem`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`, `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Extract`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.ExtractPagesItem`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`, `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `tests/MegaPDF.Core.Tests/PageToolsTests.cs::Extract`.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/PagesView.swift::Save Pages As…`. tests: `ios/MegaPDFTests/PageToolsTests.swift::testExtractingPagesWritesThemInOrderAndLeavesTheDocumentAlone`, `ios/MegaPDFTests/PagesFieldHierarchyTests.swift::class`. the extract name and every refusal sentence are asserted too; the picker the file goes through is not.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/PagesScreen.kt::R.string.pages_save_selection`. tests: `android/app/src/androidTest/java/com/megapdf/android/PageToolsTest.kt::save`.

### Save (overwrite in place) (`save-in-place`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::SaveButton`. tests: `.github/workflows/ci.yml::Run-Check "save"`, `tests/MegaPDF.Core.Tests/VerifiedSaveTests.cs::Save`, `tests/MegaPDF.Core.Tests/AtomicFileWriterTests.cs::Atomic`, `tests/MegaPDF.Core.Tests/StagedStreamWriterTests.cs::Staged`, `tests/MegaPDF.Core.Tests/FlattenTests.cs::Flatten`. by hand: `TESTING.md::**Saving**`. #590: `save` now presses the real SaveCommand on a dirtied document, checks the unsaved flag clears, the bytes on disk actually change, and an independently reopened copy has what was added. A read-only or provider-backed location is still never used.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.Save`. tests: `tests/MegaPDF.Core.Tests/VerifiedSaveTests.cs::Save`, `tests/MegaPDF.Core.Tests/AtomicFileWriterTests.cs::Atomic`, `tests/MegaPDF.Core.Tests/StagedStreamWriterTests.cs::Staged`, `tests/MegaPDF.Core.Tests/FlattenTests.cs::Flatten`. by hand: `TESTING.md::**Saving**`. the self-test saves by stream and by path and checks busy-state locking during the save.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.Save`. tests: `tests/MegaPDF.Core.Tests/VerifiedSaveTests.cs::Save`, `tests/MegaPDF.Core.Tests/AtomicFileWriterTests.cs::Atomic`, `tests/MegaPDF.Core.Tests/StagedStreamWriterTests.cs::Staged`, `tests/MegaPDF.Core.Tests/FlattenTests.cs::Flatten`. by hand: `TESTING.md::**Saving**`. also the only platform where CI checks saves are not staged on a RAM-backed filesystem and that an in-place save works where a hidden temp file is not allowed (snap, #158).
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::saveTapped`. tests: `ios/MegaPDFTests/PdfEngineTests.swift::save`, `ios/MegaPDFTests/PageCheckTests.swift::class`. FilesEndToEndUITests saves through the real picker and is excluded from CI.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.save`. tests: `android/app/src/androidTest/java/com/megapdf/android/FileCommandsTest.kt::class`, `android/app/src/test/java/com/megapdf/android/DirtyTrackerTest.kt::class`, `android/engine/src/androidTest/java/com/megapdf/engine/PdfEngineTest.kt::save`. the 2.0.1 bug was open-from-Recents-after-a-reboot then Save; the reboot half is still untested anywhere.

### Save a copy / Save As (`save-a-copy`)

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::SaveAsButton`. tests: `tests/MegaPDF.Core.Tests/VerifiedSaveTests.cs::Copy`. by hand: `TESTING.md::**Saving**`. no self-test state drives Save As; the picker is the OS's.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.SaveAs`. tests: `tests/MegaPDF.Core.Tests/VerifiedSaveTests.cs::Copy`. by hand: `TESTING.md::**Saving**`. the self-test checks Save As adopts the copy as the open document (#68).
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.SaveAs`. tests: `tests/MegaPDF.Core.Tests/VerifiedSaveTests.cs::Copy`. by hand: `TESTING.md::**Saving**`. same as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::Save a copy`. tests: `ios/MegaPDFTests/SaveACopyAdoptionTests.swift::class`, `ios/MegaPDFTests/SignatureDetectionTests.swift::copy`. #572: that the copy becomes the open document is tested in CI. The live export sheet is only in FilesEndToEndUITests, which is excluded from CI and, as of #589, does not present at all.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.save_a_copy`. tests: `android/app/src/androidTest/java/com/megapdf/android/FileCommandsTest.kt::copy`.

### Export as Markdown (`export-markdown`)

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/DocumentViewModel.cs::Strings.MarkdownDocumentFilter`. tests: `tests/MegaPDF.Core.Tests/DocumentTextExportTests.cs::Markdown`, `tests/MegaPDF.Core.Tests/SaveAsExportTests.cs::Export`. by hand: `TESTING.md::**Save As → Markdown**`. the goldens and the extension-to-kind rule are tested; nothing checks the PDF is left untouched except by hand (#386).
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::ExportMarkdownAsync`. tests: `tests/MegaPDF.Core.Tests/DocumentTextExportTests.cs::Markdown`, `tests/MegaPDF.Core.Tests/SaveAsExportTests.cs::Export`. by hand: `TESTING.md::**Save As → Markdown**`. the self-test exports and checks the dot and the busy wording (#386).
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::ExportMarkdownAsync`. tests: `tests/MegaPDF.Core.Tests/DocumentTextExportTests.cs::Markdown`, `tests/MegaPDF.Core.Tests/SaveAsExportTests.cs::Export`. by hand: `TESTING.md::**Save As → Markdown**`. same as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::Export as Markdown`. tests: `ios/MegaPDFUITests/MarkdownExportUITests.swift::class`, `ios/MegaPDFTests/StructureExportTests.swift::class`.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.export_markdown`. tests: `android/app/src/androidTest/java/com/megapdf/android/FileCommandsTest.kt::markdown`, `android/app/src/test/java/com/megapdf/android/MarkdownExportNameTest.kt::class`, `android/engine/src/androidTest/java/com/megapdf/engine/WriteTextGoldenTest.kt::class`. #409 (the picker could not write Markdown) was found by hand after CI was green.

### Share (`share`)

- **Windows** — n/a. absent (deliberate). the desktops offer Save As and the file manager instead; there is no Share on the Windows toolbar.
- **macOS** — n/a. absent (deliberate). as Windows.
- **Linux** — n/a. absent (deliberate). as Windows.
- **iOS/iPadOS** — eng+m. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::Share without saving`. tests: `ios/MegaPDFTests/ShareAndExternalOpenTests.swift::share`. by hand: `docs/RELEASING.md::**Share** on the`. ShareVerificationUITests drives the real sheet and is excluded from CI for Files-app flakiness.
- **Android** — UI+m. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.share`. tests: `android/app/src/androidTest/java/com/megapdf/android/FileCommandsTest.kt::share`, `android/app/src/test/java/com/megapdf/android/ShareFileTest.kt::class`. by hand: `docs/RELEASING.md::**Share** on the`.

### Password — set, change, remove, and opening a protected document (`password`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::SecurityButton`. tests: `.github/workflows/ci.yml::Run-Check "security"`, `tests/MegaPDF.Core.Tests/PasswordTests.cs::Password`, `tests/MegaPDF.Core.Tests/SecurityTests.cs::Security`. #590: `security` opens the real Set Password dialog and reads it back (title, the two real password fields) through DialogGate.Current, cancels it — WinUI has no way to press an ad hoc ContentDialog's Primary button outside UI Automation on a live session (#462) — then drives the save-with-a-password path directly and proves the saved file is genuinely encrypted by reopening it with and without the password. Still the only platform besides iOS (excluded from CI) with any UI-level cover here; set/change/remove end-to-end through a real click is still nobody's.
- **macOS** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.SecurityToolbar`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPasswordPromptAcrossTabSwitch`, `tests/MegaPDF.Core.Tests/PasswordTests.cs::Password`, `tests/MegaPDF.Core.Tests/SecurityTests.cs::Security`. the self-test checks a password prompt survives a tab switch but never sets, changes or removes a password through the UI.
- **Linux** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.SecurityToolbar`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckPasswordPromptAcrossTabSwitch`, `tests/MegaPDF.Core.Tests/PasswordTests.cs::Password`, `tests/MegaPDF.Core.Tests/SecurityTests.cs::Security`. same as macOS.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/DocumentSecuritySheet.swift::DocumentSecuritySheet`. tests: `ios/MegaPDFTests/PdfEngineTests.swift::password`, `ios/MegaPDFUITests/FilesEndToEndUITests.swift::setPassword`. set-password and wrong-password-then-remove are real UI tests, and both are excluded from CI — the only platform with a UI test for this and it does not run.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/SecurityDialogs.kt::R.string.security_password_title`. tests: `android/engine/src/androidTest/java/com/megapdf/engine/PdfEngineTest.kt::password`. no instrumented test opens the password sheet.

### A withheld permission is said out loud, and the person may continue (`permission-override`)

Dave's decision on #558, 2026-09-30, recorded as ADR-004 decision 11: when a document's
permissions forbid what someone is about to do, the app says its author asked that it not
be done and offers to continue anyway. The bits are advisory — any tool with the owner
password clears them, plenty of tools ignore them, and the person in front of the app may
well be the author — so a refusal treats a request as a lock, which it is not.

Four platforms, one shape; Android built it first and the comment on #558 is what the other
three copy. The three outstanding cells below are *regressions of a kind this map exists to
show*: each one has a working refusal, so no test fails — they are walls that pass.

- **Windows** — GAP. absent (gap). still refuses. DocumentCapabilities.cs reads the bits and the toolbar greys out what the author withheld; megapdf_security_override() is in the core and unbound in CoreNative.cs. #558.
- **macOS** — GAP. absent (gap). same as Windows — the shared MegaPDF.Core DocumentCapabilities both desktops consume is the half that has to change, so these two land together. #558.
- **Linux** — GAP. absent (gap). same as macOS. #558.
- **iOS/iPadOS** — GAP. absent (gap). iOS page tools landed in #570 consulting no permission bit at all, so this is the one platform where the question has to arrive with the detection rather than replace it. #558.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/MainActivity.kt::R.string.permission_ask_note`. tests: `android/app/src/androidTest/java/com/megapdf/android/PermissionPromptTest.kt::class`, `android/app/src/test/java/com/megapdf/android/PermissionOverrideTest.kt::class`, `core/tests/core_tests.cpp::megapdf_security_override`. the five Compose tests were pushed as their own commit and shown red on CI first. Continue is proved on the page's own reported size swapping after a quarter turn, which can only happen if the choice reached the C++ core — the core enforces the same bits underneath every platform, so an app-only Continue comes back MEGAPDF_ERR_RESTRICTED.

### Shrink for email (`shrink`)

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::ShrinkButton`. tests: `.github/workflows/ci.yml::Run-Check "progress"`, `tests/MegaPDF.Core.Tests/ImageShrinkerTests.cs::Shrink`, `tests/MegaPDF.Core.Tests/ImageCompressionTests.cs::Compress`. by hand: `TESTING.md::**Shrink for email**`. progress and a deterministic in-loop Stop, with no partial file left behind.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Toolbar.cs::CommandEntry(Strings.Shrink`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `tests/MegaPDF.Core.Tests/ImageShrinkerTests.cs::Shrink`, `tests/MegaPDF.Core.Tests/ImageCompressionTests.cs::Compress`. by hand: `TESTING.md::**Shrink for email**`. stoppability and the rule that the open document is never what shrink was working on.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Toolbar.cs::CommandEntry(Strings.Shrink`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `tests/MegaPDF.Core.Tests/ImageShrinkerTests.cs::Shrink`, `tests/MegaPDF.Core.Tests/ImageCompressionTests.cs::Compress`. by hand: `TESTING.md::**Shrink for email**`. same as macOS.
- **iOS/iPadOS** — GAP. absent (gap). no shrink on iOS. Nothing in the source says it was decided against, and a phone mailing a scan is the case it exists for.
- **Android** — GAP. absent (gap). no shrink on Android either, for the same reason and with the same silence.

### Print (`print`)

- **Windows** — —+m. coverage: nothing. reachable from `src/MegaPDF.App/MainWindow.xaml::PrintButton`. by hand: `TESTING.md::**Print**`. nothing automated touches printing on Windows: no self-test state, no core test of PdfPrinter, and tools/windows-qa's print flow is manual.
- **macOS** — —+m. coverage: nothing. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Toolbar.cs::CommandEntry(Strings.Print`. by hand: `TESTING.md::**Print**`. the self-test checks the command's enablement and nothing else; no print is ever performed in CI.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Platform/LinuxPrinter.cs::Print`. tests: `.github/workflows/ci.yml::The CUPS printing route is sound`. by hand: `TESTING.md::**Print**`. the best-covered print of the five: a CI step runs --print-check against CUPS, and check-flatpak.sh probes the portal path. The actual dialog is still by hand.
- **iOS/iPadOS** — GAP. absent (gap). no UIPrintInteractionController anywhere in ios/MegaPDF. AirPrint is what a phone user would reach for and there is no route to it.
- **Android** — GAP. absent (gap). no PrintManager anywhere in android/app. Same shape as iOS.

### Progress and Stop on long operations (`progress-cancel`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::BusyCancelButton`. tests: `.github/workflows/ci.yml::Run-Check "progress"`, `tests/MegaPDF.Core.Tests/BusyStateTests.cs::Busy`.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::BusyStrip`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `src/MegaPDF.Avalonia/Program.cs::CheckBusyStripControls`, `tests/MegaPDF.Core.Tests/BusyStateTests.cs::Busy`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml::BusyStrip`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckProgressAndCancel`, `src/MegaPDF.Avalonia/Program.cs::CheckBusyStripControls`, `tests/MegaPDF.Core.Tests/BusyStateTests.cs::Busy`.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::busy`. tests: `ios/MegaPDFTests/BusyStateTests.swift::class`, `ios/MegaPDFTests/NoticeLifetimeTests.swift::class`. the timing state machine only; no UI test sees a busy strip or presses Stop.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::BusyStopSnackbar`. tests: `android/app/src/test/java/com/megapdf/android/BusyStateTest.kt::class`, `android/app/src/test/java/com/megapdf/android/PageOperationBusyLabelTest.kt::class`, `android/app/src/androidTest/java/com/megapdf/android/PageToolsProgressTest.kt::class`.

### Undo and redo (`undo-redo`)

#330's own first example: mark for redaction then Undo. Undo is tested per operation on
every platform and the chains are deep, so this row is better than most — the thin part is
that undo past the start and redo after a new edit are only checked in the shared stack.

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::UndoButton`. tests: `tests/MegaPDF.Core.Tests/UndoStackTests.cs::Undo`, `.github/workflows/ci.yml::Run-Check "whiteout-text"`.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.Undo`. tests: `tests/MegaPDF.Core.Tests/UndoStackTests.cs::Undo`, `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs::Strings.Undo`. tests: `tests/MegaPDF.Core.Tests/UndoStackTests.cs::Undo`, `src/MegaPDF.Avalonia/Program.cs::CheckPageTools`.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::Label("Undo"`. tests: `ios/MegaPDFTests/UndoTests.swift::class`. cross-feature undo is unit-tested; no UI test presses the Undo button.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/PagesScreen.kt::R.string.undo`. tests: `android/app/src/androidTest/java/com/megapdf/android/PageToolsTest.kt::undo`, `android/app/src/test/java/com/megapdf/android/PageHistoryTest.kt::undo`.

### Closing with unsaved changes (`unsaved-prompt`)

- **Windows** — —+m. coverage: nothing. reachable from `src/MegaPDF.App/MainWindow.xaml.cs::ConfirmSaveChangesAsync`. by hand: `TESTING.md::unsaved changes in two tabs`. no self-test state closes a dirty document; the per-tab Cancel case TESTING.md describes is by hand only.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml.cs::ConfirmClose`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckMultiWindowQuitConfirmation`. by hand: `TESTING.md::unsaved changes in two tabs`.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml.cs::ConfirmClose`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckMultiWindowQuitConfirmation`, `src/MegaPDF.Avalonia/Program.cs::CheckLinuxCloseAndQuit`. by hand: `TESTING.md::unsaved changes in two tabs`. Linux has a check of its own for close-versus-quit.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerView.swift::Discard`. tests: `ios/MegaPDFTests/ShareAndExternalOpenTests.swift::unsaved`. the share-with-unsaved-changes branch is tested; closing dirty is not driven in the UI.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.unsaved_changes`. tests: `android/app/src/androidTest/java/com/megapdf/android/OpenWithTest.kt::unsaved`, `android/app/src/test/java/com/megapdf/android/DirtyTrackerTest.kt::dirty`.

### Recovery after a crash (`crash-recovery`)

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml.cs::Strings.RestoreTitle`. tests: `tests/MegaPDF.Core.Tests/RecoveryJournalTests.cs::Journal`, `tests/MegaPDF.Core.Tests/HiddenCopyTests.cs::HiddenCopy`, `tests/MegaPDF.Core.Tests/LaunchedDocumentTests.cs::Launched`. by hand: `TESTING.md::**Sturdiness**`. the journal and its replay are well covered; the restore dialog is never shown in CI, and the three-tabs-restored case TESTING.md describes is by hand only.
- **macOS** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml.cs::AskAboutRecoveryAsync`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckLaunchAfterCrash`, `tests/MegaPDF.Core.Tests/RecoveryJournalTests.cs::Journal`, `tests/MegaPDF.Core.Tests/HiddenCopyTests.cs::HiddenCopy`. by hand: `TESTING.md::**Sturdiness**`.
- **Linux** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.axaml.cs::AskAboutRecoveryAsync`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckLaunchAfterCrash`, `tests/MegaPDF.Core.Tests/RecoveryJournalTests.cs::Journal`, `tests/MegaPDF.Core.Tests/HiddenCopyTests.cs::HiddenCopy`. by hand: `TESTING.md::**Sturdiness**`.
- **iOS/iPadOS** — n/a. absent (deliberate). iOS apps are suspended rather than killed mid-edit, and the platform's own state restoration covers the case; no recovery journal on this platform.
- **Android** — n/a. absent (deliberate). as iOS: no recovery journal. Process death is the Android shape of this and it has its own row.

### Background, foreground and process death (`process-death`)

- **Windows** — n/a. absent (deliberate). a desktop app is not suspended by the system; crash-recovery is the desktop shape of this.
- **macOS** — n/a. absent (deliberate). as Windows.
- **Linux** — n/a. absent (deliberate). as Windows.
- **iOS/iPadOS** — —. coverage: nothing. reachable from `ios/MegaPDF/MegaPDFApp.swift::WindowGroup`. a blind spot. No test backgrounds the app, and nothing checks what survives being killed while a document is open with edits.
- **Android** — —. coverage: nothing. reachable from `android/app/src/main/java/com/megapdf/android/MainActivity.kt::onNewIntent`. a blind spot, and the one the 2.0.1 save-after-reboot bug came through: the persisted URI grant outlives the process and nothing tests that it does. UiAutomator can drive process death on the emulator CI already runs.

### Device rotation (`device-rotation`)

- **Windows** — n/a. absent (deliberate). window resize is the desktop equivalent, and CheckMinimumWindow covers the smallest case.
- **macOS** — n/a. absent (deliberate). as Windows.
- **Linux** — n/a. absent (deliberate). as Windows.
- **iOS/iPadOS** — —. coverage: nothing. reachable from `ios/project.yml::UISupportedInterfaceOrientations`. a blind spot. The app declares every orientation and no test rotates anything, on the iPhone or the iPad destination.
- **Android** — —. coverage: nothing. reachable from `android/app/src/main/AndroidManifest.xml::android:name=".MainActivity"`. a blind spot, and rotation on Android destroys and recreates the activity, which is where unsaved edits and an armed tool would go missing.

### Very large documents (1, 2.5 and 4.5 GB) (`large-files`)

Tested at two tiers because the defects only show at sizes no repository can hold. The
small tier runs on every push; the large one runs where the fixtures exist, which is never
in CI and by design (#147, #148, #267, #270).

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/DocumentViewModel.cs::OpenDocumentAsync`. tests: `core/tests/core_tests.cpp::test_xref_entries_are_findable`. by hand: `docs/RELEASING.md::2.3 Real-machine verification`. the cross-reference checks run on every push on the ordinary fixtures; the 2.5 and 4.5 GB cases skip themselves unless MEGAPDF_LARGE_FIXTURES is set, so CI never runs them. tools/windows-qa has the by-hand large-file pass.
- **macOS** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::OpenAsync`. tests: `core/tests/core_tests.cpp::test_xref_entries_are_findable`. as Windows; no large fixture in CI.
- **Linux** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::OpenAsync`. tests: `core/tests/core_tests.cpp::test_xref_entries_are_findable`. as Windows; the corpus batteries on k3 are where real large documents are seen.
- **iOS/iPadOS** — eng. coverage: engine only. reachable from `ios/MegaPDF/ViewerModel.swift::open`. tests: `ios/MegaPDFUITests/FilesEndToEndUITests.swift::1 GB`. a real 1 GB open, search and save-a-copy with timings — and excluded from CI because the fixture is 1 GB. tools/ios-files-e2e.sh is how it runs.
- **Android** — —. coverage: nothing. reachable from `android/app/src/main/java/com/megapdf/android/ViewerViewModel.kt::open`. a blind spot: no large-file test of any size on Android, in CI or by hand.

### English, fr-CA and fr-FR (`localisation`)

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::LanguageChoice`. tests: `tests/MegaPDF.Core.Tests/StringCatalogueTests.cs::Catalogue`. by hand: `docs/RELEASING.md::switch to **fr-CA** and read the chrome`. catalogue completeness for all four platforms is asserted; no Windows run happens in French, and the French-width pass in tools/windows-qa is manual.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Platform/MacLanguage.cs::PreferredLanguages`. tests: `tests/MegaPDF.Core.Tests/StringCatalogueTests.cs::Catalogue`, `src/MegaPDF.Avalonia/Program.cs::CheckMinimumWindow`. CheckMinimumWindow lays the find bar and the empty state out in en, fr-CA and fr-FR at the declared minimum (#237) — the rest of --self-test is pinned to en-US on purpose so its PASS lines read the same.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Platform/LinuxLanguage.cs::LANGUAGE`. tests: `tests/MegaPDF.Core.Tests/StringCatalogueTests.cs::Catalogue`, `src/MegaPDF.Avalonia/Program.cs::CheckMinimumWindow`, `.github/workflows/ci.yml::The app runs in French`. the only platform where CI starts the real UI in French and reads the POSIX locale environment back.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/SettingsView.swift::settingsPageColours`. tests: `.github/workflows/ios-ci.yml::-testLanguage fr`. the whole unit suite is re-run in fr-CA, which proves the catalogue compiles and the app launches — no test asserts any French string.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/res/values/strings.xml::app_name`. tests: `tests/MegaPDF.Core.Tests/StringCatalogueTests.cs::Catalogue`. nothing on Android runs in French at all — no locale-qualified instrumented run, unlike iOS. The catalogue is checked from the .NET side.

### Light and dark app chrome (`theme`)

- **Windows** — eng. coverage: engine only. reachable from `src/MegaPDF.App/MainWindow.xaml::ThemeChoice`. tests: `tests/MegaPDF.Core.Tests/DesignTokenParityTests.cs::Token`. token parity across platforms is asserted; no self-test state switches theme. The page-colour tint in `reading` is a different thing.
- **macOS** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/App.axaml.cs::RequestedThemeVariant`. tests: `tests/MegaPDF.Core.Tests/DesignTokenParityTests.cs::Token`. --self-test never switches theme; the --theme flag exists for by-hand screenshot poses only.
- **Linux** — eng. coverage: engine only. reachable from `src/MegaPDF.Avalonia/App.axaml.cs::RequestedThemeVariant`. tests: `tests/MegaPDF.Core.Tests/DesignTokenParityTests.cs::Token`. as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::colorScheme`. tests: `ios/MegaPDFUITests/ViewerBarContrastUITests.swift::class`. the one real check of chrome legibility on a dark bar on any platform; it is not a system-wide dark-mode pass.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ui/Brand.kt::darkColorScheme`. tests: `tests/MegaPDF.Core.Tests/DesignTokenParityTests.cs::Token`. no instrumented test runs in dark mode.

### Screen-reader names and announcements (`screen-reader`)

Every platform asserts the names a screen reader would read; no platform drives a real
screen reader in CI, and none can. NVDA, VoiceOver, TalkBack and Orca are by-hand passes.

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/DocumentView.Keyboard.cs::Announce`. tests: `.github/workflows/ci.yml::Run-Check "reading"`, `tests/MegaPDF.Core.Tests/PageReadingOrderTests.cs::ReadingOrder`. the reading state forces the screen-reader flag on and checks the floating bar stays put; the NVDA pass in tools/windows-qa is manual.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Platform/ScreenReader.cs::IsRunning`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckToolbarMenus`, `tests/MegaPDF.Core.Tests/PageReadingOrderTests.cs::ReadingOrder`. automation peers, names and children for the toolbar pickers, the find bar and the recents rows (#144).
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Platform/ScreenReader.cs::IsRunning`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckToolbarMenus`, `tests/MegaPDF.Core.Tests/PageReadingOrderTests.cs::ReadingOrder`. as macOS; Orca itself is by hand.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerView.swift::accessibilityLabel`. tests: `ios/MegaPDFUITests/RecentsAccessibilityUITests.swift::testEveryRecentRowAnnouncesWhereItsFileLives`. a real row announcement is asserted; the VoiceOver-running case is a mocked flag, not VoiceOver.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::contentDescription`. tests: `android/app/src/test/java/com/megapdf/android/ReadingModeMathTest.kt::class`, `android/app/src/test/java/com/megapdf/android/NoticeParagraphsTest.kt::class`. touch-exploration arithmetic and notice formatting; no instrumented test reads a content description back, and TalkBack is by hand.

### Keyboard-only operation (`keyboard-only`)

- **Windows** — UI. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.Keyboard.cs::InitializeWindowKeyboard`. tests: `.github/workflows/ci.yml::Run-Check "reading"`, `.github/workflows/ci.yml::Run-Check "pages"`. accelerators, the Escape ladder and tab order in reading mode and the pages pane. #2's Tab traversal fix was verified by hand with Narrator, not here.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Keyboard.cs::HandlePageKey`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckToolbarMenus`, `src/MegaPDF.Avalonia/Program.cs::CheckReadingMode`. real key presses injected into a headless window, including arming Redact from the keyboard.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Keyboard.cs::HandlePageKey`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckToolbarMenus`, `src/MegaPDF.Avalonia/Program.cs::CheckReadingMode`. as macOS.
- **iOS/iPadOS** — —. coverage: nothing. reachable from `ios/MegaPDF/KeyboardCommands.swift::keyboardShortcut`. iPad keyboard commands are declared and no test presses one, on either destination.
- **Android** — n/a. absent (deliberate). no hardware-keyboard commands are declared; touch and TalkBack are the input story here.

### About, the version, and the third-party notices (`about-notices`)

#571: Linux had both windows and no route to either. They had been there for releases. The
fix added the route and a self-test check for it, which is why Linux now has the strongest
cell in this row — and why `entry` for this feature has to be a menu row, never the window.

- **Windows** — UI+m. coverage: UI/VM. reachable from `src/MegaPDF.App/MainWindow.xaml::NoticesLink`. tests: `.github/workflows/ci.yml::Run-Check "about"`. by hand: `TESTING.md::**About**`. #590: `about` opens the real settings flyout and checks the version line against the running assembly's own version, loads the bundled notices text and checks it actually lists a component (PDFium), then opens the real notices dialog and reads it back through DialogGate.Current — the #571 shape (a window with nothing leading to it) checked directly rather than left to drift. The notices text itself is still checked for drift by the `notices` job.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/App.axaml.cs::NativeMenuItem(Strings.AboutMegaPDF)`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckAboutWindow`. the application menu is raised, About is clicked and the notices are read from what the bundle actually carries.
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/Views/MainWindow.Toolbar.cs::CommandEntry(Strings.AboutMegaPDF`. tests: `src/MegaPDF.Avalonia/Program.cs::CheckAboutWindow`, `src/MegaPDF.Avalonia/Program.cs::CheckLinuxAboutRoute`. CheckLinuxAboutRoute asserts More carries About and clicking it opens the window — the check that #571's absence would have failed. #571.
- **iOS/iPadOS** — —+m. coverage: nothing. reachable from `ios/MegaPDF/ViewerView.swift::About MegaPDF`. by hand: `docs/RELEASING.md::**About** says the version`. no test opens About or the notices on iOS.
- **Android** — eng. coverage: engine only. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.about_megapdf`. tests: `android/app/src/test/java/com/megapdf/android/NoticeParagraphsTest.kt::class`. the notices' paragraph formatting is unit-tested; no instrumented test opens the dialog.

### Protected, restricted, XFA and damaged documents (`special-documents`)

The documents that are not a happy path: permission bits that must disable tools, dynamic
XFA that must explain itself rather than pretend, signed documents, and files that are
simply broken. The corpus batteries are where these are met at scale (TESTING.md).

- **Windows** — eng. coverage: engine only. reachable from `src/MegaPDF.App/DocumentViewModel.cs::DocumentCapabilities`. tests: `tests/MegaPDF.Core.Tests/DocumentCapabilitiesTests.cs::Capabilities`, `tests/MegaPDF.Core.Tests/DynamicXfaTests.cs::Xfa`, `tests/MegaPDF.Core.Tests/SignatureDetectionTests.cs::Detect`, `tests/MegaPDF.Core.Tests/CorpusTests.cs::corpus`. the gating rules are tested; no self-test state opens a restricted or XFA document in the UI.
- **macOS** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::DocumentCapabilities`. tests: `tests/MegaPDF.Core.Tests/DocumentCapabilitiesTests.cs::Capabilities`, `tests/MegaPDF.Core.Tests/DynamicXfaTests.cs::Xfa`, `tests/MegaPDF.Core.Tests/SignatureDetectionTests.cs::Detect`. the self-test opens a dynamic-XFA document and a signed one and checks what the window offers (#457, #481).
- **Linux** — UI. coverage: UI/VM. reachable from `src/MegaPDF.Avalonia/ViewModels/DocumentViewModel.cs::DocumentCapabilities`. tests: `tests/MegaPDF.Core.Tests/DocumentCapabilitiesTests.cs::Capabilities`, `tests/MegaPDF.Core.Tests/DynamicXfaTests.cs::Xfa`, `tests/MegaPDF.Core.Tests/SignatureDetectionTests.cs::Detect`. as macOS.
- **iOS/iPadOS** — UI. coverage: UI/VM. reachable from `ios/MegaPDF/ViewerModel.swift::capabilities`. tests: `ios/MegaPDFTests/DynamicXfaTests.swift::class`, `ios/MegaPDFTests/DocumentCapabilitiesTests.swift::class`, `ios/MegaPDFTests/SignatureDetectionTests.swift::class`, `ios/MegaPDFTests/PagesFieldHierarchyTests.swift::class`. PdfEngineTests also feeds the engine invalid bytes.
- **Android** — UI. coverage: UI/VM. reachable from `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt::R.string.signed_overwrite_title`. tests: `android/app/src/androidTest/java/com/megapdf/android/DynamicXfaTest.kt::class`, `android/app/src/androidTest/java/com/megapdf/android/SignatureOverwriteTest.kt::class`, `android/engine/src/androidTest/java/com/megapdf/engine/DocumentFlagsTest.kt::class`. the signed-document overwrite question has an instrumented test here and nowhere else.

### The build a user installs (MSIX, App Store sandbox, .deb, Snap, Flatpak, tarball) (`packaged-install`)

#330 asked for the install matrix explicitly, and it is the dimension CI covers best on
Linux and least everywhere else: the Linux jobs install the real package in clean
containers and run the app out of it, while Windows, macOS, iOS and Android are all tested
as dev builds in CI and as real installs only by hand (docs/RELEASING.md §2.3).

- **Windows** — eng+m. coverage: engine only. reachable from `src/MegaPDF.App/Package.appxmanifest::Identity`. tests: `.github/workflows/ci.yml::Store packages, both architectures, and what is in them`. by hand: `docs/RELEASING.md::Add-AppxPackage`. the MSIX's identity, architecture and resource map are checked; nothing runs the installed MSIX in CI, which is where #401 was.
- **macOS** — eng+m. coverage: engine only. reachable from `src/MegaPDF.Avalonia/Platform/MacApplication.cs::NSApplication`. tests: `.github/workflows/macos-appstore.yml::--self-test`. by hand: `docs/RELEASING.md::tools/build-macos-app.sh`. the App Store build runs --self-test out of the bundle; the sandboxed, signed, installed app is only ever run by hand on the Mac mini.
- **Linux** — UI+m. coverage: UI/VM. reachable from `tools/linux/megapdf.desktop::Exec=megapdf %F`. tests: `tests/MegaPDF.Core.Tests/LinuxInstallTests.cs::Install`, `tests/MegaPDF.Core.Tests/UserDataPathsTests.cs::Paths`, `.github/workflows/ci.yml::Install the .deb and run the app out of it`. by hand: `docs/RELEASING.md::tools/linux/check-apt-repo.sh`. the strongest install coverage anywhere: .deb and Flatpak installed and run in CI, lintian and the Flathub linter, the tarball's install.sh as non-root, and --self-test inside both sandboxes. The APT matrix across five distributions is by hand.
- **iOS/iPadOS** — —+m. coverage: nothing. reachable from `ios/project.yml::PRODUCT_BUNDLE_IDENTIFIER`. by hand: `docs/RELEASING.md::xcodebuild test` then a simulator run`. CI builds and tests on the simulator only; nothing checks the shipped archive beyond the release workflow's own packaging.
- **Android** — —+m. coverage: nothing. reachable from `android/app/src/main/AndroidManifest.xml::android:name`. by hand: `docs/RELEASING.md::aapt dump badging`. CI assembles a debug APK and tests it; the release AAB is only verified by hand.

## Test files deliberately not on the map

The ratchet in `tools/qa/check-matrix.py` requires every test file, every Avalonia
`--self-test` section and every Windows self-test state to be cited by some cell.
These are the exceptions, each with its reason.

| artefact | why not |
|---|---|
| `android/app/src/androidTest/java/com/megapdf/android/TestPdfs.kt` | fixture builder for the instrumented suite, not a test |
| `android/app/src/androidTest/java/com/megapdf/android/ViewerHarness.kt` | Compose harness for the instrumented suite, not a test |
| `android/app/src/test/java/com/megapdf/android/ScaffoldTest.kt` | toolchain smoke test — proves Gradle and JUnit are wired, covers no feature |
| `android/engine/src/test/java/com/megapdf/engine/EngineScaffoldTest.kt` | toolchain smoke test, as ScaffoldTest |
| `ios/MegaPDFUITests/CaptureSimulatorSetupUITests.swift` | screenshot rig — mutates simulator Settings for store captures; excluded from the CI test lanes |
| `ios/MegaPDFUITests/DemoFlowUITests.swift` | preview-video choreography and the App Review walkthrough; it performs a flow but asserts no verdict, and CI excludes it |
| `ios/MegaPDFUITests/OpenWithVerificationUITests.swift` | excluded from CI (MegaPDF is not offered in the share sheet on the Mac mini runner), so no cell may count it as coverage — see open-external/ios |
| `ios/MegaPDFUITests/ShareVerificationUITests.swift` | excluded from CI for Files-app flakiness on the runner, as OpenWithVerificationUITests — see share/ios |
| `.github/workflows/ci.yml::Run-Check "more"` | the toolbar overflow state asserts nothing: it opens the flyout, returns true, and the flyout's own popup is not even in the captured PNG (#144). It proves the app does not crash opening More, which is not coverage of a feature |
| `ios/MegaPDFUITests/PageDragUITests.swift` | dragging a page tile onto another is a by-hand gate (#570): the drag is unreliable to synthesise on the simulator, so CI does not run this and no cell may count it |
