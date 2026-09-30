# Reading mode (#168) — plan for 2.2

**Status, 2026-09-29: tiers 1 and 2 shipped on all four platforms** (#505/#511 Mac and
Linux, #504/#510 Windows, #507/#513 Android, #506/#512 iPhone and iPad). This document is
kept as the pre-implementation plan and is not amended to match what shipped — where the
two differ, `SDD.md` §3.11 F10 describes reality and is the one to trust. Tier 3 (reflow)
remains unshipped, as §5 below recommended.

*Plan only, 2026-09-27, for Dave's "plan+". No code, no issues opened. Sources read: #168 and its comments (Dave's 2026-09-17 decision that #142 goes first; the triage note); `docs/adr-003-shared-engine-core.md`; `SDD.md` §1.4, §2.2, §3.9 F8, §6.1, §6.2 contracts 5–6; `core/megapdf_core.h` contracts 1–9; `core/megapdf_structure.cpp`; `core/megapdf_write_text.cpp`; `docs/design-tokens.md`; `docs/design-brief.md` ("each platform's own idiom"); `docs/localisation.md`; `docs/release-notes/2.1.1/release-notes-2.1.1.md`; #354's census, #358, #187, #174, #175, #172; the four apps' main views, recents stores and settings.*

## 1. What reading mode is, and is not

**Is.** A way of *looking at* the document you already have open: the page and nothing else, with a few preferences about how the page is shown, and — later — the same document's text rewrapped to the screen. Same document handle, same tab, same undo stack, same recovery journal. Entering it changes nothing in the document and nothing on disk; the unsaved-changes dot stays if it was there (the same posture as Save As Markdown in 2.1.1: "the PDF is untouched").

**Is not.** Not an edit, not an export, not a second document, not a mode in the SDD §2.2 sense ("one window, one mode" — that rule is about *what a click means on the page*; here clicks on the page stop editing altogether, which is the opposite of a tool palette). Not a presentation mode (no auto-advance). Not OCR (#175): a scanned page is a picture in every tier. Not page assembly (#174).

**The invariant on every platform: leaving reading mode returns to the page view at the same place.** Same tab, same page, same zoom, same scroll offset, no re-render. For tier 1 and 2 this is free because the page host never goes away — only the chrome around it does. For reflow (tier 3) it means the top visible block maps back to `megapdf_block.page` + `bounds` and the page view scrolls there; entering reflow does the inverse (current page → its first block).

Naming (decision 3 on #168, needs Dave): recommend **Reading mode** / *Mode lecture* — the glossary (`docs/localisation-glossary.md`) has no entry yet; one line, fr-CA, fr-FR derived by `tools/gen_strings.py`. "Read Mode" is Acrobat's capitalised proper noun; "Reading view" is Word's; "Reading mode" is what people say.

**SDD gate.** As with Find (§3.6), Printing (§3.5), Redact (§3.8) and Text out (§3.9), a scope amendment lands *first*: **§3.10 F9 — Reading mode**, with the three tiers, the non-goals above, and a line in §1.4. Small, and it is where decisions 1–3 get written down.

## 2. The three tiers

### Tier 1 — chrome-free page view (cheap, per platform, no engine work)

The page host stays; the chrome around it hides. What "chrome" is on each platform is already named and separable:

| Platform | Hides | Stays |
|---|---|---|
| Windows | `MainWindow.xaml` `Toolbar` (:81), `BusyStrip` (:63), the `TabView` tab strip (:317); `DocumentView.xaml` `FindBar` (:207) | `DocumentView.xaml` `PagesScroll` (:43); `PageIndicatorPill` (:188) becomes the floating bar |
| Mac / Linux | `MainWindow.axaml` `ToolbarHost` (:64), `DocumentTabStrip` (:360), `BusyStrip` (:386), `FindBarHost` (:431), `StatusBarHost` (:462) | `PageScroller` (:481); the menu bar (macOS never hides it in a window; full screen does) |
| iPhone / iPad | `ViewerView.swift` `.toolbar` (:146–275: the top bar with More, the bottom tool row) | the page `ScrollView` |
| Android | `ViewerScreen.kt` `TopAppBar` (:530) and `BottomAppBar` (:664) inside the `Scaffold` (:514) | the `LazyColumn` of pages |

Behaviour, all platforms:

- **Floating bar**, bottom centre, translucent (`radius.surface` per platform, design-tokens §3): previous / next page, page number (tap → go to page), fit width, fit page, −, +, **Exit**. Appears on entry and on mouse movement / tap / keyboard focus; fades after ~2 s idle; **never fades while it has keyboard focus or a screen reader is running** (`AutomationPeer.ListenerExists()`, `UIAccessibility.isVoiceOverRunning`, `AccessibilityManager.isTouchExplorationEnabled`). Respects reduced-motion (no fade animation, just show/hide).
- **Editing is off.** The page's tap/click dispatch is suppressed, not rerouted: desktop `DocumentView` pointer handlers and the phones' `onPageTap` (`ViewerScreen.kt:908–916`) / `model.onPageTapped` (`ViewerView.swift:742–746`) check `IsReadingMode` first. Armed tools (sign, whiteout, redact) disarm on entry and do not re-arm on exit. Undo/redo shortcuts stay live (they act on the document, not the page) — cheaper than deciding otherwise, and nothing can be added to undo while editing is off.
- **Exit** returns every hidden host, restores focus to the toolbar, announces "Reading mode off" (mechanisms exist: `DocumentView.Keyboard.cs:271` `RaiseNotificationEvent`, `ViewerModel.swift:330` `UIAccessibility.post`, `ViewerScreen.kt:1030` `liveRegion`).
- **Find** still works from the keyboard (Ctrl+F / ⌘F / the bar's magnifier on phones): the find bar is the one piece of chrome that may appear over the reading view, and closes back into it.
- **Full screen** is a second, separate level on desktops (issue's "two levels"): Windows `AppWindow.SetPresenter(FullScreenPresenter)` (today only `OverlappedPresenter` is used, `MainWindow.Toolbar.cs:97`); Mac the standard green button / ⌃⌘F (`WindowState.FullScreen`); Linux F11. **Esc steps back one level**: full screen → reading mode → normal, *after* the find bar and an open flyout have had their Esc. Full screen without reading mode is not offered (that was Acrobat's confusion).
- **Tabs (#348).** Reading mode is a *window* state (the chrome is the window's), not a document state. Ctrl+Tab / ⌃Tab still switch tabs inside it; the floating bar shows the file name when there is more than one tab. Consequence for the issue's "remember per document": with tabs, a per-document flag would flip the whole window's chrome when a tab is selected. **Recommend dropping per-document memory** in favour of one setting, *Open documents in reading mode* (off by default), plus remembering the last state per window session. Decision for Dave; it removes four recents-store migrations (`RecentFiles.cs`, `RecentsStore.swift`, `RecentFilesStore.kt`).

### Tier 2 — reading preferences for the page view (small engine change + per-platform UI)

Kept to what PDFium can do at render time and what four page hosts already have:

- **Zoom presets** — fit width and fit page exist on the desktops (`DocumentViewModel.cs:550/559`, Avalonia `:3018/3026`, View menu `MainWindow.MenuBar.cs:212–216`); the phones have width-relative zoom (fit width = 1.0) and double-tap 2×. Tier 2 adds *fit page* to the phones and puts the presets on the floating bar. No engine work.
- **Page colours: Normal / Sepia / Night**, applied at render time, never written into the file. **Where: the core**, as contract 7 flags (`megapdf_render`, `core/megapdf_core.h:702`; ADR-003 decision 4 puts render policy in the core, and #115 is what happened when the phones diverged). Mechanism: a per-pixel post-pass on the caller's buffer after `FPDF_FFLDraw` (`core/megapdf_core.cpp:3418–3424`): sepia = multiply toward a warm paper white (white → ≈`#F4ECD8`, black stays black); night = invert luminance, keep hue (so brand blue stays blue-ish, black text on white becomes light on `#1A1A1A`-ish). Cost: one linear pass over ≤32 MP, well under a render.
  - **Trade-off, images.** The issue asks for "images left alone". A post-pass inverts them too (photos read as negatives; scans invert nicely, which is what people want at night). The alternative, `FPDF_COLORSCHEME` + `FPDF_RenderPageBitmapWithColorScheme_Start` (`libs/pdfium/android/include/fpdfview.h:849`, `fpdf_progressive.h:79`), recolours text and paths and leaves images alone — but it flattens every path to one fill colour (diagrams lose their colours), it is the progressive API, and `FPDF_FFLDraw` has no colour-scheme variant, so form fields would draw in daylight colours over a night page. **Recommend the post-pass for 2.2**, inverting everything (Edge's and Chrome's PDF dark mode do the same), and note "images left alone" as a follow-up if testers ask.
  - The gutter around pages and the floating bar follow the choice per platform (that is chrome, in each app's `Brand.*` token file per design-tokens §5).
  - Render caches must key on the tint (`CappedRenderCache.cs` on Windows, the Avalonia `Rendering/` cache, `RenderWindow.kt`, the iOS page-image dictionary): switching re-renders the visible window only.
- **Out of tier 2 for 2.2:** two-page spread and cover-page option (a layout change in four page hosts, and the iPad has no layout of its own until #172), auto-scroll (the issue already says low priority), single-page vs continuous (all four are continuous today; single-page is a new host).
- **Storage.** App-level, not per document. Desktops: `src/MegaPDF.Core/Services/AppSettings.cs` (`settings.json` via `UserDataPaths`, shared by WinUI and Avalonia) grows `PageColours` (`""`/`Sepia`/`Night`) and `OpenInReadingMode`. Phones have no preferences store today (recents and signatures are JSON files; no `UserDefaults`/`DataStore` use anywhere): use the idiom — iOS `@AppStorage` (UserDefaults), Android `DataStore<Preferences>` — rather than a fifth JSON file, since these are three scalars a Settings screen binds to. Text size / line spacing / margins are reflow preferences and wait for tier 3.

### Tier 3 — reflow (the structure engine's blocks re-laid as text)

The phone feature, on-device only. Input is **contract 9 as it stands** (`megapdf_structure_load`, `core/megapdf_core.h:1175`): reading-ordered `HEADING`/`PARAGRAPH`/`LIST_ITEM`/`FIGURE`/`PAGE_IMAGE`/`FIELD` blocks with canonical text (§6.2 contract 6), spans with bold/italic/mono, `font_size`, `size_ratio`, `bounds` and `page`, and a 0–100 per-page confidence. `megapdf_write_text.cpp` already proves a consumer can render blocks "inferring nothing itself"; the reflow view is the second such consumer, and a heading in the reflow is a `#` in the Markdown by construction.

What each block becomes:

| Block | Reflow |
|---|---|
| HEADING n | a heading at level n, `size_ratio`-scaled |
| PARAGRAPH (+ `continues`) | a paragraph; `continues` joins with the previous without a break |
| LIST_ITEM | marker + text, indented by `level` |
| FIGURE | the image, via `megapdf_render_image(document, page, object_index)` (contract 6, `:641`), alt text when tagged (#358); tap → original page |
| TABLE_ROW (after #358) | a table; until then an untagged table arrives as row-by-row paragraphs, which the confidence score already penalises (§3.9 item 2) |
| PAGE_IMAGE (scan) | the page itself, inline, captioned *Page N has no text layer* — never dropped; OCR is #175 and out |
| FIELD | see forms below |
| FURNITURE | not requested (default flags) |

- **Forms.** Reading mode on a form is odd, and Acrobat's answer (refuse the page) is honest. Recommend: a page carrying FIELD blocks or any widget annotation is shown in the reflow **as a page image** with a one-line note, tappable to the page view; its filled values are still read out (the `**name:** value` shape the Markdown writer uses) so a screen reader gets them. No filling in reflow, ever.
- **Confidence gate.** The reflow control is offered only when the document's text pages meet a threshold on `megapdf_structure_page_confidence` (§3.9 item 4 leaves the number to the corpus; the spike picks it). Below it, the control is disabled with *This document's layout can't be reflowed reliably*.
- **Fonts, "the document's where possible".** Spans carry no font name today (`megapdf_span`, `:1153`; only `megapdf_text_run` has `MEGAPDF_TEXT_RUN_FONT`). Step one is name-and-style matching to installed faces; embedded-font extraction (`FPDFFont_GetFontData`, `fpdf_edit.h:1593`, plus runtime registration on each platform) is possible but is its own risk (subset fonts, symbolic fonts, licensing flags) and is not in the spike.
- **Preferences** (text size, line spacing, margins, the tier-2 colour) are the platform text stack's business and stored beside tier 2's settings.
- **Find in reflow** is a string search over block text on the platform, highlighting spans; no engine call.
- **Outline** from HEADING blocks (bookmarks via `fpdf_doc.h` are a later nicety).

## 3. Engine contract needed

**No `megapdf_reflow_*`.** Reuse contract 9 plus two small additions and one contract-7 flag, all in the "frozen struct, grown by new fields and enum values, never new parameters" shape the header already uses (`:1204`):

1. **Contract 7:** `MEGAPDF_RENDER_SEPIA = 2`, `MEGAPDF_RENDER_NIGHT = 4` on `megapdf_render` (post-pass as above). Tier 2. Tests: a fixture rendered three ways, pixel assertions on a text pixel, a white pixel and an image pixel; `text_runs.txt`-style goldens are not needed.
2. **Contract 7:** `megapdf_render_clip(page, const megapdf_rect* crop_space_rect, buffer, width, height, stride, flags)` — render one region of a page (PDFium clips natively via negative `start_x/start_y` and an oversize `size_x/size_y`). Needed so a block or a form region can be shown "as the page" inside the reflow without rasterising the whole page. Tier 3 spike.
3. **Contract 9:** `megapdf_block_span_font(s, block, span, out, capacity)` — the span's family name (and `FPDFFont_GetWeight`/`GetItalicAngle`/`GetFlags` folded into new `MEGAPDF_SPAN_*` bits only if the spike shows name+style matching is not enough). Count-then-fill like every other string accessor. Tier 3 spike.

**Where the layout runs — recommendation: per-platform native text views fed by blocks, not a C++ layout step.**

| | Core layout (C++ returns positioned glyph runs) | Platform text views (blocks → `RichTextBlock` / Avalonia `TextBlock` inlines / SwiftUI `Text(AttributedString)` / Compose `Text(AnnotatedString)`) |
|---|---|---|
| Implementations | one | four, but each is ~a view over a list |
| Bidi, CJK line breaking, hyphenation, kerning | ours to write (PDFium exposes no shaper; no HarfBuzz/ICU in the core) | the platform's, free and correct |
| Dynamic type, screen readers, selection, copy | ours to re-invent | free: heading levels, live text, VoiceOver/TalkBack navigation by heading |
| Testability | golden layouts per fixture, byte-exact on three OSes | the *blocks* are already golden-tested (`core/tests/expected/structure`); layout is the platform's problem |
| What ADR-003 says | policy is shared | ADR-003 keeps "all UI" per platform, and text layout is UI |

The core's job ends where it ends today: blocks in order with canonical text. That is the same split #386 used for Save As Markdown: one writer in the core, four Save panels in the apps.

## 4. Per-platform UI, in its idiom

Same goal, four dialects (design-brief §4; how #348 tabs and #378 Share were done: WinUI `TabView` + More menu, Avalonia `NativeMenu` File/Window menus, SwiftUI `.toolbar` Menu, Compose `DropdownMenu`).

**Windows (WinUI 3).** Entry: **Reading mode** in the zoom `DropDownButton` flyout (`MainWindow.xaml` `ZoomMenu`, beside Fit width / Fit page) and in More; accelerator **Ctrl+H** on `RootGrid.KeyboardAccelerators` (`:18–25`), **F11** for full screen inside it. Chrome collapses (`Toolbar`, `BusyStrip`, tab strip); the existing `PageIndicatorPill` (`DocumentView.xaml:188`) grows into the floating bar (Fluent acrylic, `radius.control` 4). Exit: the bar's Exit, Ctrl+H again, Esc. Preferences: `AppSettings` (`settings.json`); the ⚙ flyout (`MainWindow.xaml.cs:419–439` where Theme lives) gains *Page colours* and *Open documents in reading mode*. Announce via `RaiseNotificationEvent`. Toolbar preset from #187 reuses the same command list as the floating bar when it lands.

**Mac and Linux (Avalonia).** Entry: **View › Reading Mode ⇧⌘R** (`MainWindow.MenuBar.cs` `view` menu, `:205`; ⌘H is Hide, ⇧⌘R is Safari's Reader — the right association), **View › Enter Full Screen ⌃⌘F** as the standard macOS item; Linux gets the same items in the in-window menu with **Ctrl+H** / **F11**. Chrome: `ToolbarHost`, `DocumentTabStrip`, `BusyStrip`, `FindBarHost`, `StatusBarHost` collapse; a floating pill (design-tokens §6 already describes "a floating page/zoom pill" for the Mac) with `radius.control` 6. Exit: menu item (checked state via `_menuBarChecked`), ⇧⌘R, Esc. Preferences: the same `AppSettings` class, `settings.json` under the state directory (`ShellViewModel.cs:53`); the Options window (⌘,) gains the two rows. Linux flavour identical minus the native menu bar.

**iPhone and iPad (SwiftUI).** Entry: **Reading mode** in the More menu (`ViewerView.swift:170–235`, where Share and Export as Markdown sit); on iPad, a toolbar button once #172 gives the iPad a toolbar. Inside it, **a single tap on the page toggles the floating bar** (decision 2 on #168: tap-to-hide only *inside* reading mode, so the default tap on a field or box is untouched); the double-tap zoom stays. `.toolbar(.hidden)` on the navigation and bottom bars; the floating bar is a `.ultraThinMaterial` capsule (`radius.control` 10) with `accessibilityAddTraits`, and VoiceOver users get the bar pinned. Exit: Exit on the bar, or the swipe-back edge gesture (which asks nothing, since nothing is unsaved by this). Preferences: `@AppStorage` keys read by a *Reading* section in the existing settings sheet. Announce via `UIAccessibility.post`.

**Android (Compose).** Entry: **Reading mode** `DropdownMenuItem` in the More menu (`ViewerScreen.kt:571–660`, beside Share / Export as Markdown). Inside it the `Scaffold`'s `TopAppBar` and `BottomAppBar` are not composed; tap on the page toggles a Material 3 floating bar (`radius.control` 8, tonal surface) — `onPageTap` (`:908`) returns early when `readingMode` is on. **Back** exits reading mode (one `BackHandler` level), then the document as today. Immersive system bars (`WindowInsetsController`) only in reading mode. Preferences: `DataStore<Preferences>` read by a *Reading* group in Settings. Announce via a `liveRegion` on the bar. Instrumentation coverage is thin (#346); a Compose UI test for enter/exit/tap is the minimum.

**Common test list** (from #168): enter/leave keeps page, zoom and scroll; bar fades and returns; Esc/back order; screen reader announce and no focus trap; captures en / fr-CA / fr-FR, light / dark, per `docs/qa/*-screen-inventory.md` and `tools/capture-gate`; the 2.5 GB file in continuous reading mode (tier 1 does not touch the page host, so this is a regression check, not new risk).

## 5. The 2.2 deliverable

**Ship tier 1 + tier 2 on all four platforms in 2.2. Run reflow as a timeboxed spike with a numeric exit; full reflow is a later version.**

Why: tier 1 is fully specified and needs no engine work; tier 2's engine part is a render flag with a pixel test; both are what Acrobat users actually ask for (the issue's research: "no plain hide-the-chrome mode separate from full screen"). Reflow is the largest piece by far, depends on #358 for tables and better order, and has no UX evidence yet that contract 9's blocks *read well* on a phone as opposed to diffing well against pdftotext.

**Reflow spike (2 weeks, one engineer, Android prototype behind a debug flag, not shipped).** Exit criteria, measured on the #354 corpus (4,158 documents, 22,180 pages; 4,400 tagged, 4,663 textless) with `megapdf_structure_check` and `tools/stress/structure-battery.sh --reference`:

1. **Order:** ≥ 80 % of documents with ≥ 3 text pages have *every* text page at Kendall τ ≥ 0.9 against `pdftotext -layout` (measure 3, `tools/structure-check/structure_check.cpp:25`; today's per-page median is 0.981, so the question is the tail, not the middle). Reported alongside: the confidence threshold at which the ≥ 0.9 pages are ≥ 95 % of the eligible set — that number becomes the gate in §2 tier 3.
2. **Reads well:** the 30-document hand-check sample from #357 (10 reports/letters, 10 forms, 10 mixed) read in the prototype by someone who is not the author; ≥ 24 "reads in order without hunting"; every failure gets a note naming a block kind or a heuristic constant.
3. **Performance:** `megapdf_structure_load` over a 500-page text document on a Pixel-class phone: wall time and peak RSS recorded; if > 2 s or > 150 MB, the spike also delivers a chunking design (contract 9 is range-based, but `AssignHeadingLevels`/`AssignContinuation`/`ComputeBodySize` are range-wide — `megapdf_structure.cpp:1784, 1808–1810` — so chunks need a shared body size and a page of overlap).
4. **Scope honesty:** counts of pages that would be shown as page images (forms, scans, low confidence) as a share of the corpus, so the 2.3 decision knows how often the feature will decline.

Spike output: numbers in a comment on the reflow issue, the two engine additions (§3 items 2–3) merged if they are clean regardless, and a go/no-go for 2.3.

## 6. Issues to open (none opened here)

| # | Title | Size | Depends on |
|---|---|---|---|
| A | SDD §3.10 F9 Reading mode scope amendment; glossary entry *Mode lecture*; decisions 1–3 recorded | S | Dave |
| B1 | Windows: reading mode (tier 1) — Ctrl+H, F11, floating bar, editing off, a11y, captures | M | A |
| B2 | Mac and Linux: reading mode (tier 1) — View menu ⇧⌘R / Ctrl+H, ⌃⌘F / F11, floating pill | M | A |
| B3 | iPhone and iPad: reading mode (tier 1) — More menu entry, tap toggles bar, edge-swipe exit | M | A; #172 for an iPad-specific placement |
| B4 | Android: reading mode (tier 1) — More menu entry, tap toggles bar, Back exits, immersive bars | M | A; #346 for a UI test |
| C0 | Core: `MEGAPDF_RENDER_SEPIA` / `MEGAPDF_RENDER_NIGHT` on contract 7, pixel tests | S | — |
| C1–C4 | Per platform: page colours + zoom presets on the bar + *Open in reading mode* setting, stored in `AppSettings` / `@AppStorage` / `DataStore`; render caches keyed on tint | S each (C1/C2 share `AppSettings`) | B*, C0 |
| D | Reflow spike: `megapdf_block_span_font`, `megapdf_render_clip`, Android prototype, corpus measures 1–4 above | L, 2-week timebox | #353/#354 (done); better with #358 but not blocked by it |
| E | #187 Reading toolbar preset: reuse the floating bar's command list | — (note on #187) | B* |

Dependencies outward: **#358** (tagged path) improves reflow input — tables, alt text, real order on the 20 % of tagged pages — and should land before any reflow *ship*, not before the spike; the spike should run `--heuristic` and default both if #358 lands mid-way. **#174** page tools are independent (they rewrite pages; reading mode reads them). **#175** OCR out. **#187** designed with tier 1 (the comment on #168 already says so). **#172** iPad layout gates only where the iPad entry point sits.

## 7. Risks

- **Fonts (tier 3).** Family names on spans are new; subset-embedded and symbolic fonts (`FPDFFont_GetFlags`) will not match anything installed — fall back to the platform serif/sans by span style, and treat a symbolic-font span as a figure (render its bounds via `megapdf_render_clip`) rather than show tofu. Embedded-font extraction is deferred with its licensing-flag question.
- **RTL and CJK (tier 3).** `BuildWords`/`BuildLines`/XY-cut in `megapdf_structure.cpp` are left-to-right, horizontal assumptions; vertical CJK and Arabic pages will score badly on τ and should be *excluded from eligibility* by script detection in the spike and measured separately, not averaged in. Platform text views display bidi correctly once order is right, which is the part we do not yet have. Night/sepia (tier 2) have no script risk.
- **Performance on 500-page documents.** Tier 1/2: none new (the page host is untouched; the tint is one pass per rendered page; #147–#151 paging holds). Tier 3: one whole-range `megapdf_structure_load` under the core mutex — must run off the UI thread with a `megapdf_cancel` (the #145 pattern) and be measured (spike criterion 3); memory for 500 pages of blocks and strings is unmeasured today.
- **Accessibility.** Reading mode should be *the* screen-reader-friendly view: tier 1 must never trap focus (hidden chrome must be removed from the tab order, not just made invisible — `Visibility.Collapsed`, `IsVisible=false`, not composed, `.toolbar(.hidden)`), and the floating bar never fades with a reader on. Tier 3 gets heading levels for free (`AutomationProperties.HeadingLevel`, `.accessibilityHeading`, `semantics { heading() }`), which is the first time a MegaPDF document is navigable by heading — worth saying in the release notes.
- **Night mode inverts photos.** The trade-off chosen in §2; state it in the ⚙/Settings copy ("Night inverts the page, pictures included"), and keep `FPDF_COLORSCHEME` as the documented alternative.
- **Esc and Back ordering.** Esc already closes the find bar and cancels an armed tool; Back on Android already asks about unsaved changes. The order (find bar → full screen → reading mode → the existing behaviour) must be tested, or a user loses a tool state or gets the Save dialog when they meant "show the toolbar".
- **Tabs make "remember per document" ambiguous** (§2 tier 1); the recommendation drops it. If Dave wants it kept, it is four recents-store fields and a rule for which tab wins.
- **Forms in reflow.** A form page shown as a picture is honest but will be read as "reflow doesn't work" on the persona's documents (forms are most of what Pat opens). The confidence gate copy must say *why*; the release notes must say forms show as pages.
- **Four captures × three locales × two themes** for every tier is the usual cost (`tools/capture-gate`); budget it in B* and C*.

## Files the implementation touches

- `core/megapdf_core.h` — contract 7 (`megapdf_render`, :668–703) gains the tint flags and `megapdf_render_clip`; contract 9 (:1084–1200) gains `megapdf_block_span_font`; the frozen-struct growth rule at :1204
- `core/megapdf_core.cpp` — `megapdf_render` at :3403–3427 is where the sepia/night post-pass goes, after `FPDF_FFLDraw`
- `core/megapdf_structure.cpp` — the reflow input; `BuildStructure` (:1722–1812) and the range-wide passes at :1784, :1808–1810 that constrain chunking
- `src/MegaPDF.App/MainWindow.xaml` — Windows chrome to hide (`Toolbar` :81, `BusyStrip` :63, `TabView` :317) and the accelerator grid (:18–25); with `DocumentView.xaml` (`PagesScroll` :43, `PageIndicatorPill` :188, `FindBar` :207)
- `src/MegaPDF.Avalonia/Views/MainWindow.MenuBar.cs` — the View menu (:205–224) for ⇧⌘R and full screen; with `MainWindow.axaml` hosts (:64, :360, :386, :431, :462, :481)
- `ios/MegaPDF/ViewerView.swift` — More menu (:170–235) for the entry, tap dispatch (:737–746) to suppress
- `android/app/src/main/java/com/megapdf/android/ViewerScreen.kt` — `Scaffold`/bars (:514–664), More menu (:571), `onPageTap` (:908–916)
- `src/MegaPDF.Core/Services/AppSettings.cs` — the desktop settings file both WinUI and Avalonia share; the new `PageColours` and `OpenInReadingMode` fields
