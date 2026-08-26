# Mobile release notes — text you can move, correct and style

Written when the work merged (#36, #43) so the store copy is not composed under
time pressure on approval day. Paste as-is; both are within their length limits.

Continues `docs/release-notes-mobile-1.2.md`, which shipped Add text and undo
and listed **"moving a placed text box (#36)"** under *Not in this release, on
purpose*. That caveat is now spent.

## Version numbers — check before you paste

`android/app/build.gradle.kts` currently reads **1.2.0, versionCode 8**, which is
the release 1.2 prepared and has not yet been superseded. Whether this work goes
out as 1.3.0/vc9 or folds into an unshipped 1.2 depends on whether vc8 actually
reached Play — **look before bumping.**

iOS is the same story as last time: `MARKETING_VERSION` comes from the tag, so
there is nothing in the repo to bump, and nothing here should be folded into a
submission that is waiting on review. See `docs/app-review-notes.md`.

## Google Play — release notes (en-CA, ≤500 chars)

```
Text you placed is no longer stuck. Tap it to select it, drag it where it
belongs, or tap the pencil to fix a typo.

Choose the size and the font. Six sizes, and sans, serif or monospace, so what
you add matches the form you are filling in — on new text and on text you
already placed.

Everything is still one undo away.
```

*(around 330 characters)*

## App Store — "What's New"

```
Text you placed is no longer stuck. Tap it to select it, drag it where it
belongs, or tap the pencil to correct it.

Choose the size and the font — six sizes, and sans, serif or monospace — so
what you add matches the form you are filling in. Works on new text and on text
you have already placed, and it is all one undo away.
```

## What the copy deliberately does not say

- **"Three fonts"** rather than naming Helvetica, Times and Courier. The names
  are exact in the PDF (SDD §6.2 contract 4) and must not drift, but they mean
  nothing to the person reading a store listing. Sans/serif/monospace does.
- **Nothing about bold or italic.** They are not offered. The full standard-14
  set was considered and cut: §3.1 keeps formatting controls out, and a choice
  between three faces is not a formatting toolbar.
- **Nothing about editing the document's own text.** Still Windows-only, still
  waiting on the shared engine core (#33 / ADR-002). Both stores' descriptions
  already avoid claiming it on mobile — keep it that way.

## Not in this release, on purpose

- **Resizing a text box by dragging a handle.** A signature stamp resizes by
  scaling its image; a text box would have to change font size, which is now a
  picker instead. The selection overlay deliberately has no resize handle.
- **Text boxes written by MegaPDF for Windows 1.6.x.** They carry no id, so the
  phones decline to move or edit them and say so rather than silently addressing
  the wrong box. Not worth a line in the listing, but it is the one case where
  the feature visibly declines to work — worth knowing if a reviewer asks.

## Screenshots

A new `text` screenshot state ships with this work, so both stores get a shot of
the text editor with the size and font pickers open. Run the **iOS Screenshots**
and **Android Screenshots** workflows and upload the `-text` capture alongside
the existing ones; captions are in `docs/app-store-listing.md` §Screenshots.

The Windows Store screenshots are **not** covered by that — they need a real
desktop with the new build installed (`tools/screenshots-windows/`), and the four
on disk are still from 1.6.2. `tools/Store-Listing.md` §Screenshots carries the
warning.
