# MegaPDF 2.5 — what's new

**Draft, not yet released.** Dave set the wording on 2026-11-16: "Just say linked to support
page, no need for additional description." That gives one line, the same on every channel. It
names the About link to the support page that #730 added to all four apps (PR #850): Windows,
the Avalonia app for Mac and Linux, iOS and Android.

**Not named, by that decision:** the Android rotation fix (#826: turning the phone used to
reopen the document the app was launched with). It ships in 2.5 all the same. Everything else
merged since 2.4.0 is tests, tooling or docs (#820, #847, #726) and changes nothing a user can
see.

| File | Channel | Field | Limit | Says |
|---|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 | support link |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 | support link |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 | support link |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 | support link |
| [`linux.md`](linux.md) | Linux (AppStream) | `<release><description>` | none | support link |
| [`check_copy.py`](check_copy.py) | — | derives the France and Spain blocks, recounts, and checks the limits and the code points | — | — |

```
python3 docs/release-notes/2.5/check_copy.py          # derive fr-FR and es-ES, recount, check
python3 docs/release-notes/2.5/check_copy.py --check  # fail on drift
```

## The text, in five blocks

English, the same on all five channels:

> About now links to the support page.

Français (Canada) is written by hand, and Français (France) is derived from it. The France
block comes out identical, because nothing in the sentence is in `FR_CA_TO_FR_FR`:

> La section À propos comprend maintenant un lien vers la page de soutien.

Español (Latinoamérica) is written by hand, and Español (España) is derived from it by
`to_spain`. It also comes out identical:

> La sección Acerca de ahora incluye un enlace a la página de soporte.

"About" is a window on the desktops and a screen or sheet on the phones. So the French and the
Spanish say *section* and *sección* instead of naming a window. They use the apps' own words:
*À propos* and *Acerca de* (glossary, "About MegaPDF"), and *soutien* and *soporte* (the link
labels #730 added).

## Headings the tools read

These are unchanged from 2.4; see [`../2.4/README.md`](../2.4/README.md#headings-the-tools-read).
`check_copy.py` fails if a heading's code is not the one its tool wants.

## Review status, and what remains

- **French: not yet reviewed.** `FR-REVIEW #730`. Fable reviews the fr-CA block, and the fr-FR
  block is derived from it.
- **Spanish: not yet reviewed.** `ES-REVIEW #730`. Fable reviews the es-419 block, and the
  es-ES block is derived from it.
- **Dave's wording: given** (2026-11-16, above). His approval to go live comes at release time.
- **The claim must be true in every build that ships these words.** It is. #850 merged the
  About links on all four apps before any 2.5 tag.
- **Version strings.** `NOTES_VERSION` in `tools/gen_listing_copy.py` is still `"2.4"`. It and
  the strings in `tools/msstore_submit.py` and `tools/asc_publish.py` are bumped at release
  time, per `docs/RELEASING.md`, not in this folder.
