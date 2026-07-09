# MegaPDF — Software Design Document

| | |
|---|---|
| **Document version** | 1.1 |
| **Date** | 2026-07-08 |
| **Status** | Open questions resolved (Appendix B); ready for planning |
| **Product** | MegaPDF — a lightweight PDF editor for Windows 11 |

---

## 1. Executive Summary & Product Vision

### 1.1 The problem

Administrative professionals handle PDFs all day: forms to fill, boxes to check, documents to sign and send back. The dominant tool for this work, Adobe Acrobat, is engineered for print professionals and legal departments — it front-loads hundreds of features the typical office user never touches, hides the four operations they perform daily behind ribbons and panels, and carries a subscription price and install footprint to match.

The result is a familiar office scene: a two-minute task ("check these three boxes, sign, save") turns into ten minutes of hunting through menus, or worse, a print-sign-scan cycle.

### 1.2 The product

**MegaPDF** is a native Windows 11 desktop application that does four things exceptionally well and deliberately nothing else:

1. **Edit text** — click any text in the document and type, like editing a Word file.
2. **Check boxes** — click an empty square and it becomes a checked box.
3. **Apply signatures** — keep a small personal library of signature images; drag one onto the page.
4. **Save** — Save overwrites, Save As creates a copy. No export wizards, no "flatten" dialogs.

MegaPDF is **free and open source** (Apache-2.0). There is no monetization: no subscription, no upsell, no account. This is both a product stance (the persona's core frustration with Acrobat is being sold to) and a technical constraint — every dependency must be license-compatible with a permissive open-source release (§4.3).

### 1.3 Product principles

These principles override feature requests. Every design decision in this document traces back to one of them.

- **P1 — Zero learning curve.** A first-time user must complete a fill-check-sign-save task with no training, no tutorial, and no documentation. If a feature needs explaining, the design is wrong.
- **P2 — The document is the interface.** Users interact with the page itself (click text, click boxes, drop signatures), not with tool palettes that change what a click means.
- **P3 — Never lose work, never surprise.** Every destructive or irreversible action is either undoable or explicitly confirmed. Save behaves exactly like Save in every other Windows application.
- **P4 — Small and fast.** Sub-2-second cold start on mid-range hardware, installer under 60 MB, idle memory under 200 MB for typical documents.
- **P5 — It looks like Windows 11.** Fluent Design throughout — Mica material, rounded corners, Segoe UI Variable, system light/dark theme. The app should feel like it shipped with the OS.

### 1.4 Explicit non-goals (v1)

Stating what MegaPDF will *not* do is as important as what it will. Out of scope for v1:

- PDF creation from scratch, page assembly/reordering, merging/splitting
- OCR of scanned documents
- Cryptographic digital signatures (PKI / certificate-based signing) — MegaPDF signatures are *graphic* signatures, which is what the target user means by "signing"
- Redaction, commenting/review workflows, form *authoring*
- Cloud storage integration beyond what the Windows file picker already provides (OneDrive etc. work transparently through the file system)
- macOS/Linux/mobile versions

---

## 2. User Persona & UI/UX Philosophy

### 2.1 Primary persona: "Pat, the office administrator"

| Attribute | Detail |
|---|---|
| Role | Secretary / administrative assistant at a small-to-mid-size business |
| Age / tech comfort | 30–65; fluent in Outlook, Word, Excel; does **not** self-identify as "technical" |
| PDF tasks | Fills intake forms, checks boxes on agreements, applies the boss's (and her own) signature, fixes a typo in a date or name, saves and emails back |
| Frequency | 5–20 PDFs per day |
| Current pain | Acrobat's mode-switching ("Edit PDF" vs "Fill & Sign" vs "Comment"), accidental subscription upsells, slow launch, fear of "breaking" the document |
| Success metric | "I opened it, fixed it, saved it, and nothing weird happened." |

**Anti-persona:** graphic designers, prepress operators, legal e-discovery teams. If a design choice would please them at Pat's expense, we choose Pat.

### 2.2 UI/UX philosophy

**One window, one mode.** MegaPDF has no editing "modes." The cursor communicates capability contextually: hovering text shows a text-edit cursor, hovering a checkbox shows a pointer with a check hint, empty page areas show the default arrow. What you click determines what happens — the user never has to declare intent in advance (P2).

**Command surface: a single slim toolbar.** No ribbon, no menu bar, no dockable panels. The toolbar contains, left to right:

```
┌────────────────────────────────────────────────────────────────────────┐
│  [Open]  [Save]  [Save As]   |   [Undo] [Redo]   |   [✒ Signatures]    │
│                                        [ − 100% + ]  [Page 3 of 12]  ⚙ │
└────────────────────────────────────────────────────────────────────────┘
```

- Seven primary controls total. Every control is labeled with **icon + text** (icons alone fail discoverability for this persona).
- `⚙` opens a minimal settings flyout (theme, default zoom, "reopen last file" toggle). Settings that most users never need do not earn toolbar space.
- The zoom and page indicators live at the right edge, matching Edge's built-in PDF viewer — a UI Pat already knows.

**Discoverability mechanics:**

- **First-run empty state:** a friendly drop zone — "Drag a PDF here, or click Open" — plus a recent-files list on subsequent launches. No feature tours or first-run wizards. The only launch chrome is the branded splash screen, shown for a few seconds while the app initializes (stakeholder decision, 2026-07-08).
- **Hover affordances:** editable text regions get a faint dotted underline on hover; checkboxes get a light accent-color glow. The document itself teaches the user what is clickable (P1, P2).
- **Plain-language everywhere.** "Save a copy…" not "Export." "Your signatures" not "Stamp library." Error messages say what happened and what to do next, never show error codes as the primary content.

**Forgiveness mechanics (P3):**

- Unlimited undo/redo within a session (`Ctrl+Z` / `Ctrl+Y`), covering text edits, checkbox toggles, and signature placement/removal.
- Unsaved-changes indicator: a dot on the Save button and in the title bar (`● contract.pdf`), matching Word/VS Code convention.
- Closing with unsaved changes prompts with the standard Windows three-button dialog: **Save / Don't Save / Cancel**.
- Autosaved session recovery: if the app or OS crashes, the next launch offers to restore unsaved changes.

**Accessibility (required, not optional):**

- Full keyboard operability: `Tab` traverses interactive PDF elements (text regions, checkboxes); `Enter`/`Space` activates; arrow keys nudge a selected signature.
- UI Automation exposure for all controls and document interaction targets (Narrator-compatible).
- Respects system text scaling, high-contrast themes, and reduced-motion settings.
- All hit targets ≥ 40×40 epx per Windows 11 guidance.

---

## 3. Core Feature Specifications

### 3.1 F1 — Simple Text Edits

**User story:** *"There's a typo in the client's name. I click the name, fix it, and save."*

#### Behavior

1. Hovering over text renders a subtle dotted underline beneath the hovered text line (150 ms fade-in, so normal reading isn't visually noisy).
2. A single click places a caret inside the text run and draws a light rounded-rectangle boundary around the editable region (the text line, extended to the paragraph block where the engine can detect it).
3. The user types. Text reflows within the region's original bounding box. Font family, size, and color are inherited from the clicked run — **no formatting UI appears.** This is deliberate (P1): Pat is fixing content, not designing documents.
4. Clicking anywhere outside the region, or pressing `Esc`, commits the edit. `Ctrl+Z` reverts it.

There is deliberately **no formatting toolbar** in v1 — not even a floating one. Inherited formatting covers the "fix a typo / change a date" job completely, and every control we add is a control Pat has to ignore.

#### The two kinds of "text" — handled invisibly

PDFs contain two very different kinds of editable text, and MegaPDF must hide the distinction:

| Case | Detection | Editing strategy |
|---|---|---|
| **AcroForm text field** (a real fillable form) | Field annotation under the click point | Activate the field's native editing; value stored in the form dictionary. This is the easy, reliable path and covers most of Pat's documents. |
| **Body text** (content-stream text) | Text run under the click point | In-place content-stream edit (below). |

For body text, the editing strategy is tiered by font availability:

1. **Fully embedded font with needed glyphs** → edit the text run directly in the content stream; re-layout the affected line(s).
2. **Subset-embedded font missing needed glyphs** → substitute the closest available system font *for the entire edited run* (never mid-word), matching metrics as closely as possible. A one-line, non-modal notification appears: *"This document's font isn't available, so a similar one was used."* — dismissible, never blocking.
3. **Non-extractable / rasterized text (scanned page)** → the click does nothing silently harmful. Instead, a small inline hint appears: *"This text is part of a scanned image and can't be edited."* v1 offers no workaround (OCR is a non-goal); honesty beats a broken feature.

#### Acceptance criteria

- Clicking a form field and typing a value works on 100% of standard AcroForm documents.
- A single-word replacement in embedded-font body text produces no visible layout shift elsewhere on the page.
- Undo restores byte-identical prior state of the affected object.
- Edit latency (click → caret visible) < 150 ms on the reference machine.

### 3.2 F2 — Checkbox Toggling

**User story:** *"The agreement has six checkboxes. I click each one; a check appears. Clicking again removes it."*

#### Behavior

A single click toggles. No tool selection, no stamp-picking dialog. Hover shows a light accent glow plus a ghost preview of the current mark style so the user knows before clicking what will happen.

#### The two kinds of "checkbox" — handled invisibly

| Case | Detection | Toggle strategy |
|---|---|---|
| **AcroForm checkbox / radio field** | Widget annotation under the click | Toggle the field value (`/Off` ↔ its "on" state); the field's own appearance stream renders the check. Radio-group semantics respected (checking one unchecks siblings). |
| **Drawn square** (a rectangle in the content stream or a ☐ glyph — extremely common in real-world forms) | Heuristic detector: small (6–24 pt) axis-aligned stroked square, or box-drawing/ballot glyph, within a tolerance of the click point | Place a vector checkmark **annotation** (stamp) sized to ~80% of the detected square, centered. Clicking the mark again removes the annotation. |

The heuristic detector for drawn squares is a differentiating feature — competitors force the user into a "stamp tool" for these. Detection runs lazily on the visible page and caches results per page.

**Miss behavior:** if the user clicks something that is neither text nor a detectable box, nothing happens — no error dialog. If the user clicks *near* a detected box (within 12 epx), the click snaps to it.

**Style:** the default mark is a clean vector **✗** in near-black. Settings offers a single "Mark style" picker with a small curated set — ✗, ✓, and regional variants (e.g., the filled square ■ used in some locales) — nothing more. One picker, one global choice applied to all toggles; offering per-click style galleries or color/weight options would violate P1.

#### Acceptance criteria

- AcroForm checkboxes toggle correctly, including radio groups and checkboxes with custom "on" state names.
- Detector achieves ≥ 95% hit rate on a curated corpus of ~200 real-world flat forms drawn from our internal document set (§5.6), with < 1% false-positive rate (measured: clicks on non-checkbox squares like table cells must not place marks — minimum-size and context heuristics apply).
- Toggle round-trip (check then uncheck then save) leaves the document byte-equivalent to the original except metadata.

### 3.3 F3 — Signature Management

**User story:** *"I keep my signature, my initials, and my boss's signature on file. I drag the right one onto the line, nudge it into place, and save."*

#### The signature library

- Opened from the **✒ Signatures** toolbar button; renders as a Fluent flyout panel (not a modal dialog) anchored to the button, showing signature thumbnails in a vertical list.
- **Adding a signature**, via an "Add signature" button in the flyout, offers exactly three paths:
  1. **Type it** — the user types their name; it renders in a script-style font. (Fastest path; good enough for many workflows.)
  2. **Draw it** — freehand canvas (mouse, touch, or pen; pen pressure supported on Surface hardware). Stroke smoothing applied.
  3. **Import an image** — PNG/JPG file. MegaPDF automatically trims whitespace margins and offers one-click **"Remove white background"** (luminance threshold → alpha), because scanned signature images on white paper are the norm.
- Each library entry has a user-supplied name ("Dave", "Dave – initials", "M. Johnson"). Rename and delete via right-click context menu / `…` button.
- **Storage:** signatures are stored per-user in `%LOCALAPPDATA%\MegaPDF\Signatures\` as PNG (with alpha) plus a small JSON index. They are **never** embedded in application settings or roamed to any server — a signature is sensitive personal data, and local-only storage is both the simple and the safe choice. Library capacity: soft limit of 20 (covers the persona; prevents the library from becoming an unmanaged image dump).

#### Placing a signature

- **Drag and drop:** drag a thumbnail from the flyout onto the page. A live ghost preview follows the cursor; drop places it.
- **Click to stamp:** single-click a thumbnail → the cursor becomes the signature ghost → click on the page to place. (Two interaction idioms, one result; whichever the user tries first works — P1.)
- Once placed, the signature is a selected object with standard Windows selection chrome: four corner handles for **proportional-only** resize (no distortion possible), body drag to move, `Delete` key or an ✕ chip to remove, arrow keys to nudge 1 pt (`Shift+arrows` = 10 pt).
- Default placement size: 180 pt wide (a typical signature-line width), preserving aspect ratio.
- Internally the placed signature is a **stamp annotation** containing the image with alpha. It remains movable/deletable across sessions until the user chooses **"Flatten on save"** — an off-by-default setting; the default preserves editability, honoring P3. (Most recipients cannot tell the difference; flattening exists for workflows that demand it.)

#### Acceptance criteria

- Library CRUD (add via all three paths, rename, delete) with thumbnails rendering < 100 ms.
- Drag-and-drop and click-to-stamp both place a correctly sized, alpha-transparent signature.
- A placed signature reopened in Adobe Acrobat and Edge renders identically (annotation compatibility check).
- Background removal produces clean alpha on a standard scanned-signature test set.

### 3.4 F4 — File Management (Save / Save As)

**User story:** *"Ctrl+S. Done. Or 'Save As' when I need to keep the original."*

#### Behavior

| Action | Trigger | Result |
|---|---|---|
| **Open** | Toolbar, `Ctrl+O`, drag-drop onto window, double-click .pdf (file association), recent-files list | Loads document; restores last scroll position for recently opened files |
| **Save** | Toolbar, `Ctrl+S` | Overwrites the current file in place |
| **Save As** | Toolbar, `Ctrl+Shift+S` | Standard Windows file picker; suggested name `originalname - edited.pdf`; the newly saved file becomes the active document |

#### Engineering requirements

- **Atomic writes.** Save writes to a temp file in the same directory, fsyncs, then replaces the original via `ReplaceFile` (which preserves ACLs, alternate data streams, and creates the optional backup). A crash or full disk mid-save must never corrupt or truncate the user's original (P3).
- **Incremental save** where the engine supports it (append-only update sections) for large documents, falling back to full rewrite; either way the atomic-replace protocol applies.
- **Locked/readonly handling:** if the target is read-only, locked by another process, or on unavailable media, Save transparently degrades to the Save As flow with a plain-language explanation: *"This file can't be overwritten (it may be open in another program). Save a copy instead?"*
- **OneDrive/network paths** work through normal file APIs; MegaPDF performs no special cloud sync logic. (Note: per the atomic-replace protocol, OneDrive sees a single file update — no partial-state uploads.)
- **Crash recovery:** a session journal in `%LOCALAPPDATA%\MegaPDF\Recovery\` records unsaved edit operations; on next launch after an unclean exit, the user is offered a one-click restore.
- Preserves document properties MegaPDF doesn't understand: unknown annotations, XMP metadata, attachments, and tagged-PDF structure pass through untouched on save.

#### Acceptance criteria

- Kill-process test at any point during save leaves the original file intact and readable.
- Save of a 500-page document completes < 3 s (incremental path).
- Round-trip (open → no edits → save) preserves all document content and metadata except modification date.

### 3.5 F5 — Printing *(scope amendment, added in 1.4 — 2026-07-09)*

**User story:** *"Ctrl+P, pick my printer, print. Like every other Windows app."*

Printing was originally out of scope; tester demand promoted it. The scope stays
deliberately minimal (P1):

- **Ctrl+P** or the toolbar Print button opens the **standard Windows print dialog**
  with a live preview — printer choice, copies, and page ranges come from the system
  dialog. MegaPDF adds **no print-options UI of its own.**
- Pages print at 150 DPI raster (crisp for documents; matches the email-quality bar
  used elsewhere), scaled uniformly to the paper.
- What you see is what prints: the current in-memory document, including unsaved
  edits, marks, whiteouts, and signatures.

**Acceptance:** print the persona's filled form to Microsoft Print to PDF; output
matches the on-screen document page-for-page.

---

## 4. Technical Architecture & UI Framework

### 4.1 Technology stack summary

| Layer | Choice | Rationale |
|---|---|---|
| Language / runtime | **C# on .NET 8 (LTS)** | First-class WinUI 3 support, mature ecosystem, team productivity |
| UI framework | **WinUI 3 (Windows App SDK 1.5+)** | Mandated Fluent/Win11 look; native Mica, Fluent controls, dark mode, and accessibility for free |
| MVVM framework | **CommunityToolkit.Mvvm** | Source-generated observable properties/commands; minimal boilerplate |
| PDF engine | **PDFium (open source)** + in-house interactive editing layer (see §4.3) | $0 licensing budget and open-source release mandate; PDFium supplies rendering, form fill, annotations, and incremental save — the in-place text-edit layer is ours to build |
| Rendering surface | SDK's raster output composited via `Win2D`/`CanvasControl` (or SDK-native control if it meets perf bar) | GPU-composited, DPI-aware page rendering |
| Packaging | **MSIX** via Windows App SDK packaging project (§5) | Clean install/update/uninstall guarantees |
| Crash/telemetry | Local crash dumps + **opt-in** anonymous telemetry (AppCenter successor / self-hosted) | Privacy-respecting; persona handles sensitive documents |

### 4.2 Architectural overview

A straightforward layered MVVM architecture — this is a focused utility app, and the architecture should be no cleverer than the product:

```
┌───────────────────────────────────────────────────────────┐
│  Presentation (WinUI 3)                                   │
│  MainWindow · DocumentView (XAML + Win2D canvas)          │
│  SignatureFlyout · Dialogs · Toolbar                      │
├───────────────────────────────────────────────────────────┤
│  ViewModels (CommunityToolkit.Mvvm)                       │
│  DocumentVM · PageVM · SignatureLibraryVM · EditSessionVM │
├───────────────────────────────────────────────────────────┤
│  Application services                                     │
│  IDocumentService (open/save/atomic-write)                │
│  IEditService (text edit, checkbox toggle) + UndoStack    │
│  ISignatureLibrary (CRUD, image processing)               │
│  ICheckboxDetector (drawn-square heuristics, per-page)    │
│  IRecoveryJournal · ISettings · IRecentFiles              │
├───────────────────────────────────────────────────────────┤
│  PDF engine adapter (single seam)                         │
│  IPdfEngine — wraps the engine behind our own interface:  │
│  load, render page, hit-test, text runs, form fields,     │
│  annotations, incremental save                            │
├───────────────────────────────────────────────────────────┤
│  PDFium (native, via a thin P/Invoke wrapper we own)      │
│  + in-house text-edit layer built on its page-object API  │
└───────────────────────────────────────────────────────────┘
```

Key decisions:

- **The engine adapter is the only layer that references PDFium.** This contains engine risk: if PDFium's editing primitives prove insufficient for a later phase, or a better open-source engine emerges, migration cost is confined to one project. It also makes the engine mockable for tests.
- **All PDF operations run off the UI thread.** Rendering, hit-testing, detection heuristics, and saves execute on a worker pool; the UI thread only composites bitmaps and routes input. Page renders are cancellable (fast scrolling cancels stale renders).
- **Rendering pipeline:** visible pages render at current zoom × DPI into GPU bitmaps; a low-res preview of ±2 adjacent pages is pre-rendered for instant scroll; a small LRU cache (capped ≈ 150 MB) bounds memory (P4).
- **Undo model:** command pattern — every edit is a reversible `IEditOperation` (TextEdit, CheckToggle, SignaturePlace/Move/Resize/Delete) on a bounded stack (500 ops). The recovery journal (§3.4) serializes these same operations, so undo and crash recovery share one implementation.

### 4.3 PDF engine selection (resolved: open source, $0 licensing)

**Decision (Appendix B):** the engine licensing budget is $0 and MegaPDF itself ships as free, permissively licensed open source. Commercial SDKs (Apryse, Foxit) are out. License compatibility with our Apache-2.0 release is a hard selection criterion, which also rules out the AGPL family for anything we link against.

| Option | License | Verdict |
|---|---|---|
| **PDFium** | BSD-3 (permissive) | **Primary engine.** Production-grade rendering (it powers Chrome's PDF viewer), AcroForm form-fill, annotation API, page-object editing (`FPDFText_SetText`, object insert/remove), and incremental save. Consumed via prebuilt binaries and a thin P/Invoke wrapper we own and maintain in-repo. |
| iText / MuPDF | AGPL | License-compatible only if MegaPDF itself went AGPL, which would constrain downstream users and contributors; rejected. Neither offers interactive in-place editing anyway. |
| PdfPig / PDFsharp | Apache / MIT | Useful read/create utilities; no in-place editing or reliable incremental save. Retained as candidates for test tooling and structural verification, not the engine. |

**Consequence — we own the hard part.** No $0 library provides interactive in-place body-text editing (glyph metrics, font subsetting/substitution, content-stream rewriting, line reflow). That layer is built in-house on top of PDFium's page-object API.

**Scope decision (stakeholder, 2026-07-08): v1.0 ships all four features in full, including body-text editing tiers 1 and 2.** There is no phased F1 rollout. Because the text-edit layer is the product's dominant schedule risk, it is managed by build order rather than by scope cuts:

- The text-edit layer is built **first**, before UI polish and secondary features, so risk is retired at the start of the schedule, not discovered at the end.
- An early spike must prove the pipeline end-to-end (click → hit-test text run → mutate content stream via `FPDFText_SetText`/page-object APIs → incremental save → renders correctly in Acrobat and Edge) on the internal corpus before the rest of the app is layered on.
- The `IPdfEngine` adapter (§4.2) keeps the door open to swapping engines if PDFium's primitives prove insufficient.

Being open source is also a mitigation here, not just a distribution choice: the text-edit layer is exactly the kind of hard, well-scoped problem that attracts community contribution.

### 4.4 Data & privacy

- **No account, no cloud, no document upload — ever.** All processing is local. This is a marketable feature for offices handling confidential documents, and it removes an entire class of compliance concern.
- Signature images: local-only, per-user, under `%LOCALAPPDATA%` (§3.3).
- Settings: JSON in `%LOCALAPPDATA%\MegaPDF\settings.json` (simple, inspectable, survives MSIX updates).
- Telemetry: **off by default**, one-toggle opt-in, never includes document content, file names, or signature data.

### 4.5 Performance budgets (P4, testable)

| Metric | Budget | Reference hardware |
|---|---|---|
| Cold start → interactive | < 2.0 s | i5-8250U, 8 GB, SSD |
| Open 50-page PDF → first page rendered | < 1.5 s | same |
| Scroll frame rate | 60 fps sustained | same |
| Idle memory, 50-page doc | < 200 MB | same |
| Installed footprint | < 150 MB | — |
| Installer download | < 60 MB | — |

---

## 5. Installation & Deployment Strategy

### 5.1 Packaging decision: MSIX (primary)

**MSIX** is the primary and recommended packaging technology:

| Requirement | How MSIX satisfies it |
|---|---|
| Clean initial install | Declarative package; no custom actions that can fail half-way; per-user install requires no admin rights (important — Pat often can't elevate) |
| Automated updates | Built-in differential updates via App Installer; only changed blocks download |
| Complete uninstall | OS-managed containerized install — uninstall is guaranteed-complete removal of binaries and registrations; no orphaned registry keys or Program Files debris |
| Win11 alignment | Native integration with Windows App SDK deployment; Store-ready |

**Distribution channels, in priority order:**

1. **Microsoft Store** — zero-friction install for the persona ("get it from the Store" is an instruction Pat's IT department and Pat herself both trust), automatic updates handled entirely by the OS, and Store signing removes our certificate-management burden for this channel.
2. **Direct download (`.msix` + App Installer)** — from the product website for users/orgs avoiding the Store. `<AppInstaller>` manifest configured with `OnLaunch` update checks (`HoursBetweenUpdateChecks="24"`), so the direct channel also gets automated updates. Requires our own code-signing certificate (EV or Azure Trusted Signing) — budgeted as a release-infrastructure line item.
3. **winget** (`winget install MegaPDF`) — published to the community repo, pointing at the signed MSIX; serves IT departments scripting deployments.

### 5.2 Enterprise fallback: MSI via WiX (deferred, v1.x)

Some corporate environments still block Store/MSIX sideloading and mandate MSI for SCCM/Intune deployment. Plan: ship MSIX-only in v1.0; produce a WiX v4-built MSI wrapper in v1.x **if and when** enterprise demand materializes. The MSI variant would disable in-app auto-update (updates then flow through the org's deployment tooling, as those orgs require). Building both installers from day one is premature complexity for a product aimed first at individuals and small offices.

### 5.3 Update experience

- Updates are **silent and automatic** by default (Store or App Installer mechanisms). The persona should never see a "new version available!" interstitial, never click through an update wizard, and never be interrupted mid-document — updates apply on next launch.
- In-app "About" flyout shows current version and a "Check for updates" button (links to Store/App Installer check) for support scenarios.
- **Versioning:** semantic `MAJOR.MINOR.PATCH.0` (MSIX requires 4-part). Release cadence: patch releases as needed; feature releases quarterly at most — churn is a cost to this persona, not a benefit.
- **Rollback:** Store and App Installer both support publishing a rolled-back package version; the recovery journal format (§3.4) is versioned and backward-compatible so a rollback never strands unsaved-work journals.

### 5.4 Install-time integrations

Declared in the MSIX manifest (all OS-managed, all cleanly removed on uninstall):

- **File association** for `.pdf` — registered as an *available* handler, **never** silently seizing the default (respects user choice; Windows 11 requires explicit user action to change defaults anyway). First-run offers a one-click "Make MegaPDF your PDF app?" card, dismissible forever.
- **Shell context menu:** "Edit with MegaPDF" verb on `.pdf` files — the discoverable entry point for a persona who lives in File Explorer and Outlook attachments (save → right-click → edit).
- **Start menu / taskbar:** standard app-list entry; jump list shows recent documents.

### 5.5 Uninstall guarantee

Uninstall via Settings → Apps removes the package completely. Per-user data under `%LOCALAPPDATA%\MegaPDF\` (signatures, settings, recovery journals) is removed with the app container. Because signatures are personal data, the uninstaller behavior is documented plainly on the website and in the About flyout: *"Uninstalling removes your signature library."*

### 5.6 Release pipeline (summary)

- Source lives in a **public GitHub repository** (Apache-2.0). CI (GitHub Actions, `windows-latest` — free for public repos): build → unit tests (engine adapter mocked) → integration tests against the document corpus → MSIX package → sign (Azure Trusted Signing — the product's only recurring infrastructure cost) → publish to Store submission API / website / winget PR.
- **Test corpus:** assembled from our own internal documents (Appendix B). Because these are confidential business documents, the corpus lives in a **private** artifact store, never in the public source repo; corpus-dependent integration tests run only on maintainer-triggered CI, and the public repo includes a small synthetic corpus so outside contributors can still run meaningful tests.
- Every release candidate must pass the kill-during-save torture test (§3.4) and the performance budget suite (§4.5) before publish.

---

## Appendix A — Traceability: features → principles

| Design choice | Principle |
|---|---|
| No modes; click-determines-action | P1, P2 |
| No text-formatting toolbar in v1 | P1 |
| Drawn-square checkbox detection | P2 |
| Atomic saves + recovery journal | P3 |
| Signature flatten off by default | P3 |
| MSIX per-user, no-admin install | P1, P4 |
| Local-only signatures, no cloud | P3 |
| 60 MB installer / 2 s cold start budgets | P4 |
| WinUI 3 + Mica + Segoe UI Variable | P5 |

## Appendix B — Resolved decisions (2026-07-08)

The open questions from v1.0 were resolved by the stakeholder as follows; the resolutions are incorporated throughout this document.

1. **PDF engine licensing budget: $0.** Open-source libraries only ("PDF is an open standard; any open source library as a start, otherwise we write it"). Resolved to **PDFium as primary engine + in-house text-edit layer**. All four features, including full body-text editing, ship in v1.0 — see §4.3.
2. **Monetization: none — free and open source.** MegaPDF ships under **Apache-2.0** in a public repository — see §1.2 and §5.6. (Apache-2.0 chosen over MIT for its explicit patent grant; over AGPL to keep contribution and reuse friction low and stay compatible with PDFium's BSD license.)
3. **Checkbox mark default: ✗**, with a settings picker offering ✓, ✗, and regional variants — see §3.2.
4. **Test corpus: internal documents.** Held privately, never published in the source repo; a synthetic public corpus supports outside contributors — see §5.6.
