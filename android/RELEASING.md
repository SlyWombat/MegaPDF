# Releasing MegaPDF for Android

## App identity

| Field | Value |
|---|---|
| **Package name** (applicationId) | `ca.electricrv.megapdf` — locked in by the first AAB uploaded to Play, permanent thereafter |
| Play Console app name | MegaPDF |
| Version scheme | `versionName` = marketing (0.1.0…), `versionCode` = must increase every upload |
| Version parity | `versionName` tracks the **iOS public version**, which has no file to bump — iOS takes `MARKETING_VERSION` from the release tag (`ios-v<version>`). Bumping one platform for a shared feature means tagging the other to match: Android 1.1.0 (search/Find) expects `ios-v1.1.0`. |
| Privacy policy URL | `https://electricrv.ca/megapdf/privacy/` |
| Contact website (Store settings) | `https://electricrv.ca/megapdf/support/` — set with `tools/play.sh listing contact --website … --commit`, or by hand: Play Console → MegaPDF → Grow users → Store presence → Store settings → Store listing contact details → Website, Save (#662) |
| Contact email | `info@electricrv.ca` |
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

Main store listing → **Manage translations** → add **French (Canada) – fr-CA** and **French (France) – fr-FR** beside the default **English (Canada) – en-CA**; since #724 also **Spanish (Latin America) – es-419** and **Spanish (Spain) – es-ES**. `tools/play_submit.py` uploads binaries only; this text is pasted by hand. Every field for every language is below as a block that pastes as-is. Screenshots: the **Android Screenshots** workflow captures once per language (artifacts `play-screenshots-en`, `-fr-CA`, `-fr-FR`, `-es-419`, `-es-ES`); the French and Spanish runs set the app's per-app locale on the emulator and open that language's demo agreement.

*French copy was translated by the build assistant against `docs/localisation-glossary.md` and signed off by a francophone reviewer for 2.0 (2026-09-18, #242); new copy gets the same read before it goes live. The France variant is derived from the Canadian one by `tools/gen_listing_copy.py`.*

*Spanish copy was written by the build assistant against `docs/localisation-glossary-es.md` and is **ES-REVIEW #724: not yet read by the reviewer**. Fable's approval, recorded on #724, gates it; nothing Spanish is pasted into a store before then. The Spain variant is derived from the Latin American one by `tools/gen_listing_copy.py` (`spain()`), with no punctuation pass.*

### English (Canada) — `en-CA`

**Title** [30] (25)
```
MegaPDF: Fill & Sign PDFs
```

**Short description** [80] (49)
```
Fill, check and sign a PDF. No account, no cloud.
```

**Full description** [4000] (3249)
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
Draw your signature with a finger, type your name, or use a photo of the one on paper — the white background disappears automatically. Your signatures stay in a private library on your device; drop one on a document and move and resize it until it sits right on the line.

Save without fear
Save writes back to the original file — safely. MegaPDF checks every save first, so a failed one can never corrupt the file someone sent you. Or keep the original and save a copy. Export as Markdown writes the document's text — headings, lists, the values you filled in — as a Markdown file, and leaves the PDF as it was. You can also put a password on a document, or take one off.

Find any word
Search the whole document as you type: every match lights up and the counter says how many, so the one clause you need in a forty-page lease is a few taps away.

Read it, not the app
Reading mode takes the toolbars off the screen and leaves the page. Page colours come with it: Sepia for a long read, Night for a dark room — and Night inverts pictures too, deliberately. MegaPDF can open every document that way.

Put the pages in order
Rotate a page that came in sideways, delete one, drag pages into the right order, insert a blank page, add the pages of another PDF, or save a few pages out as their own file. Each is a single step, and one Undo puts it back.

Private by design
MegaPDF asks you for no permissions and makes no network connections. Your documents and your signature never leave your device — there is no server for them to go to. The app is open source, so anyone can verify that.

Works with everything
Open a PDF from Files, Google Drive, Gmail or any app that hands one over — MegaPDF is in their Open with list — and send it back with Share. Documents you fill and sign here open perfectly in Adobe Acrobat and in any desktop PDF app.

MegaPDF is deliberately simple: it doesn't bury you in toolbars. It opens, it fixes, it saves. Done.
```

**Release notes** [500] (116) — 2.3, from `docs/release-notes/2.3/google-play.md`
```
Find now searches scanned pages. MegaPDF reads them on your device, and nothing leaves it. Plus various other fixes.
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

**Full description** [4000] (3985)
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

Signez comme sur papier
Dessinez votre signature du doigt, tapez votre nom ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil; déposez-en une sur un document, puis déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque enregistrement d'abord : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs remplies) dans un fichier Markdown, et laisse le PDF tel quel. Vous pouvez aussi mettre un mot de passe sur un document, ou en retirer un.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez : chaque résultat s'allume et le compteur dit combien il y en a, pour que la seule clause utile dans un bail de quarante pages soit à quelques touches.

Lisez le document, pas l'application
Le mode lecture retire les barres d'outils de l'écran et laisse la page. Les couleurs de la page viennent avec : Sépia pour une longue lecture, Nuit pour une pièce sombre — et le mode nuit inverse aussi les images, à dessein. MegaPDF peut ouvrir chaque document ainsi.

Mettez les pages en ordre
Faites pivoter une page arrivée de travers, supprimez-en une, glissez les pages dans le bon ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou extrayez quelques pages dans un fichier à part. Chaque geste est une étape, et une seule annulation le défait.

Confidentiel par conception
MegaPDF ne vous demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Fichiers, Google Drive, Gmail ou toute application qui en remet un (MegaPDF est dans leur liste Ouvrir avec), et renvoyez-le avec Partager. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat et dans toute application PDF de bureau.

MegaPDF est volontairement simple : il ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Release notes** [500] (158) — 2.3, from `docs/release-notes/2.3/google-play.md`
```
La recherche fonctionne maintenant dans les pages numérisées. MegaPDF les lit sur votre appareil; rien n'est envoyé ailleurs. Et plusieurs autres corrections.
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

**Full description** [4000] (3983)
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

Signez comme sur papier
Dessinez votre signature du doigt, tapez votre nom ou utilisez une photo de celle sur papier : le fond blanc disparaît automatiquement. Vos signatures restent dans une bibliothèque privée sur votre appareil ; déposez-en une sur un document, puis déplacez-la et redimensionnez-la jusqu'à ce qu'elle soit bien sur la ligne.

Enregistrez sans crainte
Enregistrer écrit dans le fichier original, en toute sécurité. MegaPDF vérifie chaque enregistrement d'abord : un enregistrement raté ne peut jamais corrompre le fichier qu'on vous a envoyé. Ou gardez l'original et enregistrez une copie. Exporter en Markdown écrit le texte du document (titres, listes, les valeurs remplies) dans un fichier Markdown, et laisse le PDF tel quel. Vous pouvez aussi mettre un mot de passe sur un document, ou en retirer un.

Trouvez n'importe quel mot
Cherchez dans tout le document à mesure que vous tapez : chaque résultat s'allume et le compteur dit combien il y en a, pour que la seule clause utile dans un bail de quarante pages soit à quelques touches.

Lisez le document, pas l'application
Le mode lecture retire les barres d'outils de l'écran et laisse la page. Les couleurs de la page viennent avec : Sépia pour une longue lecture, Nuit pour une pièce sombre — et le mode nuit inverse aussi les images, à dessein. MegaPDF peut ouvrir chaque document ainsi.

Mettez les pages en ordre
Faites pivoter une page arrivée de travers, supprimez-en une, glissez les pages dans le bon ordre, insérez une page vierge, ajoutez les pages d'un autre PDF, ou extrayez quelques pages dans un fichier à part. Chaque geste est une étape, et une seule annulation le défait.

Confidentiel par conception
MegaPDF ne vous demande aucune permission et n'établit aucune connexion réseau. Vos documents et votre signature ne quittent jamais votre appareil : il n'y a aucun serveur où ils pourraient aller. L'application est un logiciel libre ; n'importe qui peut le vérifier.

Compatible avec tout
Ouvrez un PDF depuis Fichiers, Google Drive, Gmail ou toute application qui en remet un (MegaPDF est dans leur liste Ouvrir avec), et renvoyez-le avec Partager. Les documents remplis et signés ici s'ouvrent parfaitement dans Adobe Acrobat et dans toute application PDF de bureau.

MegaPDF est volontairement simple : il ne vous noie pas sous les barres d'outils. Il ouvre, il corrige, il enregistre. Terminé.
```

**Release notes** [500] (159) — 2.3, from `docs/release-notes/2.3/google-play.md`
```
La recherche fonctionne maintenant dans les pages numérisées. MegaPDF les lit sur votre appareil ; rien n'est envoyé ailleurs. Et plusieurs autres corrections.
```

### Español (Latinoamérica) — `es-419`

**Title** [30] (26)
```
MegaPDF: llena y firma PDF
```

**Short description** [80] (51)
```
Llena, marca y firma un PDF. Sin cuenta y sin nube.
```

**Full description** [4000] (3833)
```
Abrir. Corregir. Guardar. Listo.

MegaPDF hace el trabajo que la mayoría de la gente realmente tiene con un PDF: alguien te envió un formulario y necesitas devolverlo completado, marcado y firmado. Sin cuenta. Sin suscripción. Sin nube. Todo sucede en tu dispositivo.

Marca cualquier casilla
Toca una casilla y queda marcada, tanto en campos de formulario interactivos reales como en simples cuadros impresos. MegaPDF reconoce las casillas dibujadas que otras aplicaciones tratan como simple decoración.

Escribe en cualquier línea
Toca donde va la respuesta y escríbela. Elige el tamaño y el tipo de fuente (sans, serif o monoespaciada) para que lo que agregues combine con el formulario que estás completando. Arrástralo a su lugar, o tócalo de nuevo para corregir un error. Todo lo que agregas es texto real en el que se puede buscar, no un adhesivo pegado sobre la página.

Corrige el texto del propio documento
¿Fecha equivocada? ¿Nombre mal escrito? Toca la línea y vuelve a escribirla. MegaPDF conserva la fuente del propio documento siempre que puede y te avisa cuando tuvo que usar una parecida. Si un cambio alterara el resto de la página, te lo dice en lugar de mover cosas en silencio. Deshacer devuelve el original exactamente como estaba.

Censura, y de verdad desaparece
Selecciona lo que tiene que desaparecer (un nombre, una dirección, una imagen) y MegaPDF lo quita del archivo en lugar de taparlo. Lo que había debajo desaparece: ninguna otra aplicación puede copiarlo ni buscarlo.

Firma con convicción
Dibuja tu firma con el dedo, escribe tu nombre o usa una foto de la que tienes en papel; el fondo blanco desaparece automáticamente. Tus firmas se quedan en una biblioteca privada en tu dispositivo; coloca una en un documento y muévela y cámbiale el tamaño hasta que quede justo sobre la línea.

Guarda sin miedo
Guardar escribe sobre el archivo original, de forma segura. MegaPDF revisa primero cada guardado, así que uno fallido nunca puede corromper el archivo que alguien te envió. O conserva el original y guarda una copia. Exportar como Markdown escribe el texto del documento (títulos, listas, los valores ingresados) como archivo Markdown y deja el PDF tal como estaba. También puedes ponerle una contraseña a un documento o quitársela.

Encuentra cualquier palabra
Busca en todo el documento mientras escribes: cada coincidencia se ilumina y el contador dice cuántas hay, así que la única cláusula que necesitas en un contrato de arrendamiento de cuarenta páginas está a unos toques de distancia.

Lee el documento, no la aplicación
El modo de lectura quita las barras de herramientas de la pantalla y deja solo la página. Con él vienen los colores de página: Sepia para una lectura larga, Noche para una habitación a oscuras; y Noche invierte también las imágenes, deliberadamente. MegaPDF puede abrir todos los documentos de esa forma.

Pon las páginas en orden
Gira una página que llegó de lado, elimina una, arrastra las páginas al orden correcto, inserta una página en blanco, agrega las páginas de otro PDF o guarda algunas páginas como su propio archivo. Cada acción es un solo paso, y un solo Deshacer la revierte.

Privado por diseño
MegaPDF no te pide ningún permiso y no realiza ninguna conexión de red. Tus documentos y tu firma nunca salen de tu dispositivo: no hay ningún servidor al que puedan ir. La aplicación es de código abierto, así que cualquiera puede verificarlo.

Funciona con todo
Abre un PDF desde Archivos, Google Drive, Gmail o cualquier aplicación que te entregue uno (MegaPDF está en su lista Abrir con) y devuélvelo con Compartir. Los documentos que completas y firmas aquí se abren perfectamente en Adobe Acrobat y en cualquier aplicación de PDF de escritorio.

MegaPDF es deliberadamente simple: no te entierra en barras de herramientas. Abre, corrige, guarda. Listo.
```

**Release notes** [500] (129) — 2.3, from `docs/release-notes/2.3/google-play.md`
```
PENDING: the Español (Latinoamérica) release notes are written with docs/release-notes/2.3/google-play.md at release time (#724).
```

### Español (España) — `es-ES`

**Title** [30] (28)
```
MegaPDF: rellena y firma PDF
```

**Short description** [80] (53)
```
Rellena, marca y firma un PDF. Sin cuenta y sin nube.
```

**Full description** [4000] (3826)
```
Abrir. Corregir. Guardar. Listo.

MegaPDF hace el trabajo que la mayoría de la gente realmente tiene con un PDF: alguien te envió un formulario y necesitas devolverlo completado, marcado y firmado. Sin cuenta. Sin suscripción. Sin nube. Todo sucede en tu dispositivo.

Marca cualquier casilla
Toca una casilla y queda marcada, tanto en campos de formulario interactivos reales como en simples cuadros impresos. MegaPDF reconoce las casillas dibujadas que otras aplicaciones tratan como simple decoración.

Escribe en cualquier línea
Toca donde va la respuesta y escríbela. Elige el tamaño y el tipo de fuente (sans, serif o monoespaciada) para que lo que añadas combine con el formulario que estás completando. Arrástralo a su lugar, o tócalo de nuevo para corregir un error. Todo lo que añades es texto real en el que se puede buscar, no un adhesivo pegado sobre la página.

Corrige el texto del propio documento
¿Fecha equivocada? ¿Nombre mal escrito? Toca la línea y vuelve a escribirla. MegaPDF conserva la fuente del propio documento siempre que puede y te avisa cuando tuvo que usar una parecida. Si un cambio alterara el resto de la página, te lo dice en lugar de mover cosas en silencio. Deshacer devuelve el original exactamente como estaba.

Censura, y de verdad desaparece
Selecciona lo que tiene que desaparecer (un nombre, una dirección, una imagen) y MegaPDF lo quita del archivo en lugar de taparlo. Lo que había debajo desaparece: ninguna otra aplicación puede copiarlo ni buscarlo.

Firma con convicción
Dibuja tu firma con el dedo, escribe tu nombre o usa una foto de la que tienes en papel; el fondo blanco desaparece automáticamente. Tus firmas se quedan en una biblioteca privada en tu dispositivo; coloca una en un documento y muévela y cámbiale el tamaño hasta que quede justo sobre la línea.

Guarda sin miedo
Guardar escribe sobre el archivo original, de forma segura. MegaPDF revisa primero cada guardado, así que uno fallido nunca puede corromper el archivo que alguien te envió. O conserva el original y guarda una copia. Exportar como Markdown escribe el texto del documento (títulos, listas, los valores introducidos) como archivo Markdown y deja el PDF tal como estaba. También puedes ponerle una contraseña a un documento o quitársela.

Encuentra cualquier palabra
Busca en todo el documento mientras escribes: cada coincidencia se ilumina y el contador dice cuántas hay, así que la única cláusula que necesitas en un contrato de alquiler de cuarenta páginas está a unos toques de distancia.

Lee el documento, no la aplicación
El modo de lectura quita las barras de herramientas de la pantalla y deja solo la página. Con él vienen los colores de página: Sepia para una lectura larga, Noche para una habitación a oscuras; y Noche invierte también las imágenes, deliberadamente. MegaPDF puede abrir todos los documentos de esa forma.

Pon las páginas en orden
Gira una página que llegó de lado, elimina una, arrastra las páginas al orden correcto, inserta una página en blanco, añade las páginas de otro PDF o guarda algunas páginas como su propio archivo. Cada acción es un solo paso, y un solo Deshacer la revierte.

Privado por diseño
MegaPDF no te pide ningún permiso y no realiza ninguna conexión de red. Tus documentos y tu firma nunca salen de tu dispositivo: no hay ningún servidor al que puedan ir. La aplicación es de código abierto, así que cualquiera puede verificarlo.

Funciona con todo
Abre un PDF desde Archivos, Google Drive, Gmail o cualquier aplicación que te entregue uno (MegaPDF está en su lista Abrir con) y devuélvelo con Compartir. Los documentos que completas y firmas aquí se abren perfectamente en Adobe Acrobat y en cualquier aplicación de PDF de escritorio.

MegaPDF es deliberadamente simple: no te entierra en barras de herramientas. Abre, corrige, guarda. Listo.
```

**Release notes** [500] (122) — 2.3, from `docs/release-notes/2.3/google-play.md`
```
PENDING: the Español (España) release notes are written with docs/release-notes/2.3/google-play.md at release time (#724).
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
edit with the sources. The public contact details are app-level, not per release:
`tools/play.sh listing contact` reads them and says whether the website is the support page;
`contact --website https://electricrv.ca/megapdf/support/ --commit` sets it (a details edit needs an email, so the tool fills
`info@electricrv.ca` in when the read showed it empty). **If the API PATCH is refused, or the
read-back is still empty after a commit,** do it in the Console: Play Console → MegaPDF →
**Grow users → Store presence → Store settings → Store listing contact details** → **Website** =
`https://electricrv.ca/megapdf/support/`, **Email** = `info@electricrv.ca`, Save, then read back with `tools/play.sh listing contact`
(#662). By hand, the API calls are:
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
