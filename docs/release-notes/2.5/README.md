# MegaPDF 2.5 — what's new

**Draft, not yet released.** There are two things to say:

- **On every channel:** the About link to the support page, which #730 added to all four apps (PR #850): Windows, the Avalonia app for Mac and Linux, iOS and Android.
- **On Google Play only, first:** the Android rotation fix (#826). Turning the phone used to reopen the document the app was launched with.

Dave, 2026-11-16, on the support line: "Just say linked to support page, no need for additional description." That cut the first draft's description of what the support page offers. It did not cut the Android fix.

Everything else merged since 2.4.0 is tests, tooling or docs (#820, #847, #726). None of it changes anything a user can see.

| File | Channel | Field | Limit | Says |
|---|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 | support link |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 | support link |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 | support link |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 | rotation fix + support link |
| [`linux.md`](linux.md) | Linux (AppStream) | `<release><description>` | none | support link |
| [`check_copy.py`](check_copy.py) | — | derives the France and Spain blocks, recounts, and checks the limits and the code points | — | — |

```
python3 docs/release-notes/2.5/check_copy.py          # derive fr-FR and es-ES, recount, check
python3 docs/release-notes/2.5/check_copy.py --check  # fail on drift
```

## The text, in five blocks

The shared sentence, in English, on all five channels:

> About MegaPDF now links to the support page.

Français (Canada) is written by hand, and Français (France) is derived from it. The France
block comes out identical, because nothing in the sentence is in `FR_CA_TO_FR_FR`:

> La section À propos de MegaPDF comprend maintenant un lien vers la page de soutien.

Español (Latinoamérica) is written by hand, and Español (España) is derived from it by
`to_spain`. It also comes out identical:

> La sección Acerca de MegaPDF ahora incluye un enlace a la página de soporte.

"About" is a window on the desktops and a screen or sheet on the phones. So the French and the
Spanish say *section* and *sección* instead of naming a window. They use the apps' own words:
*À propos* and *Acerca de* (glossary, "About MegaPDF"), and *soutien* and *soporte* (the link
labels #730 added).

Google Play puts the rotation fix in front of the shared sentence:

> Turning your phone no longer reopens a different document; you stay on the one you're reading.
>
> Faire pivoter votre téléphone ne rouvre plus un autre document. Vous restez sur celui que vous lisez.
>
> Girar el teléfono ya no vuelve a abrir otro documento. Te quedas en el que estás leyendo.

`check_copy.py` requires every channel's block to equal the shared sentence, except Play's,
which must end with it.

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
