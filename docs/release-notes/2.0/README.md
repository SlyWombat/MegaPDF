# MegaPDF 2.0 — release notes and "What's New" (#146 §2)

Draft copy for every store, in `en`, `fr-CA` and `fr-FR`. Nothing here has been
pasted into a console; this is the text to paste when 2.0 is submitted.

> **The French is signed off.** It was written by the build assistant against
> `docs/localisation-glossary.md`, with the France variant derived from the
> Canadian one by the same rules `tools/gen_strings.py fr-fr` applies
> (*courriel* → *e-mail*, *infonuagique* → *cloud*, and a non-breaking space
> before `?`, `!` and `;` as well as `:`). A francophone reviewed it and signed it
> off (Dave, 2026-09-18), and the recommendations of the independent Fable review
> are applied ([`french-review-fable.md`](french-review-fable.md), #242). Hand-made
> France wording (« en étaient à la 1.7 ») lives in the fr-FR blocks themselves.

**The French strings themselves** — everything 2.0 adds or changes in the app, as
opposed to this store copy — are in [`french-review.md`](french-review.md), laid
out for the same reviewer: 230 rows, 37 of them needing judgement rather than
proofreading.

| File | Store | Field | Limit |
|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 |
| [`release-notes-2.0.md`](release-notes-2.0.md) | — | the long form, for the repo and the website | — |

The captures that go up beside this copy are measured in
[`capture-gate-report.md`](capture-gate-report.md) (the stills) and
[`preview-videos.md`](preview-videos.md) (the nine app preview clips).

Microsoft Store has four listing languages (`en-US`, `en-CA`, `fr-CA`, `fr-FR`);
the English block is pasted into both English listings. The Apple and Play
listings have three (`en-CA` / `en`, `fr-CA`, `fr-FR`).

## For the francophone reviewer

*Settled by the review (2026-09-18); kept as the record of what was asked.*

Beyond the register, three things to settle:

- **The Play bullets are shorter than the English**, because 500 characters in
  French buys fewer words. "Huge PDFs open straight away, and pages full of
  pictures draw far faster" became only *Les PDF très volumineux s'ouvrent tout
  de suite* — the picture-heavy pages, one of 2.0's most visible improvements,
  are dropped. Is that the right thing to lose?
- **Store consoles sometimes normalise pasted text**, so the narrow
  non-breaking spaces `fix_french_spacing.py` puts before `:` `;` `?` `!` are
  worth one last look in the console itself, after pasting.
- **"Enregistrer une copie" vs "Enregistrer sous"** — the app says the first;
  the notes follow the app rather than the platform convention. Confirm that is
  what you want in copy people read before they install.

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
| #173 | redact, and the marked content is out of the file rather than covered; whiteout says it only covers |

## House rules these follow

- **No other platforms named** in App Store or Mac App Store copy (Apple 2.3.10).
  For consistency the Microsoft Store and Play notes stay platform-neutral too,
  even though neither store forbids it.
- No feature named that the app on *that* store does not have. Whiteout, Shrink
  for email and Print are desktop-only and appear only in the Microsoft Store
  and Mac App Store notes.
- Sentence case, the glossary's vocabulary, and no version numbers in the body —
  the store shows the version itself.
