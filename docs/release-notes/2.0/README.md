# MegaPDF 2.0 — release notes and "What's New" (#146 §2)

Draft copy for every store, in `en`, `fr-CA` and `fr-FR`. Nothing here has been
pasted into a console; this is the text to paste when 2.0 is submitted.

> **Awaiting francophone review.** The French was written by the build assistant
> against `docs/localisation-glossary.md`, with the France variant derived from
> the Canadian one by the same rules `tools/gen_strings.py fr-fr` applies
> (*courriel* → *e-mail*, *infonuagique* → *cloud*, and a non-breaking space
> before `?`, `!` and `;` as well as `:`). A francophone reads it before it goes
> live — the same gate `android/RELEASING.md` and `docs/app-store-listing.md`
> put on the listing copy, and the same gate #146 §2 puts on the new 2.0 strings.

| File | Store | Field | Limit |
|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 |
| [`release-notes-2.0.md`](release-notes-2.0.md) | — | the long form, for the repo and the website | — |

Microsoft Store has four listing languages (`en-US`, `en-CA`, `fr-CA`, `fr-FR`);
the English block is pasted into both English listings. The Apple and Play
listings have three (`en-CA` / `en`, `fr-CA`, `fr-FR`).

## What each store's notes are the delta from

The stores are not all at the same public version, so "what's new" is a
different span on each of them.

| Store | Public version now | Span these notes cover |
|---|---|---|
| Microsoft Store | 1.7.0.0 | 1.7 → 2.0 |
| App Store (iOS) | 1.7.0 | 1.7 → 2.0 |
| Mac App Store | 1.7.0 | 1.7 → 2.0 |
| Google Play | **1.2.0** (versionCode 8, promoted 2026-09-04) | **1.2 → 2.0** |

**Google Play is five minor versions behind** the other three: `versionName`
is meant to track the iOS public version (`android/RELEASING.md`), and no
Android release was cut for 1.3 through 1.7. So the Play notes have to carry
French, editing the document's own text, the signature sheet, typed signatures,
document protection and the new toolbar as well as the 2.0 work — all of which
shipped to the other stores already. Its copy is therefore the longest of the
four relative to its 500-character field, and it names features rather than
explaining them.

## Where the content comes from

Written from `git log ios-v1.7.0..ee2751d` (188 commits) for the three stores at
1.7, and `git log android-v1.2.0..ee2751d -- android/ core/` (41 commits) for
Play. The issues behind them:

| Issue | What the user sees |
|---|---|
| #125, #128 | text edits that used to be refused now go through — 112 → 0 refusals over a 22,267-edit battery |
| #131 | protected PDFs open; permissions honoured; set, change and remove a password |
| #136, #137, #130 | lines drawn twice edit cleanly; an edit keeps the document's font only when that font can draw every character |
| #139 | one warning per page before a change that would alter parts of it you did not touch |
| #143 | Mac: a file opened from the Finder saves back to itself, and the first view fits the page |
| #144 | one row of tools on every platform |
| #145 | busy feedback everywhere, and the data-loss fixes around saving and closing |
| #147, #148 | a 2.5 GB file opens in milliseconds and holds megabytes, not gigabytes |
| #149 | text-heavy pages search and extract in linear time |
| #150 | `/UserUnit` honoured, so banner-size pages measure and print at their real size |
| #151, #152 | pages with large images, and colour-managed pages, draw several times faster |

## House rules these follow

- **No other platforms named** in App Store or Mac App Store copy (Apple 2.3.10).
  For consistency the Microsoft Store and Play notes stay platform-neutral too,
  even though neither store forbids it.
- No feature named that the app on *that* store does not have. Whiteout, Shrink
  for email and Print are desktop-only and appear only in the Microsoft Store
  and Mac App Store notes.
- Sentence case, the glossary's vocabulary, and no version numbers in the body —
  the store shows the version itself.
