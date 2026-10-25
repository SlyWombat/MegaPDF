# MegaPDF 2.4 — what's new

**Approved by Dave on 2026-10-25 ("The majority are 2.4, so go live with what we have") and released on the platforms below where it is live.** As of 2026-10-25: Windows, Mac, Linux, Android and iOS are all live on 2.4.0.
Dave set the shape on 2026-10-23 (recorded on
[#724](https://github.com/SlyWombat/MegaPDF/issues/724)): "The 2.4 release notes are
simple, 'Spanish support and minor bug fixes'." One line saying Spanish is supported, then
one generic line for the other fixes, as 2.3's notes did for scanned pages. No feature list
and no per-issue detail: selection and copy (#710), tab windows (#711) and the rest are not
named. Dave set the shape and approved it.

| File | Channel | Field | Limit | Says |
|---|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 | Spanish + fixes |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 | Spanish + fixes |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 | Spanish + fixes |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 | Spanish + fixes |
| [`linux.md`](linux.md) | Linux (AppStream) | `<release><description>` | none | Spanish + fixes |
| [`check_copy.py`](check_copy.py) | — | derives the France and Spain blocks, recounts, checks the limits and the code points | — | — |

```
python3 docs/release-notes/2.4/check_copy.py          # derive fr-FR and es-ES, recount, check
python3 docs/release-notes/2.4/check_copy.py --check  # fail on drift
```

## The text, in five blocks

English, the same on all five channels:

> MegaPDF now speaks Spanish, for Latin America and for Spain. Plus minor bug fixes.

Français (Canada), written by hand; Français (France) is derived from it:

> MegaPDF est maintenant offert en espagnol, pour l'Amérique latine et pour l'Espagne. Et quelques corrections mineures.

Español (Latinoamérica), written by hand; Español (España) is derived from it by
`to_spain` in `tools/gen_strings.py`, with no punctuation pass:

> MegaPDF ahora está disponible en español, para Latinoamérica y para España. Además, correcciones de errores menores.

The French and the Spanish say "offert en" and "disponible en" rather than a literal
"speaks": it is how each language announces a language in a store. The Spain block comes out identical to the
Latin American one: nothing in the sentence is on `ES_419_TO_ES_ES`.

## Platforms

Spanish ships on all five platforms in 2.4 (Windows, macOS, Linux, iOS, Android): the
catalogues, the glossary and the store copy landed in #751, #752 and #758, and the design on
#724 names no exception. So every channel says it, Linux included. Where it differs is the
listing code, not the claim: App Store Connect lists Latin America as `es-MX` (no es-419),
Partner Center as `es-mx`, Google Play as `es-419`.

## Headings the tools read

The headings are what the listing tools find, so they are not decoration:

- The headings `## Español (Latinoamérica)` and `## Español (España)` are the exact names
  `tools/gen_listing_copy.py` passes to `release_block` and `release_block_or_pending`
  (App Store and Play What's new). It splits on the text after `## `, so the code
  after the dash is free.
- `tools/asc_publish.py` keys each block by the last backticked word of the heading:
  `es-MX` and `es-ES`, as its `LOCALES` list has them.
- `tools/msstore_submit.py` reads `[a-z]{2}-[A-Z]{2}` codes from the heading: `es-MX`
  and `es-ES`, as its `LANGS` table has them.
- `check_copy.py` fails if a heading's code is not the one its tool wants.

## Review status, and what remains

- **French: reviewed.** `FR-REVIEW #724` stays in the files by design. Fable approved the fr-CA
  block on 2026-10-23 ([#724](https://github.com/SlyWombat/MegaPDF/issues/724): "fr-CA block
  APPROVED", identical in all five channel files). The fr-FR block is derived from it by
  `tools/gen_strings.py`; Fable recomputed it with the real `to_france` and found it equal.
- **Spanish: reviewed.** `ES-REVIEW #724` stays in the files by design. Fable approved the es-419
  block on 2026-10-23 (same comment). The es-ES block is derived and was verified equal. Any
  change to either block after that review needs another Fable review.
- **Dave's approval of the shape and the wording: given.** 2026-10-23 for the shape and all
  five blocks as drafted (recorded on #724), and again on 2026-10-25 ("go live with what we have").
- **The claim must still be true at release.** Spanish must be in every build that ships
  these words. It was: all five channels are live on 2.4.0 (read back 2026-10-25).
- **Version strings** `NOTES_VERSION` in `tools/gen_listing_copy.py` is `"2.4"`. The version
  strings in `tools/msstore_submit.py` and `tools/asc_publish.py` are bumped at release time,
  per `docs/RELEASING.md`, not in this folder.
- **`devolverlo completado`** (the ruling on #724 for the 2.4 store copy) is a phrase in the
  store *descriptions*, not in these notes, which have no such sentence. Fable read it as right
  in both store sentences on 2026-10-23 and #785 applied it to the descriptions.
