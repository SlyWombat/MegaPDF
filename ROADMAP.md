# MegaPDF Roadmap

Issue-formatted backlog, priority-ordered. When the repo lands on GitHub, each
section becomes one issue verbatim. Nothing here is committed-to until tester
feedback is in — feedback outranks this list.

---

## #1 · Real code signing and public distribution
**Labels:** infra, release · **Size:** M (mostly process)

Test builds are self-signed, which costs one UAC prompt at install and a
SmartScreen warning on download. Move to **Azure Trusted Signing** (or Store
submission) so installs are double-click with zero prompts, then publish the
repo to GitHub (activates the existing `release.yml` tag workflow) and submit a
**winget** manifest.

**Acceptance:** a downloaded release installs on a clean machine with no
certificate step and no SmartScreen interstitial; `winget install MegaPDF` works.

---

## #2 · Full keyboard accessibility (SDD §2.2 — required)
**Labels:** accessibility · **Size:** L

Baseline shipped (control names, shortcuts, page/editor UIA names). Missing:
**Tab traversal of interactive PDF regions** (fields, checkboxes, text lines,
stamps) with Enter/Space activation, visible focus indicators on regions, and a
Narrator walkthrough of the fill-check-sign-save journey. Build on the cached
per-page interaction maps.

**Acceptance:** the SDD persona task completes keyboard-only; Narrator announces
each region meaningfully; Accessibility Insights FastPass passes.

---

## #3 · Whiteout move/resize chrome
**Labels:** enhancement, ux · **Size:** S

Whiteouts are place-and-remove today (chrome is ✕-only; repositioning means
redraw). Add drag-move and free-form corner resize like signatures (aspect
unlocked), with undo as a move operation and journal coverage.

**Acceptance:** drag/resize a whiteout, undo restores the prior rect,
crash-replay reproduces it.

---

## #4 · Text box ergonomics: font size and multi-line
**Labels:** enhancement, ux · **Size:** M

Added text is fixed 12pt Helvetica, single-line. Wants: a small size stepper on
the inline editor (persona-simple: S / M / L rather than a point picker),
Shift+Enter for multi-line (engine writes one text object per line), and reuse
for the line editor so wrapped paragraphs can be edited as a block.

**Acceptance:** place a two-line note at Large; each line remains individually
editable afterwards; undo removes the whole note.

---

## #5 · Incremental save (SDD §3.4 performance)
**Labels:** performance · **Size:** M

Saves are full rewrites via FPDF_SaveAsCopy. The SDD calls for append-only
incremental saves where possible (FPDF_INCREMENTAL) under the same
atomic-replace protocol. Measure on the 60-page corpus doc and a 500-page
document before/after; keep full-rewrite as the Shrink/flatten path.

**Acceptance:** save of a 500-page doc with one edit meets the SDD <3s budget;
kill-during-save test still passes; Acrobat/Edge open incremental saves.

---

## #6 · Shrink-for-email tuning for scanned/CCITT documents
**Labels:** enhancement · **Size:** S–M

JPEG re-encode can be a poor trade for bilevel scans (fax-style CCITT); the
current guard skips them (worst case: "already small" message). Investigate
per-image decisions: keep CCITT as-is, try grayscale JPEG for gray scans, and
consider a quality preset in settings. Validate against real scans in the
private corpus.

**Acceptance:** a typical scanned-letter PDF shrinks meaningfully or reports
honestly; no output ever larger than input.

---

## #7 · Find in document (Ctrl+F)
**Labels:** enhancement, candidate · **Size:** M

Not in the SDD, but the most-expected missing basic. PDFium has FPDFText_Find*.
Minimal chrome: a small find bar, highlight matches, Enter cycles. Needs an SDD
amendment first (keep the minimalism bar).

---

## #8 · Printing
**Labels:** enhancement, candidate · **Size:** M

Also not in the SDD; persona likely expects Ctrl+P eventually. Windows print
via PrintManager rendering pages at printer DPI. Decide scope (system dialog
only, no print options UI) before building.

---

## #9 · WiX MSI for enterprise deployment
**Labels:** infra, on-demand · **Size:** M

Per SDD §5.2: build only when an SCCM/GPO shop asks. Self-contained payload
harvest, shortcuts, .pdf ProgId registration, upgrade code, auto-update
disabled. Until then the Setup.exe bootstrapper covers friction.

---

## #10 · Localization readiness
**Labels:** infra, candidate · **Size:** L

All strings are inline English. If distribution goes beyond en-US, move UI
strings to resw, pseudo-localize, and verify layout at +30% string length.
Blocked on distribution plans; don't start speculatively.
