# MegaPDF 2.2 — store listing copy, for Dave to approve

**Draft. Nothing here has been pasted into a console and no publishing tool was
run.** This folder is the text to paste, or to let the tools push, when 2.2 is
submitted. The tree is still 2.1.1 everywhere
(`src/MegaPDF.App/MegaPDF.App.csproj`, `src/MegaPDF.Avalonia/MegaPDF.Avalonia.csproj`,
`android/app/build.gradle.kts`, `ios/project.yml`); no version was bumped here.

2.2's headlines are **reading mode** and **the page tools**, and the live listings
described neither. Both now appear in every channel's *description* — the evergreen
slot — as well as in each channel's *what's new*. Reading mode leads, because it is
the one a person pictures themselves using.

| File | Channel | Field | Limit |
|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 |
| [`linux.md`](linux.md) | Linux (AppStream) | `<release><description>` | none |
| [`check_copy.py`](check_copy.py) | — | derives the France blocks, recounts, checks the limits | — |

The descriptions, names, subtitles and keywords live in
[`tools/gen_listing_copy.py`](../../../tools/gen_listing_copy.py), which splices
them into `docs/app-store-listing.md`, `docs/microsoft-store-listing.md` and
`android/RELEASING.md`. Edit the generator, never those three sections.

```
python3 docs/release-notes/2.2/check_copy.py        # derive fr-FR, recount, check limits
python3 tools/gen_listing_copy.py                   # the three listing docs
```

`NOTES_VERSION` in the generator is now `2.2`. `tools/msstore_submit.py`'s
`VERSION` and `tools/asc_publish.py`'s `ASC_NOTES_VERSION` default still say
2.1.1 / 2.1 — those are bumped at release time, per `docs/RELEASING.md`, not here.
2.2.0 derives `2.2`, which is this folder, so unlike 2.1.1 there is no second copy
to keep in step and `check_copy.py` has no sync step.

<a id="what-each-channel-may-claim"></a>
## What each channel may claim

Checked against `main` (`c196704`) and the merged pull requests, not against
issues or plans. `tests/matrix/coverage.toml` — the features×platforms map
`tools/qa/check-matrix.py` re-resolves against the tree on every push — was the
most useful single source.

| | Windows | macOS | Linux | iPhone/iPad | Android |
|---|---|---|---|---|---|
| Reading mode, sepia + night, open-in setting | yes | yes | yes | yes | yes |
| Page tools (rotate, delete, reorder, insert blank, insert from a file, extract), one undo step each | yes | yes | yes | yes | yes |
| Page thumbnails as a working pane | yes | yes | yes | yes | yes |
| Zoom anchored on the focal point | yes | yes | yes | yes | yes |
| A zoom gesture that did not exist before | — | trackpad pinch | Ctrl+wheel | — | — |
| Whiteout move/resize chrome | yes | yes | yes | **no tool at all** | **brand new** |
| Added text over more than one line | yes | yes | yes | **no** | yes |
| Long work says what it is doing | yes | yes | yes | **no** | yes |
| Stop on a long operation | search, shrink, extract | same | same | **no** | search, extract |
| A withheld permission becomes an informed choice | **no** | **no** | **no** | **no** | yes |
| Warn before a save destroys a digital signature | yes | yes | yes | yes | yes |
| A wide, titled toolbar | — | — | — | iPad | — |

PRs: reading mode #541 / #533 / #539 / #540; page tools #560 / #556 / #554 / #570
over core #430; zoom #546 / #534 / #529 / #553; whiteout and multi-line #564 /
#535 / #565; progress and Stop #568 / #563 / #580; permission #581; digital
signature #486 / #491 / #492 / #497; iPad toolbar #435.

### Four things that could not honestly be claimed

1. **iPhone and iPad get no whiteout, no multi-line added text and no
   progress-with-Stop.** That work is PR
   [#591](https://github.com/SlyWombat/MegaPDF/pull/591), still **open**.
   [#592](https://github.com/SlyWombat/MegaPDF/pull/592) is the trap: it is
   *merged*, into `wip/3-4-ios`, so `gh pr list --state merged` lists it and
   `git merge-base --is-ancestor` shows none of its commits on `main`. iOS still
   has no whiteout tool at any point, and its busy indicator is the indeterminate
   one that shipped before 2.1.1. None of it is in `app-store.md`.
2. **The informed-permission choice is Android only** (#558 → #581). Windows,
   macOS, Linux and iOS still refuse; `megapdf_security_override()` is in the core
   and unbound on the desktops. It appears in `google-play.md` and nowhere else.
3. **Linux gained Ctrl+wheel zoom, not a pinch.** Avalonia bridges a trackpad
   magnify gesture only on macOS (#534's own description says so). The Mac's pinch
   is genuinely new; Linux's is the wheel; Windows already had Ctrl+wheel and only
   its anchor was wrong.
4. **Reflow is not in 2.2** and is promised nowhere. `SDD.md` is explicit: tier 3
   was not shipped and no platform lays a document's text out to the screen. The
   only reflow-adjacent merge is #587, two engine primitives with nothing
   user-facing.

Also not claimed: *"both tiers"*. Tiers 1 and 2 are implementation tiers of the
reading-mode plan, not product tiers — there is no paid edition — so no copy says
it, because a reader would infer one.

### The claim that had to be withdrawn

Every description ended with **"It doesn't rearrange pages, run OCR, or bury you
in toolbars"** / **"Il ne réorganise pas les pages…"**. #174 made the first third
false. The closing line is now *"MegaPDF is deliberately simple: it doesn't bury
you in toolbars"* — the OCR disclaimer went with it, because a description that is
at its limit should not spend characters on what the app does not do. It was in
all four descriptions and all three generated docs; it is gone from all of them.

## Where the copy had to be cut for a limit

| Slot | Locale | Count | Limit |
|---|---|---|---|
| App Store description | `en-CA` | 3214 | 4000 |
| App Store description | `fr-CA` | 3943 | 4000 |
| App Store description | `fr` | 3941 | 4000 |
| Mac App Store description | `en-CA` | 3279 | 4000 |
| Mac App Store description | `fr-CA` | 3975 | 4000 |
| Mac App Store description | `fr` | 3981 | 4000 |
| Play full description | `en-CA` | 3249 | 4000 |
| Play full description | `fr-CA` | 3982 | 4000 |
| Play full description | `fr-FR` | 3980 | 4000 |
| Microsoft Store description | `en` | 2856 | 10000 |
| Microsoft Store description | `fr-CA` | 3542 | 10000 |
| App Store keywords | `en-CA` | 96 | 100 |
| App Store keywords | `fr-CA` | 94 | 100 |

**The Mac French description had 22 characters spare before 2.2** (3978 of 4000),
and the Play French 310. Adding two paragraphs to a full field means taking
something out of it, so this is the part to read closely.

**One claim left the Mac App Store description.** The *Protect, shrink, print*
paragraph — set/change/remove a password, save a smaller copy for email, print
through the macOS panel — is gone. All three are still in the app's menus, in
2.0's what's new, and on the website; none of them is why a person picks a PDF
app, and reading mode and the page tools are. **The trade, if you would rather
keep it:** restoring that paragraph costs about 210 French characters, which is
roughly what reading mode and the page tools would have to give back. Say which
and it is a one-line change to the generator.

**Everything else was prose, not claims.** On the Mac: the Finder double-click
moved out of *Save without fear* (it is named one paragraph below, in *At home on
the Mac*); *no account, no subscription, no cloud* left the opening paragraph,
where it duplicated *Private by design* two paragraphs down — the claim itself is
untouched and still stated at full strength in the paragraph that is about it;
*Cover is there too, when hiding it on the page is enough* lost its middle clause
but still says it only covers. Shared with iPhone and Play: *Find any word* and
*Sign like you mean it* each lost a few words of repetition. Play only: its own
cross-platform sentence no longer lists the four platforms by name, and its
password sentence is shorter.

**The Play release notes are the tight one, as always.** English 489 of 500,
French 470. The French drops the English's *Added text takes more than one line* —
the one item of the four that is a refinement rather than a new thing — and
shortens the page list. Anything added to the French has to come out of it first.

**The Microsoft Store what's new** is 1184 English, 1379 French of 1500. The
French runs about 27 % longer than the English on this field (2.1.1: 1053 → 1341),
so the English is deliberately held near 1100.

## Judgement calls worth a second opinion

1. **The Microsoft Store description now opens with `READ`.** It used to open with
   `EDIT TEXT` and say *"It does six things exceptionally well"*; it now says
   *eight* and leads with reading, because reading is the first thing anyone does
   with a PDF and it is 2.2's headline. If `EDIT TEXT` should stay first, moving
   one block is the whole change. Two feature boxes were added (14 of 20 now).
2. **`reader` / `lecture` joined the App Store keywords** (96 and 94 of 100).
   Neither list had a word for reading, and reading mode is the headline. Nothing
   was dropped to fit — `fill and sign` is still there. Revert in one line if the
   ranking is something you have tuned.
3. **Name, subtitle, promotional text and short descriptions are unchanged** on
   every channel. The subtitle is 23 of 30 characters and *Fill, check & sign
   PDFs* is still what the app is for; spending it on reading mode is a positioning
   decision rather than a copy fix, so it is left alone.
4. **The Linux channel is English only.** See [`linux.md`](linux.md): the
   metainfo translates `<name>`, `<summary>` and the screenshot captions and has
   never translated `<description>`. The 2.2 blocks follow that. Adding French for
   only the two new paragraphs, with the other seven left English, would read worse
   than either consistent answer — so this is a decision for you, not something to
   change quietly in a listing pull request.
5. **Nothing in `linux.md` has been applied to the metainfo.**
   `tools/linux/make-release-tarball.sh` requires the newest `<release>` version to
   equal the version being built, and the new description paragraphs would ship
   inside the **2.1.1** package and promise what it does not do. Both go in at the
   version bump.

## The French

**It has not been reviewed.** 2.1.1's French was reviewed on 2026-09-26 (#343),
and every 2.2 string in the app still carries its own `FR-REVIEW` comment — the
reading-preference strings (#511), the page-tool strings (#174) and the
permission strings (#558) are all new French that nobody has read yet. The
listing copy here follows `docs/localisation-glossary.md` and quotes the shipped
labels: *Mode lecture*, *Couleurs de la page*, *Normales / Sépia / Nuit*, *Le mode
nuit inverse la page, images comprises*, *Ouvrir les documents en mode lecture*,
*Vignettes*, *Présentation* (the Mac's View menu), *Paramètres* on Windows and the
phones, *Options* on the Mac, *Correcteur* / *Masquer*, *Annuler*, *Arrêter*. If a
reviewer changes a label, this copy changes with it.

France French is derived, never hand-written: `check_copy.py` applies
`to_france` plus France punctuation, exactly as `tools/gen_strings.py fr-fr` does
for the app's catalogues.

### The two senses of "signature" (#602)

The vocabulary is Adobe's and the PDF specification's: an **electronic signature**
is the broad kind and includes the drawn, typed or photographed mark this app
places; a **digital signature** is the specific kind backed by a certificate,
whose certificate and key are a **digital ID**. In the interface the placed one
stays *signature* / *sign* / *your signature*, which is what people look for, and
this copy keeps that. 2.2's actual signature work is the warning before a save
would invalidate a document's **digital signature**, and that is how it is named.

Nothing in any block says that placing a signature makes a document verifiable,
tamper-evident, certified, legally binding or secure; *protected* and *secure* are
left to the password feature, which 2.2 does not change.

**One thing to raise rather than paper over.** In French both senses are
`signature`, separated only by `numérique` — and `signature numérique` in everyday
French also reads as *a signature made on a screen*, which is the very thing it is
being contrasted with. `app-store.md` and `mac-app-store.md` lean on `celle du
chiffrement` and on calling the placed one `une image de votre nom` to break the
tie. `signature numérique` is the shipped in-app term (#476/#481), so the listing
follows the app rather than inventing a second word — but if a francophone finds
the sentence still collapses, the fix belongs in the app string first and in these
blocks second.

**And one piece of pre-existing copy worth a look while you are here.** The
description's signature heading is *Sign like you mean it* / **Signez pour de
vrai**. The English is about conviction. The French *pour de vrai* can be read as
*a signature that is legally real*, which is the inference #602 exists to prevent.
It is 2.0 copy that a francophone signed off, so it has not been changed here —
but it is the one existing sentence that the new rule would probably have worded
differently.

## Still to do before 2.2 is submitted

- A francophone (or Fable, at your instruction, as for 2.1.1) reads every French
  block here and every 2.2 `FR-REVIEW` string in the app.
- Screenshots. Reading mode and the pages pane are 2.2's headlines and no capture
  set shows either; `docs/app-store-listing.md`'s screenshot tables and
  `tools/capture-gate` are unchanged by this pull request. A listing whose images
  are all 2.1's undersells the release.
- The long form (`release-notes-2.2.md`) for the GitHub release and the website.
  `check_copy.py` will derive its France section once it exists.
- `website/megapdf/linux/index.html`'s intro names tabs, Markdown and
  `megapdf-cli` and not reading mode. It is not a store listing, so it is left
  alone here.
- The version bump, and with it `tools/msstore_submit.py`, `asc_publish.py`, the
  metainfo's `<release>` entry from [`linux.md`](linux.md), and its date.
