# Google Play — release notes, 2.1.0 (versionCode 11)

Play Console → the release → **Release notes**, per language. Limit **500
characters**, the tightest of the four, and the field where the French has to be
cut rather than translated long. The public Android version is 2.0.1
(versionCode 10, in production since 2026-09-19), so these notes are the delta
from one point release to the next and can spend every character on what changed.

`tools/play_submit.py` uploads binaries only; this text is pasted by hand, or
sent in the `releaseNotes` of an `edits/.../tracks` call.

*The French copy is new for 2.1 and has not been reviewed. It follows `docs/localisation-glossary.md`; [README](README.md) lists it beside the strings the app itself gained, for a francophone to read.*

## English (Canada) — `en-CA`

**Release notes** [500] (388)

```
Redaction marks you can take back: select one, move it, reshape it, or remove it, and clear every mark at once. Undo puts them back, a drag is one step, and a mark no longer outlives the document it was made on.

Redact is now a named row in the menu, with an on/off state a screen reader reads out. A pinch zooms the page.

The signature library holds twenty and says so when it is full.
```

## Français (Canada) — `fr-CA`

**Notes de version** [500] (487)

```
Des marques de caviardage réversibles : sélectionnez-en une, déplacez-la, redimensionnez-la ou retirez-la, et effacez toutes les marques d'un coup. Annuler les remet en place, un glissement ne fait qu'une étape, et une marque ne survit plus au document d'origine.

Caviarder est maintenant une ligne nommée du menu, avec un état activé ou désactivé annoncé par un lecteur d'écran. Un pincement zoome la page.

La bibliothèque de signatures en garde vingt et le dit quand elle est pleine.
```

## Français (France) — `fr-FR`

**Notes de version** [500] (487)

```
Des marques de caviardage réversibles : sélectionnez-en une, déplacez-la, redimensionnez-la ou retirez-la, et effacez toutes les marques d'un coup. Annuler les remet en place, un glissement ne fait qu'une étape, et une marque ne survit plus au document d'origine.

Caviarder est maintenant une ligne nommée du menu, avec un état activé ou désactivé annoncé par un lecteur d'écran. Un pincement zoome la page.

La bibliothèque de signatures en garde vingt et le dit quand elle est pleine.
```

> The two French blocks are identical: nothing in this copy is one of the words
> the derivation table changes, and the punctuation France spaces differently
> from Quebec is already spaced the way both do — a non-breaking space before `:`.
> The French runs three lines where the English runs three, but each is a little
> longer; at 500 characters that is the whole budget.
