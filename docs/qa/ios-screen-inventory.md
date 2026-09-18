# iPhone and iPad — screen inventory and QA checklist (#146 §1b)

Every screen, sheet, alert, menu, mode, notice, busy state and error the iOS app
can show. Tick a row when it has been *seen* on the device class, language, theme
and text size the pass is covering.

Run the pass on the **smallest** and **largest iPhone**, an **iPad**, in **en,
fr-CA and fr-FR**, **light and dark**, and again at the **largest Dynamic Type
size** (AX5).

**Look for:** clipping, overlap, untranslated text, the wrong theme, broken
layout, and labels that truncate or wrap badly — French words are longer, and the
stacked Draw/Type/Photo labels exist precisely because they hyphenated in French
on a phone (`SignatureViews.swift:213`).

## How to pose each screen

The app has a screenshot mode driven by launch arguments — no tapping needed for
the posed states:

```
xcrun simctl launch --console <udid> com.megapdf.ios \
  -screenshot search -AppleLanguages "(fr-CA)" -AppleLocale fr_CA
```

`-screenshot` accepts `home viewer sign draw search text text-edit story`
(`ViewerModel.swift:217`). Everything else must be tapped through, or driven from
`MegaPDFUITests`.

Dynamic Type is set on the simulator, not by a launch argument:

```
xcrun simctl ui <udid> content_size accessibility-extra-extra-extra-large
xcrun simctl ui <udid> appearance dark
```

---

## 1. Screens

The whole app is SwiftUI: one `WindowGroup` → `ContentView`, which switches on a
four-case state machine (`home / loading / passwordNeeded / viewing`). There is
no tab bar and no navigation hierarchy beyond one `NavigationStack`.

| # | Screen | How to reach it | Posed by |
|---|---|---|---|
| 1.1 | **Home** — "MegaPDF", "Open. Fix. Save. Done.", Open PDF button, Recent list, info ⓘ | launch | `-screenshot home` |
| 1.2 | Home with **no recents** (first run) | fresh install | delete the app, relaunch |
| 1.3 | Home with the red **error text** under the button | open a file that has gone away | tap a stale recent |
| 1.4 | **Opening…** — full-screen spinner, only after 0.5 s | open a large file | open `huge-2_5gb.pdf` |
| 1.5 | **Protected-document screen** — display name under an always-present alert | open a protected PDF | real device |
| 1.6 | **Viewer** — page list, nav bar, bottom toolbar | open any PDF | `-screenshot viewer` |
| 1.7 | Viewer, page still rendering (white placeholder) | scroll fast in a long file | real device |
| 1.8 | Viewer nav title with the **"• " dirty marker** | make any edit | real device |
| 1.9 | **About** sheet — version, copyright, thanks, GitHub link, Third-party notices | ⓘ on Home, or More ▸ About | real device |
| 1.10 | **Third-party notices** — ~150 KB monospaced scroll, spinner while loading | About ▸ Third-party notices | real device |
| 1.11 | Launch screen (empty system background, no logo) | cold launch | real device |

## 2. Sheets

| # | Sheet | Detents | How to reach it |
|---|---|---|---|
| 2.1 | **Signatures** library — adaptive card grid, Draw / Type / Photo row, Close | medium, large | `-screenshot sign` |
| 2.2 | Signatures **empty state** | medium | fresh install ▸ Sign |
| 2.3 | **Draw signature** — 220 pt canvas, Cancel / Clear / Save | default | `-screenshot draw` |
| 2.4 | **Type signature** — name field + live Snell Roundhand preview, Cancel / Add | default | Sign ▸ Type |
| 2.5 | **Add / Edit text** — Text field, Size (8–24), Font (Helvetica / Times / Courier) | medium | `-screenshot text` |
| 2.6 | **Body text editor** — one field + footer, Cancel / Save | medium | `-screenshot text-edit` |
| 2.7 | **Document protection** — `unlock` / `set` / `change` modes; `change` adds a Change/Remove segmented picker | medium, large | More ▸ Protection… |
| 2.8 | Protection sheet, **busy** — the confirm button is replaced by a spinner, and interactive dismiss is disabled | medium | commit a change |
| 2.9 | **Files picker** (import) — `.pdf` only | system | Open PDF |
| 2.10 | **Files picker** (export) — "Save a copy", default filename = display name | system | More ▸ Save a copy |
| 2.11 | **Photos picker** — images | system | Sign ▸ Photo |

There is **no share sheet** anywhere in the app.

## 3. Alerts and confirmation dialogs

| # | Alert | Buttons | How to reach it |
|---|---|---|---|
| 3.1 | **Protection required** — "This document is protected." + secure field | Open / Cancel | open a protected PDF |
| 3.2 | 3.1 after a wrong entry — "That … didn't work. Try again." | Open / Cancel | get it wrong once |
| 3.3 | **Status message alert** — the title *is* the message | OK | every status string (below) |
| 3.4 | **Unsaved changes** — "This document has unsaved changes." | Save / Discard / Cancel | edit, then Close |
| 3.5 | **Change this page?** (#139) — "…may slightly alter parts of it you haven't touched." | Continue / Cancel | edit body text on a page needing a rewrite |
| 3.6 | **Rename signature** — name field | Save / Cancel | card ⋯ ▸ Rename |
| 3.7 | **Delete "{name}"?** — "This cannot be undone." | Delete / Cancel | card ⋯ ▸ Delete |

3.7 is a `confirmationDialog`, so it is an action sheet on iPhone and a **popover
anchored to the card** on iPad — check both.

## 4. Menus

| # | Menu | Items |
|---|---|---|
| 4.1 | Viewer **More** (⋯, id `viewerMore`) | Save a copy · Protection… · **Unlock with owner credential…** (only when restricted) · — · About MegaPDF |
| 4.2 | Signature card **⋯ menu** | Rename · Delete (destructive) |
| 4.3 | Signature card **long-press context menu** | same two |

On iOS 26 the system may fold the bottom toolbar into its own "More" button —
the review UI test already handles that fallback
(`DemoFlowUITests.swift:234`). Check whether it happens on the smallest iPhone in
French, where the labels are longest.

There is **no zoom menu on iOS.** Zoom is gesture-only: pinch clamped 1×–4× and
double-tap toggling 1× ↔ 2×. There is no zoom %, no Fit width and no Fit page.

## 5. Modes and the notice each shows

iOS has **no persistent mode banner** — unlike the Mac app. Each armed mode
announces itself with a modal alert instead.

| # | Mode | Armed by | Notice | Disarmed by |
|---|---|---|---|---|
| 5.1 | **Add text** | bottom bar ▸ Add text | alert "Tap the page where the text should go" | next page tap, or Cancel |
| 5.2 | **Place signature** | pick a card in the Signatures sheet | alert "Tap the page where the signature should go" | next page tap |
| 5.3 | **Find** | bottom bar ▸ Search | inline bar; count reads "", "No results" or "N of M" | Done |
| 5.4 | **Edit body text** | tap a line of the document's own text | opens the body-text sheet; a spinner sits on the line while the #139 verdict runs | Cancel / Save |
| 5.5 | Tick / checkmark | tap an AcroForm field, an existing mark, or a detected square | none | — |

**Redact does exist on iOS** (#173) — bottom bar ▸ Redact (id `viewerRedact`),
armed state on the button's accessibility *value* ("On"/"Off"), a drag marks an
area, the tool disarms itself once a mark lands, and Save raises a confirmation
before anything is removed. A mark on its own does not change the document, so
the title stays clean until it is applied. This line used to say redaction was
absent, which was true when the inventory was written and stopped being true
when #173 landed.

**Cover (whiteout) does not exist on iOS.** The mobile set is fill, check, sign,
find, add text and redact (`ViewerView.swift:169`). Do not file its absence as a
defect here; it is a platform-feature gap, and belongs on its own issue if Dave
wants it.

Tap dispatch order, for reproducing an ambiguous tap: pending signature → pending
text → existing signature → text box → AcroForm field → existing mark → detected
square → body-text line → empty page (scanned hint).

## 6. Notices, status and busy states

### Notice banner (auto-dismissing capsule at the bottom, id `noticeBanner`)

| # | Notice |
|---|---|
| 6.1 | "The owner of this document has restricted changes. Unlock it with the owner … to edit it." — on a restricted open, and on **every** refused edit |
| 6.2 | "The original font couldn't show this text, so a similar standard font was used." |
| 6.3 | "This page is a scanned image, so its text can't be edited." (once per document) |
| 6.4 | The three layout-refusal notices: moving text elsewhere · making other parts look different · disturbing the layout |
| 6.5 | "Document unlocked." |
| 6.6 | "… removed." / "… changed." / "… set." after a protection change |

### Status messages — these are **modal alerts with OK**, not toasts

| # | Message |
|---|---|
| 6.7 | "Saved" |
| 6.8 | "Signature added" |
| 6.9 | "Tap the page where the text should go" |
| 6.10 | "Tap the page where the signature should go" |
| 6.11 | "This text was added by an older version and can't be edited here." |

### Busy (appears only after 0.5 s, then held at least 0.3 s)

| # | Surface | Labels |
|---|---|---|
| 6.12 | **Document strip** under the nav bar (id `busyStrip`), posts a VoiceOver announcement when it appears | Opening… / Saving… / Checking the saved file… / Checking this page… / Applying… / Searching… |
| 6.13 | **Page spinner** (id `pageBusy`), centred on the affected rect | same set |
| 6.14 | **Opening…** full screen (id `busyOpening`) | Opening… |
| 6.15 | Signature card thumbnail spinner | — |
| 6.16 | Protection sheet confirm-button spinner | — |
| 6.17 | Third-party notices spinner | — |
| 6.18 | Save title flips to "Saving…"; Close, Sign, Add text, Undo and Redo disable; repeat taps are **swallowed, not queued** | — |

## 7. Errors

| # | Group | Messages |
|---|---|---|
| 7.1 | Home | That entry was unreadable and has been removed. · That file is no longer accessible. Pick it again to reopen it. · unsupported protection · too large · Couldn't read that file. · Couldn't open that file. |
| 7.2 | Editing | Couldn't change the document. · Couldn't change that text. · Couldn't add that text. · Couldn't move that text. · Couldn't remove that text. · This text was added by an older version… |
| 7.3 | Undo / redo | Couldn't undo that. · Couldn't redo that. |
| 7.4 | Signatures | Couldn't decode that image. · Couldn't capture the drawing. · Couldn't read this signature's image. · Couldn't move the signature. · Couldn't remove this signature. · That signature's image is missing. · Couldn't save the signature. · Couldn't place the signature. |
| 7.5 | Save | Save failed — use Save a copy. (+ the system error) · Couldn't prepare the copy. |
| 7.6 | Protection, inline in the sheet | That … didn't work. Try again. · Couldn't unlock this document. · The passwords don't match. |
| 7.7 | About | The notices file is missing from this build. |

## 8. Flows to walk with real files (#146 §1b)

- [ ] open (Open PDF, a recent, a large file)
- [ ] scroll (short file, 10,000 pages)
- [ ] zoom (pinch to 4×, double-tap, pinch back)
- [ ] find (a hit, no hit, next/previous)
- [ ] tick a checkbox (AcroForm and a drawn square)
- [ ] sign (draw, type, photo; place, move, resize, delete)
- [ ] add text
- [ ] edit body text
- [ ] undo and redo
- [ ] **redact** — arm, drag, confirm, and read the saved copy back from outside
      the app (the rule #173 sets: never ask PDFium whether PDFium removed it)
- [ ] save, and save a copy
- [ ] set protection, then remove it
- [ ] close with unsaved changes
- [ ] relaunch after the app is killed

Cover, shrink and print have no iOS surface — skip them here.

**#259 cannot happen on iOS**: zoom is clamped to 1×–4× (`ViewerView.swift:30`,
`min(max(zoom * gestureZoom, 1), 4)`), so there is no zoom below 100 % for a
dropped signature to be clamped up from. Measured as well as read, in the
2026-09-18 RC pass: a hard pinch inwards from 1× leaves the page exactly as wide
as it was.

## 9. iPad vs iPhone

The app has **no iPad-specific layout**: no split view, no sidebar, no idiom
branching. The only adaptation is the bottom toolbar, which shows **icon only on
iPhone (compact)** and **icon + title on iPad (regular)**. Everything else adapts
only through system behaviour, so these are the surfaces worth extra attention on
an iPad:

- [ ] the More menu and the card ⋯ menu as **popovers**
- [ ] Delete "{name}?" as a **popover anchored to the card**, not a sheet
- [ ] `.medium` detents on a 13" screen — the half-height design assumes a phone
- [ ] the signature grid at its widest (adaptive 150–240 pt columns)
- [ ] a single centred column with the grey backdrop either side
- [ ] all four orientations (iPhone has no upside-down; iPad has all four)

## 10. Dynamic Type (run the whole of §1–§7 again at AX5)

Nothing in the app sets a fixed point size for body text, and nothing clamps
Dynamic Type — so at AX5 the risk is fixed **frames**, not fixed fonts. Check:

- [ ] signature card, fixed height 88 pt
- [ ] drawing canvas, fixed height 220 pt
- [ ] type-signature preview card, fixed height 120 pt
- [ ] busy strip and notice banner paddings
- [ ] `.lineLimit(1)` truncation: the signature name, the find count
- [ ] the stacked Draw / Type / Photo labels (`minimumScaleFactor(0.8)`) — **in French**
- [ ] the bottom toolbar, especially whether iOS folds it into a system More

## 11. Things that look like defects but are not

- Mode prompts and confirmations are **modal alerts**, by current design — not toasts.
- Helvetica / Times / Courier are untranslated on purpose.
- The empty-state icon and the Snell Roundhand preview use fixed point sizes on
  purpose: one is a decorative symbol, the other is rasterised ink.
- "MegaPDF" is the one unlocalised string in the catalogue — it is the brand name.
