# Google Play — release notes, 2.2

Play Console → the release → **Release notes**, per language. Limit **500
characters**, the tightest of the five, and the field where the French has to be
cut rather than translated long.

`tools/play_listing.py` pushes the text it finds in `android/RELEASING.md`'s
copy-by-language section, which `tools/gen_listing_copy.py` fills from this file
(the [README](README.md) says how).

**Play is the only channel that may name the informed-permission choice.** #558
landed on Android alone (#581); the desktops and the phones' other half still
refuse. It is also the only channel where whiteout is *new* rather than improved
— Android had no whiteout tool at all until #565 on 2026-10-01.

**Where the French is cut.** The English spends 489 of 500 and the French 470.
The French drops the English's *Added text takes more than one line* — the one
item of the four paragraphs that is a refinement rather than a new thing — and
shortens the page-tools list (`celles d'un autre PDF`, `Une annulation suffit`).
Anything added to the French has to come out of it first.

*The French below is new and has **not** been reviewed. 2.1.1's was reviewed on
2026-09-26 (#343); every 2.2 string in the app still carries an `FR-REVIEW`
comment of its own. The France block is derived from the Canadian one by
`check_copy.py` in this folder — edit the Canadian block, then run it.*

## English (Canada) — `en-CA`

**Release notes** [500] (489)

```
Reading mode hides everything but the page. Sepia for a long read, Night for a dark room — Night inverts pictures too, on purpose, and says so.

Pages: rotate, delete, reorder, insert a blank page, add another PDF's pages, or save a selection as its own file. Each is one Undo.

Whiteout arrives: cover anything on the page, then move and resize it. Added text takes more than one line.

Where a document's author asked that it not be changed, MegaPDF says so and leaves the choice to you.
```

## Français (Canada) — `fr-CA`

**Notes de version** [500] (470)

```
Le mode lecture ne laisse que la page. Sépia pour lire longtemps, Nuit pour une pièce sombre — le mode nuit inverse aussi les images, et le dit.

Pages : pivoter, supprimer, réordonner, insérer une page vierge, ajouter celles d'un autre PDF, extraire une sélection. Une annulation suffit.

Le correcteur arrive : masquez une zone, puis déplacez-la et redimensionnez-la.

Quand l'auteur a demandé qu'un document ne soit pas modifié, MegaPDF le dit et vous laisse choisir.
```

## Français (France) — `fr-FR`

**Notes de version** [500] (470)

```
Le mode lecture ne laisse que la page. Sépia pour lire longtemps, Nuit pour une pièce sombre — le mode nuit inverse aussi les images, et le dit.

Pages : pivoter, supprimer, réordonner, insérer une page vierge, ajouter celles d'un autre PDF, extraire une sélection. Une annulation suffit.

Le correcteur arrive : masquez une zone, puis déplacez-la et redimensionnez-la.

Quand l'auteur a demandé qu'un document ne soit pas modifié, MegaPDF le dit et vous laisse choisir.
```

> The France block is derived from the Canadian one: none of this copy is one of
> the words the derivation table changes, and the only punctuation France spaces
> differently from Quebec here is the colon, which both space the same way.
>
> **No signature of either kind is named in this block (#602).** The
> digital-signature warning (#497) is real on Android and would be a fifth
> paragraph; there is no room for it at 500 characters, and it is the item a
> person is least likely to go looking for. It is named in the long form and in
> the three roomier channels instead. Nothing here implies that placing a
> signature makes a document verifiable, certified or binding, because nothing
> here mentions placing one.
