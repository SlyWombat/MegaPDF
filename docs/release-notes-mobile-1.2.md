# Mobile release notes — undo and Add text

Written when the work merged (#30, #34) so the store copy is not composed under
time pressure on approval day. Paste as-is; both are within their length limits.

## The version numbers differ per platform, deliberately

- **Android** is at 1.1.2 (versionCode 7) awaiting Google's first-app review, so
  this is **1.2.0, versionCode 8** — already bumped in `android/app/build.gradle.kts`.
- **iOS has never shipped.** Version 1.0 is *rejected* (Guideline 2.1, information
  — see `docs/app-review-notes.md`) with build 27 still valid and attached. The
  plan of record is to get 1.0 approved with **build 27 as it stands**, then ship
  this work as **1.0.1**. Do not fold new features into the pending submission: the
  rejection asks for information, not code, and a new binary restarts nothing that
  needs restarting while adding review surface.

  iOS `MARKETING_VERSION` comes from the tag (`ios-v1.0.1`), so there is nothing to
  bump in the repo for it.

## Google Play — release notes (en-CA, ≤500 chars)

```
Undo anything. Every change — ticks, signatures, added text — can be taken back,
and redone.

Add text: tap the Text button, tap where it should go, and type. Handy for the
blank lines a form leaves you.

Fixed: on documents whose page box is offset (many scans and imposed pages),
taps and highlights landed in the wrong place.
```

## App Store — "What's New" (for 1.0.1, after 1.0 is approved)

```
Undo anything. Every change — ticks, signatures, added text — can be taken back
from the Undo button, and redone from the menu.

Add text: tap Text, tap the spot, type. For the blank lines a form leaves you.

Fixed: on documents whose page box is offset — many scans and imposed pages —
taps, highlights and signature placement landed in the wrong part of the page.
```

## Not in this release, on purpose

- **Editing text that is already in the document.** Adding text is new; changing
  existing body text is Windows-only and waits for the shared engine core (#33 /
  ADR-002), where the font-substitution policy can be written once.
- **Moving a placed text box** (#36). It can be undone and re-placed, which is why
  the feature is coherent without it.

Neither is mentioned in the copy above — the listing describes what the app does,
and both stores' descriptions already avoid claiming text editing on mobile.
