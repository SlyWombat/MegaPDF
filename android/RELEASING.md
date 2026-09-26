# Releasing MegaPDF for Android

## App identity

| Field | Value |
|---|---|
| **Package name** (applicationId) | `ca.electricrv.megapdf` — locked in by the first AAB uploaded to Play, permanent thereafter |
| Play Console app name | MegaPDF |
| Version scheme | `versionName` = marketing (0.1.0…), `versionCode` = must increase every upload |
| Version parity | `versionName` tracks the **iOS public version**, which has no file to bump — iOS takes `MARKETING_VERSION` from the release tag (`ios-v<version>`). Bumping one platform for a shared feature means tagging the other to match: Android 1.1.0 (search/Find) expects `ios-v1.1.0`. |
| Privacy policy URL | `https://electricrv.ca/megapdf/privacy/` |
| Data safety | Nothing collected, nothing shared; no ads; zero permissions |
| Sibling ids for reference | iOS `com.megapdf.ios` (+ diag `ca.electricrv.megapdf`) · SlyLED `ca.electricrv.slyled` · SlyTab `ca.electricrv.slytab` |

## Signing model

- **Play App Signing** holds the release key (enroll during first upload — it's
  the default). We sign bundles with an **upload key**, which Google can reset
  if lost.
- Upload keystore: `C:\Users\DavidSeaman\.megapdf-keys\upload-keystore.jks`
  (alias `upload`; password in `key-info.txt` next to it — back both up to a
  password manager). CI has it as the repo secrets
  `ANDROID_UPLOAD_KEYSTORE_B64` / `ANDROID_UPLOAD_KEYSTORE_PASSWORD` /
  `ANDROID_UPLOAD_KEY_ALIAS`.

## Cutting a release

1. Bump `versionCode` (must increase every Play upload) and `versionName` in
   `android/app/build.gradle.kts`; commit on `main`.
2. Tag and push: `git tag android-v0.1.0 && git push origin android-v0.1.0`.
3. `android-release.yml` runs unit tests, builds the signed AAB, and attaches
   it to a GitHub release for the tag. Download `app-release.aab` from there.

## First-time Play Console setup (once)

1. [play.google.com/console](https://play.google.com/console) → **Create app**
   (name *MegaPDF*, app, free).
2. **Internal testing** → create release → upload `app-release.aab` → accept
   Play App Signing enrollment.
3. **Testers**: create an email list (start with the Windows testers) and share
   the opt-in link.
4. Required declarations:
   - **Privacy policy:** `https://electricrv.ca/megapdf/privacy/` — the
     Electric RV-hosted policy (issue #25; must be live before submission).
     The interim GitHub Pages policy retires once it is.
   - **Data safety:** no data collected, no data shared — documents and
     signatures never leave the device; the app makes no network calls and
     declares zero permissions.
   - **Content rating:** questionnaire → utility, no user-generated content.
   - **Ads:** none.
5. Roll out the internal-testing release.

<!-- copy-by-language -->
## Play listing — copy by language

Main store listing → **Manage translations** → add **French (Canada) – fr-CA** and **French (France) – fr-FR** beside the default **English (Canada) – en-CA**. `tools/play_submit.py` uploads binaries only; this text is pasted by hand. Every field for every language is below as a block that pastes as-is. Screenshots: the **Android Screenshots** workflow captures once per language (artifacts `play-screenshots-en`, `-fr-CA`, `-fr-FR`); the French runs set the app's per-app locale on the emulator and open the French demo agreement.

*French copy was translated by the build assistant against `docs/localisation-glossary.md` and signed off by a francophone reviewer for 2.0 (2026-09-18, #242); new copy gets the same read before it goes live. The France variant is derived from the Canadian one by `tools/gen_listing_copy.py`.*

### English (Canada) — `en-CA`

**Title** [30] (25)
```
MegaPDF: Fill & Sign PDFs
```

**Short description** [80] (49)
```
Fill, check and sign a PDF. No account, no cloud.
```

**Full description** [4000] (2934)
```
Open. Fix. Save. Done.

MegaPDF does the one job most people actually have with a PDF: someone sent you a form, and you need to send it back filled in, checked off, and signed. No account. No subscription. No cloud. Everything happens on your device.

Check any box
Tap a checkbox and it's checked — real interactive form fields and plain printed squares alike. MegaPDF recognizes drawn checkboxes that other apps treat as decoration.

Type on any line
Tap where the answer goes and type it. Choose the size and the face — sans, serif or monospace — so what you add matches the form you are filling in. Drag it into place, or tap it again to fix a typo. Everything you add is real, searchable text, not a sticker on top of the page.

Fix the document's own text
Wrong date? Misspelled name? Tap the line and retype it. MegaPDF keeps the document's own font where it can and tells you when it had to use a similar one. If a change would disturb the rest of the page, it says so instead of quietly moving things. Undo puts the original back exactly.

Redact, and it really is gone
Mark what has to come out — a name, an address, a picture — and MegaPDF takes it out of the file rather than covering it over. What was underneath is gone: no other app can copy it or search for it.

Sign like you mean it
Draw your signature with a finger, type your name, or use a photo of the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one onto any document, move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF checks every save before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy — as a PDF, or as a Markdown file of the document's text (headings, lists, the values you filled in) that leaves the PDF as it was. You can also protect a document with a password, or remove one you know.

Find any word
Search the whole document as you type. Every match lights up and the counter tells you how many there are, so the one clause you need in a forty-page lease is a few taps away.

Private by design
MegaPDF asks you for no permissions and makes no network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open a PDF from Files, Google Drive, Gmail or any app that hands one over — MegaPDF is in their Open with list — or through MegaPDF's own file picker, and send it back with Share. Documents you fill and sign here open perfectly in Adobe Acrobat, desktop PDF apps, and MegaPDF for Windows, Mac and iOS — same engine, same result, on every platform.

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.
```

**Release notes** [500] (486) — 2.1.1, from `docs/release-notes/2.1.1/google-play.md`
```
Open with: MegaPDF is now offered when you open a PDF from Files, Drive, Gmail or any app that hands one over.

Share, in the More menu, sends the document through the share sheet. If it has unsaved changes it offers Save, Share without saving, or Cancel.

Save a copy can now write Markdown as well as PDF: the document's text, headings, lists and filled-in values, as a file you can paste anywhere. An export, not a save — the PDF is untouched, and a scanned page says it has no text.
```

### Français (Canada) — `fr-CA`

**Title** [30] (27)
```
MegaPDF : remplir et signer
```

**Short description** [80] (68)
```
Remplir, cocher et signer un PDF. Pas de compte, pas d'infonuagique.
```

**Full description** [4000] (3673)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date? Nom mal orthographié? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

Signez pour de vrai
Dessinez votre signature du doigt, tapez votre nom ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque enregistrement avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie, en PDF ou dans un fichier Markdown du texte du document (titres, listes, les valeurs que vous avez remplies) qui laisse le PDF tel quel. Vous pouvez aussi protéger un document par un mot de passe, ou retirer un mot de passe que vous connaissez.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne vous demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Fichiers, Google Drive, Gmail ou toute application qui en remet un (MegaPDF est dans leur liste Ouvrir avec), ou avec le sélecteur de fichiers de MegaPDF, et renvoyez-le avec Partager. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat, dans les applications PDF de bureau, et dans MegaPDF pour Windows, Mac et iOS : même moteur, même résultat, sur toutes les plateformes.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Release notes** [500] (490) — 2.1.1, from `docs/release-notes/2.1.1/google-play.md`
```
Ouvrir avec : MegaPDF est proposé quand vous ouvrez un PDF depuis Fichiers, Drive, Gmail ou une autre application.

Partager, dans le menu Plus, envoie le document par la feuille de partage. Avec des modifications non enregistrées : Enregistrer, Partager sans enregistrer ou Annuler.

Enregistrer une copie écrit maintenant en Markdown aussi bien qu'en PDF : texte, titres, listes et valeurs remplies, à coller n'importe où. Une exportation, pas un enregistrement : le PDF n'est pas touché.
```

### Français (France) — `fr-FR`

**Title** [30] (27)
```
MegaPDF : remplir et signer
```

**Short description** [80] (62)
```
Remplir, cocher et signer un PDF. Pas de compte, pas de cloud.
```

**Full description** [4000] (3671)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas de cloud. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Corrigez le texte du document
Mauvaise date ? Nom mal orthographié ? Touchez la ligne et retapez-la. MegaPDF garde la police du document quand il le peut et vous prévient quand il a dû en utiliser une semblable. Si une modification devait perturber le reste de la page, il vous le dit plutôt que de déplacer les choses en silence. Annuler remet l'original exactement.

Caviardez, et c'est parti pour de bon
Marquez ce qui doit disparaître — un nom, une adresse, une image — et MegaPDF le retire du fichier au lieu de le recouvrir. Ce qui était dessous n'y est plus : aucune autre application ne peut le copier ni le retrouver par une recherche.

Signez pour de vrai
Dessinez votre signature du doigt, tapez votre nom ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil ; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque enregistrement avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie, en PDF ou dans un fichier Markdown du texte du document (titres, listes, les valeurs que vous avez remplies) qui laisse le PDF tel quel. Vous pouvez aussi protéger un document par un mot de passe, ou retirer un mot de passe que vous connaissez.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne vous demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Fichiers, Google Drive, Gmail ou toute application qui en remet un (MegaPDF est dans leur liste Ouvrir avec), ou avec le sélecteur de fichiers de MegaPDF, et renvoyez-le avec Partager. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat, dans les applications PDF de bureau, et dans MegaPDF pour Windows, Mac et iOS : même moteur, même résultat, sur toutes les plateformes.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Release notes** [500] (490) — 2.1.1, from `docs/release-notes/2.1.1/google-play.md`
```
Ouvrir avec : MegaPDF est proposé quand vous ouvrez un PDF depuis Fichiers, Drive, Gmail ou une autre application.

Partager, dans le menu Plus, envoie le document par la feuille de partage. Avec des modifications non enregistrées : Enregistrer, Partager sans enregistrer ou Annuler.

Enregistrer une copie écrit maintenant en Markdown aussi bien qu'en PDF : texte, titres, listes et valeurs remplies, à coller n'importe où. Une exportation, pas un enregistrement : le PDF n'est pas touché.
```

<!-- /copy-by-language -->

## Headless Play submission (one-time service-account setup)

With a Play service account, releases become tag-only: CI (or Claude via the
Play Developer API) uploads the AAB and rolls out the internal track — no
console visit. Setup (adapted from SlyTab's `docs/private/android-play-setup.md`):

1. Play Console → **Setup → API access** → link (or create) a Google Cloud
   project → **Create service account** (opens Google Cloud Console).
2. In Google Cloud: create the service account (name e.g. `play-publisher`),
   no special GCP roles needed. Then **Keys → Add key → JSON** and download.
3. Back in Play Console → API access → the new account → **Grant access** →
   role **Release manager**, scope **account-level** (covers MegaPDF, SlyTab,
   and future apps).
4. Save the JSON as `SplitWise\secrets\play-service-account.json` (the
   documented shared location — gitignored there; never commit it anywhere).
   Optionally also `gh secret set PLAY_SERVICE_ACCOUNT_JSON < file` in this
   repo so `android-release.yml` can gain a `play-submit` step.
**Status: DONE (2026-08-09).** The service account is live
(`play-publisher@electricrv-play.iam.gserviceaccount.com`, Admin), the key
sits at the path above and in the repo secret `PLAY_SERVICE_ACCOUNT_JSON`,
and `android-release.yml` submits every tagged release to the internal track
automatically via `tools/play_submit.py`. The 0.1.1 first release went
through the API end to end — including the first-bundle upload, which
worked fine against the console-created app record.

## Subsequent releases

Bump `versionCode` and `versionName` → push a tag `android-v<version>` on **main**
→ `android-release.yml` builds the signed AAB, attaches it to a GitHub release and
puts it on the **internal** track. Nothing else is automatic.

**Promoting to production is a separate, deliberate step** — `play_submit.py` only
ever touches `internal`. `tools/play_listing.py` does it in one edit: the listing text per language from the
copy-by-language section above, the phone and 10-inch tablet screenshots from a capture
folder, and the production release with its notes (`push <captures> --production <vc>`
validates and discards; add `--commit` to send it), then `readback` compares a fresh
edit with the sources. By hand, the API calls are:
create an edit, `PUT /edits/{id}/tracks/production` with
`{"releases":[{"versionCodes":["<vc>"],"status":"completed","releaseNotes":[…]}]}`,
then `POST /edits/{id}:commit`. Service-account key at
`SlyTab/secrets/play-service-account.json`; run it with `/usr/bin/python3` (the
Windows Python shim ships a broken `cryptography`).

Reading state is safe: `edits.insert` → `tracks.list` → `edits.delete` changes
nothing. **The API cannot tell you the review verdict** — track `status=completed`
describes the rollout, not Google's decision. Only Play Console → Publishing
overview shows whether a release is in review, approved or rejected; a 404 on
`https://play.google.com/store/apps/details?id=ca.electricrv.megapdf` means it is
not public yet.

### Release log

| Version | vc | Track | Notes |
|---|---|---|---|
| 0.1.1 | 2 | internal | first API upload end to end |
| 1.0.0 | 4 | production | submitted 2026-08-09 for Google's first-app review; **shipped the default Android launcher icon** (no mipmap resources existed) |
| 1.1.2 | 7 | production | promoted 2026-08-14, replacing vc4 while that review was still pending — accepted a likely review restart to avoid a first public release with a placeholder icon and misplaced highlights. Adds search, the real launcher icon, the CropBox coordinate fix (#28) and scroll-to-hit. |
| 1.2.0 | 8 | production | tagged 2026-08-29 (internal via the pipeline); **promoted to production 2026-09-04** via the API, full rollout, en-CA notes from `docs/release-notes-mobile-1.2.md`. vc7 had gone public by then (listing live, 2026-09-04 check). Adds undo/redo, Add text (#34), text-box drag (#36), text faces (#43), and targets API 36 (#40) — which restores visibility on Android 16 devices after the 31 Aug deadline. |
| 2.0.0 | 9 | production | tagged 2026-09-19 at `b9327b2` (internal via the pipeline); **promoted to production 2026-09-19 07:39 EDT** with `tools/play_listing.py` (edit 02001940654542014226), full rollout on approval (Dave: "set everything to go live"). Same edit: title/short/full description in en-CA, fr-CA, fr-FR from the copy above, and 8 phone + 8 ten-inch tablet screenshots per language (viewer, text-edit, text, search, sign, draw, home, redact), replacing the four 1.x phone images. Read back identical. The 9:20 phone images and the 1600×2560 tablet images were accepted. |
| 2.0.1 | 10 | production | tagged 2026-09-19 at `2b018a9` (internal via the pipeline, run 35442678152); **replaced vc 9 in the production release 2026-09-19 08:27 EDT** (edit 04875081465951832807, `play_listing.py push --text-only --production 10`), full rollout on approval, same release notes. Fixes Save after a restart: only the read grant was persisted, so a document reopened from Recents after a reboot refused Save (`tools/android-qa/save_after_reboot.py`: FAIL on vc 9, PASS on vc 10). Earlier the same morning (edit 15589575813950404234) the Play copy was corrected after the independent audit: no Gmail/share-sheet or camera claims, "asks you for no permissions", typed signatures, passwords, the Mac, and the title "MegaPDF: Fill & Sign PDFs" again. |
