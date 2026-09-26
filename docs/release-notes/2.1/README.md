# MegaPDF 2.1 — release notes and "What's New"

> **2026-09-26 (#395): 2.1.0 shipped on every store on 2026-09-21, and the four
> store files in this folder now hold 2.1.1's What's New.** `tools/msstore_submit.py`
> and `tools/asc_publish.py` derive the release-notes folder from the first two
> parts of the version (`2.1.1.0` → `2.1`) and read here, so
> `microsoft-store.md`, `app-store.md`, `mac-app-store.md` and `google-play.md`
> are generated copies of the files in [`../2.1.1/`](../2.1.1/README.md) — edit
> those, then run `docs/release-notes/2.1.1/check_copy.py`. The 2.1.0 blocks
> the rest of this README describes are kept, unchanged, in
> [`2.1.0/`](2.1.0/); `release-notes-2.1.md` and `readiness.md` are 2.1.0's.

Draft copy for every store, in `en`, `fr-CA` and `fr-FR`, for the 2.1 release of
the work done since 2.0: **#332, #333, #334** (merged to `main` as `3c6f8f3`),
**#337** (the phones) and **#338** (the desktops). Nothing here has been pasted
into a console; this is the text to paste when 2.1 is submitted.

2.1 is a small release with one headline — a redaction mark you can take back —
so the copy is short. The Microsoft Store field uses 1102 of 1500 characters and
the Google Play field 388 of 500; only the Play French comes close to its limit.

> **The French was reviewed on 2026-09-20** by an AI reviewer at Dave's
> instruction (#343): every new string and every French block below, for
> correctness, register, consistency with the glossary and with 2.0's shipped
> French, platform idiom and length. What changed is in
> [the review section](#french-review-2026-09-20) at the end. 2.0's French was
> reviewed by a francophone and signed off (Dave, 2026-09-18). It follows
> `docs/localisation-glossary.md` — *redact* is **Caviarder**, *mark* is
> **marque**, *Clear all marks* is **Effacer toutes les marques**, *Undo* is
> **Annuler**, *Redo* is **Rétablir**, and the delete key is **Suppr** on Windows
> and **Supprimer** on the Mac.

| File | Store | Field | Limit |
|---|---|---|---|
| [`2.1.0/microsoft-store.md`](2.1.0/microsoft-store.md) | Microsoft Store | What's new in this version | 1500 |
| [`2.1.0/app-store.md`](2.1.0/app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 |
| [`2.1.0/mac-app-store.md`](2.1.0/mac-app-store.md) | Mac App Store | What's New in This Version | 4000 |
| [`2.1.0/google-play.md`](2.1.0/google-play.md) | Google Play | Release notes | 500 |
| [`release-notes-2.1.md`](release-notes-2.1.md) | — | the long form, for the repo and the website | — |

Microsoft Store has four listing languages (`en-US`, `en-CA`, `fr-CA`, `fr-FR`)
and the English block goes into both English listings. The Apple and Play
listings have three (`en-CA` / `en`, `fr-CA`, `fr-FR`), except that Apple spells
the France locale `fr`.

## What each store's notes are the delta from

2.0 went out on every store between 2026-09-18 and 2026-09-19, so unlike 2.0's
notes — where Google Play was five minor versions behind and had to carry 1.3
through 2.0 — all four are now within one point release of the same code, and
2.1's notes describe the same work everywhere.

| Store | Public version now | Span these notes cover |
|---|---|---|
| Microsoft Store | 1.7.0.0 live; **2.0.0.0 submitted 2026-09-19, in certification** | 2.0 → 2.1 |
| App Store (iOS) | 2.0.0 uploaded 2026-09-19 | 2.0 → 2.1 |
| Mac App Store | 2.0.0 package delivered to App Store Connect 2026-09-19 | 2.0 → 2.1 |
| Google Play | **2.0.1** (versionCode 10), in production 2026-09-19 | 2.0.1 → 2.1 |
| Linux | 2.0.0-2, released 2026-09-19 (not a store) | 2.0.0 → 2.1 |

Read off the repository — `tools/Store-Submission.md`, the tags `ios-v2.0.0`,
`android-v2.0.0`, `android-v2.0.1`, `linux-v2.0.0-2`, the run history of
`macos-appstore` and `ios-release` — because this machine reaches none of the
four consoles. **The consoles are the authority at submission time:** if a store
is still holding 2.0 in review when 2.1 goes up, the 2.1 notes stay correct as
written; if one rejected 2.0, its notes have to grow to cover 2.0 as well.

## Where the content comes from

`git log 3c6f8f3..HEAD` on the 2.1 branches, and the issues behind it:

| Issue | What the user sees |
|---|---|
| #329 | a redaction mark can be selected, moved, reshaped and removed; one gesture is one undo step; Clear all marks; and a mark no longer outlives the document it was made on |
| #328 | Redact is a named row in the phones' menu, with an on/off state a screen reader reads out, instead of an unlabelled icon |
| #336 | a pinch zooms on both phones |
| #333 | the signature library stops at twenty and says so when it is full; a signature whose image has gone leaves the list instead of sitting there as a broken thumbnail |
| #332 | one page check at a time, whichever platform asks; a document closed while a check runs settles nothing in the next one |
| #334 | a save to a file holds one copy of the document instead of two |

**Not every platform gets every line.** The phones have #328, #333 and #336; the
desktops do not, and their copy does not mention them. #332 and #334 are core
changes, so all four get them. The mark lifecycle is #329 on both phones and
#338 on both desktops — which is why it is the one item in all four stores'
copy.

## The new French, for a francophone to read

**In the app.** 2.1 adds four strings on Windows, six on Mac and Linux, three on
Android and three on iOS. The French is in `src/MegaPDF.App/Strings/fr-CA/`,
`src/MegaPDF.Avalonia/Strings/Strings.fr-CA.resx`, `android/app/src/main/res/values-fr-rCA/`
and `ios/MegaPDF/Localizable.xcstrings`; the `fr`/`fr-FR` files are derived from
those by `tools/gen_strings.py fr-fr` and must not be edited directly.

| English | French | Where | The judgement in it |
|---|---|---|---|
| Clear all marks | Effacer toutes les marques | all four | the verb for a mark, beside *Retirer la marque* for one |
| All marks cleared. Undo puts them back. | Toutes les marques sont effacées. Annuler les remet en place. | Windows, Mac and Linux | saying what Undo does, not that it exists |
| Marks cleared. | Marques effacées. | phones | the same sentence with the room a phone status line has |
| Remove mark | Retirer la marque | phones | *Retirer*, the glossary's word for taking something off a page |
| Drop every redaction mark on the document | Retirer toutes les marques de caviardage du document | Windows, Mac and Linux | the tooltip; *caviardage* is spelled out because the tooltip has room |
| Mark selected. Arrow keys move it, Delete removes it, Esc lets it go. | Marque sélectionnée. Les flèches la déplacent, Suppr la retire, Échap la désélectionne. | Windows | the key is **Suppr** on a French Windows keyboard; *désélectionne* is what 2.0's three sibling hints (signature, text, cover) already say for *lets it go* |
| Drag to move, the corners to resize, Delete to remove. | Glissez pour déplacer, les coins pour redimensionner, Supprimer pour retirer. | Mac and Linux | **Supprimer** here, because that is the Mac's key |
| Mark moved. | Marque déplacée. | Mac and Linux | the Mac's status line after a drag |
| Remove | Retirer | Mac and Linux | the screen-reader name of the ✕ |

**In this copy.** The four store blocks are each the same text as their
Canadian counterpart: nothing in the store copy is one of the words the
derivation table changes, and the punctuation France spaces differently from
Quebec is already spaced the same way — a non-breaking space before `:`. The
long form is the one exception: its Redact section names the check mark, which
is *un crochet* in Canada and *une coche* in France, so its two blocks differ
by that one word. That is a property of this copy, not a claim that the two
locales agree.

## House rules these follow

- **No other platforms named** in App Store or Mac App Store copy (Apple 2.3.10).
  For consistency the Microsoft Store and Play notes stay platform-neutral too,
  even though neither store forbids it.
- No feature named that the app on *that* store does not have, and nothing named
  that the build does not have. **#172, the iPad's own layout, is not in these
  notes:** it moved to the 2.1 *milestone* on 2026-09-18, but the work is not
  done, and no store copy may name what the build cannot do.
- Sentence case, the glossary's vocabulary, and no version numbers in the body —
  the store shows the version itself.
- Each block's character count is beside it, counted the way the field counts it.
  The Play French is the tight one at 496 of 500; anything added there has to
  come out of it first.

## French review, 2026-09-20

Reviewed by an AI reviewer at Dave's instruction (#343), against the glossary,
the shipped 2.0 French and each platform's keyboard idiom. The France blocks
were regenerated with `tools/gen_strings.py fr-fr` after the Canadian edit, and
the derived diff is the one string.

**App strings.** Twelve of the thirteen are kept as written. One changed, on
Windows: *Échap la relâche* → **Échap la désélectionne**, because that is what
the three 2.0 hints it sits beside (signature, text, cover) already say for
*lets it go*, and *relâcher* reads as releasing a grip. One note, not a change:
the Mac and Linux hint says *Supprimer* because the Avalonia catalogue is shared
and the Mac's key is called that; a French Linux keycap reads *Suppr* like
Windows, and splitting the string would be a code change.

**Store copy** (the same edit in every file that carries the block:
`docs/release-notes/2.1/*.md`, `docs/app-store-listing.md`,
`android/RELEASING.md`):

| Was | Now | Why |
|---|---|---|
| Des marques de caviardage que vous pouvez reprendre | Des marques de caviardage sur lesquelles vous pouvez revenir | *reprendre* is to take an object back or resume; *revenir sur* is to take back a decision, which is the English |
| Décider qu'une chose doit disparaître pour de bon est une décision | Faire disparaître une chose pour de bon est une décision | the French had made a tautology of *whether … is a decision* |
| avec ✕, Suppr ou | avec le ✕, Suppr ou | the article, as the other blocks already have |
| un glissement ne fait qu'une (seule) étape | un glissement ne compte que pour une (seule) étape | *one drag is one step* is a count |
| le suivant s'ouvre propre | le suivant s'ouvre sans aucune marque | *propre* is not an adverb; says what *clean* means |
| partent en une seule étape | sont retirées en une seule étape | *partir* is colloquial for marks |
| n'allument aucun point de modification | n'affichent aucun point de modification non enregistrée | the unsaved dot is shown, not lit, and named |
| embrouillée | perturbée | *tangled* for a process |
| une ligne nommée du menu | une entrée nommée du menu | a menu has *entrées*, not *lignes* |
| ne devrait pas être une devinette | ne devrait pas être un jeu de devinettes | the idiom |
| laissée là en vignette brisée | laissée là sous forme de vignette brisée | *en vignette* does not construe |
| chaque bord de son côté, parce que c'est la zone qu'on décide de masquer | chaque côté indépendamment, parce que ce que vous décidez, c'est la zone à couvrir | *each side on its own*; and *masquer* is the whiteout verb, which a redaction area must not be confused with |

The long form (`release-notes-2.1.md`) also had a dozen prose fixes of the same
kind — *et les décisions changent*, *sans rien pour l'obliger à s'en défaire*,
*cédait le pas au défilement*, *libère la sélection*, *le journal de
récupération*, *nouveau rendu*, *qui n'existe plus* — and one locale fix: its
Canadian block said *une coche* for the check mark, which is France's word; it
now says *un crochet*, and the France block keeps *coche*.

Not changed, on purpose: *Caviarder* and *marque* (glossary); *bogue* (the
Académie's word too, so both locales); *zoome* (OQLF and Larousse both accept
*zoomer*); the Play block's *réversibles* headline, which is the short form the
500-character field needs. Counts after the review: Microsoft Store 1465 of
1500, App Store 1565, Mac App Store 1455, Google Play 496 of 500.
