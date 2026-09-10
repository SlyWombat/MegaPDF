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

Main store listing → **Manage translations** → add **French (Canada) – fr-CA** and **French (France) – fr-FR** beside the default **English (Canada) – en-CA**. `tools/play_submit.py` uploads binaries only; this text is pasted by hand. Every field for every language is below as a block that pastes as-is. Screenshots: `dist/play-assets/screenshots/` per language once a French set exists; the English set otherwise.

*French copy was translated by the build assistant against `docs/localisation-glossary.md`; have a francophone read it before it goes live. The France variant is derived from the Canadian one by `tools/gen_listing_copy.py`.*

### English (Canada) — `en-CA`

**Title** [30] (7)
```
MegaPDF
```

**Short description** [80] (49)
```
Fill, check and sign a PDF. No account, no cloud.
```

**Full description** [4000] (2059)
```
Open. Fix. Save. Done.

MegaPDF does the one job most people actually have with a PDF: someone sent you a form, and you need to send it back filled in, checked off, and signed. No account. No subscription. No cloud. Everything happens on your device.

Check any box
Tap a checkbox and it's checked — real interactive form fields and plain printed squares alike. MegaPDF recognizes drawn checkboxes that other apps treat as decoration.

Type on any line
Tap where the answer goes and type it. Choose the size and the face — sans, serif or monospace — so what you add matches the form you are filling in. Drag it into place, or tap it again to fix a typo. Everything you add is real, searchable text, not a sticker on top of the page.

Sign like you mean it
Draw your signature with a finger, or photograph the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one onto any document, move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF verifies every document before it touches your original, so a failed save can never corrupt the file someone sent you. Or keep the original and save a copy.

Find any word
Search the whole document as you type. Every match lights up and the counter tells you how many there are, so the one clause you need in a forty-page lease is a few taps away.

Private by design
MegaPDF requests zero permissions and makes zero network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open PDFs from Gmail, Files, Google Drive, or any app that shares files. Documents you fill and sign here open perfectly in Adobe Acrobat, desktop PDF apps, and MegaPDF for Windows and iOS — same engine, same result, on every platform.

MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or bury you in toolbars. It opens, it fixes, it saves. Done.
```

**Release notes** [500] (154)
```
MegaPDF now speaks French. The app follows your device language; on Android 13 and later you can also pick one under Settings → Apps → MegaPDF → Language.
```

### Français (Canada) — `fr-CA`

**Title** [30] (7)
```
MegaPDF
```

**Short description** [80] (68)
```
Remplir, cocher et signer un PDF. Pas de compte, pas d'infonuagique.
```

**Full description** [4000] (2637)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas d'infonuagique. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez des PDF depuis Gmail, Fichiers, Google Drive ou toute application qui partage des fichiers. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat, dans les applications PDF de bureau, et dans MegaPDF pour Windows et iOS : même moteur, même résultat, sur toutes les plateformes.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Release notes** [500] (186)
```
MegaPDF parle maintenant français. L'application suit la langue de votre appareil; sur Android 13 et plus, vous pouvez aussi la choisir dans Paramètres → Applications → MegaPDF → Langue.
```

### Français (France) — `fr-FR`

**Title** [30] (7)
```
MegaPDF
```

**Short description** [80] (62)
```
Remplir, cocher et signer un PDF. Pas de compte, pas de cloud.
```

**Full description** [4000] (2633)
```
Ouvrir. Corriger. Enregistrer. Terminé.

MegaPDF fait la seule chose que la plupart des gens ont vraiment à faire avec un PDF : quelqu'un vous a envoyé un formulaire, et vous devez le renvoyer rempli, coché et signé. Pas de compte. Pas d'abonnement. Pas de cloud. Tout se passe sur votre appareil.

Cochez n'importe quelle case
Touchez une case et elle est cochée : les vrais champs de formulaire interactifs comme les simples carrés imprimés. MegaPDF reconnaît les cases dessinées que d'autres applications prennent pour de la décoration.

Écrivez sur n'importe quelle ligne
Touchez l'endroit où va la réponse et tapez-la. Choisissez la taille et la police (sans empattement, avec empattement ou à chasse fixe) pour que votre ajout s'accorde au formulaire. Glissez-le en place, ou touchez-le de nouveau pour corriger une coquille. Tout ce que vous ajoutez est du vrai texte, dans lequel on peut chercher, pas un autocollant posé sur la page.

Signez pour de vrai
Dessinez votre signature du doigt, ou photographiez celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil ; déposez-en une sur n'importe quel document, déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque document avant de toucher à votre original : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez. Chaque résultat s'allume et le compteur vous dit combien il y en a, pour que la seule clause dont vous avez besoin dans un bail de quarante pages soit à quelques touches.

Confidentiel par conception
MegaPDF ne demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez des PDF depuis Gmail, Fichiers, Google Drive ou toute application qui partage des fichiers. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat, dans les applications PDF de bureau, et dans MegaPDF pour Windows et iOS : même moteur, même résultat, sur toutes les plateformes.

MegaPDF est volontairement simple. Il ne réorganise pas les pages, ne fait pas de reconnaissance de caractères et ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Release notes** [500] (187)
```
MegaPDF parle maintenant français. L'application suit la langue de votre appareil ; sur Android 13 et plus, vous pouvez aussi la choisir dans Paramètres → Applications → MegaPDF → Langue.
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
ever touches `internal`. Either use the Play Console, or drive the API directly:
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
