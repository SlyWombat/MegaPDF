# Mobile release notes drafted for 1.3 — SPENT, published as 1.2.0

> ⚠️ **The release-note copy in this file has already been published.** It was
> drafted for #36 and #43 as a separate 1.3, but that work folded into **1.2.0
> (vc9 never happened)** and went to Google Play production on **2026-09-04**.
> Verified by ancestry: `daa0b07` (#36 drag), `9ae70cb` and `4fdc0a3` (#43 faces)
> are all ancestors of the `android-v1.2.0` tag.
>
> **Do not paste the Play or App Store blocks below into a future release.**
> They describe features the public already has. Android's next release is
> 1.3.0 / vc9 and needs its own copy — as of 2026-09-10 the only unreleased
> Android work is brand tokens and the design conformance pass (#76-#80), which
> is cosmetic.
>
> The file is kept for the two sections that have **not** expired: *What the copy
> deliberately does not say* and *Not in this release, on purpose*. Those are
> standing editorial and design decisions, and they still apply.

Continues `docs/release-notes-mobile-1.2.md`, which is the copy that actually
shipped (`android/RELEASING.md` records it as the en-CA source for vc8).

## Google Play — release notes (en-CA) — ALREADY PUBLISHED as 1.2.0

```
Text you placed is no longer stuck. Tap it to select it, drag it where it
belongs, or tap the pencil to fix a typo.

Choose the size and the font. Six sizes, and sans, serif or monospace, so what
you add matches the form you are filling in — on new text and on text you
already placed.

Everything is still one undo away.
```

*(around 330 characters)*

## App Store — "What's New" — ALREADY PUBLISHED

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
  waiting on the shared engine core (#33 / ADR-003 — ADR-002 is the macOS desktop one). Both stores' descriptions
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
desktop with the build installed (`tools/screenshots-windows/`). **Done
2026-09-09:** all five re-shot at 2482x1541 from the packaged 1.7.0 build and
submitted with it. `docs/microsoft-store-listing.md` §Screenshots is current.
