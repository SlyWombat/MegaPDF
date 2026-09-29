# Localisation glossary — English → French (Canada)

The one vocabulary every platform's catalogue follows. Same English concept,
same French word on Windows, macOS, Android and iOS. Add a row before you coin a
new term; a term that exists here is not re-translated per platform.

Conventions (OQLF, Canadian French):

- Non-breaking space (U+00A0) before `:` only — none before `?`, `!` or `;`.
- Quotation marks are guillemets with non-breaking spaces inside: `« {0} »`.
- *Courriel*, never *e-mail*; *enregistrer*, never *sauvegarder*.
- Sentence case for buttons and titles, as in the English.
- Product and brand names stay as they are: MegaPDF, Electric RV,
  Mega Woman, GitHub, Apache-2.0, Helvetica, Times, Courier.
- Language names are written in their own language: *English*, *Français (Canada)*.
- Units: *Mo* for MB. Numbers take the platform's culture formatting (`1,4 Mo`).
- Desktop says *cliquez*, mobile says *touchez*.

| English | Français (Canada) | Notes |
|---|---|---|
| Open | Ouvrir | |
| Open a PDF | Ouvrir un PDF | |
| Open PDF… | Ouvrir un PDF… | #172, the iPad keyboard command (⌘O) in iPadOS 26's menu bar and the ⌘ overlay; the ellipsis because a picker follows |
| Save | Enregistrer | |
| Save As | Enregistrer sous | |
| Save a copy | Enregistrer une copie | |
| Save a smaller copy for email | Enregistrer une copie réduite pour courriel | |
| Shrink | Réduire | toolbar label |
| Shrink for email | Réduire pour courriel | UIA name |
| Print | Imprimer | |
| printer | imprimante | |
| network | réseau | macOS Local Network prompt (Info.plist) |
| Scroll up, down, left, right | Défiler vers le haut, le bas, la gauche, la droite | #144, accessible names of a scroll bar's arrow buttons |
| Page up, down, left, right | Une page vers le haut, le bas, la gauche, la droite | #144, accessible names of a scroll bar's track either side of the thumb |
| Undo | Annuler | |
| Redo | Rétablir | |
| Cancel | Annuler | same word as Undo; standard on every French OS |
| Continue | Continuer | |
| OK | OK | |
| Close | Fermer | |
| Done | Terminé | |
| Add | Ajouter | |
| Clear | Effacer | |
| Delete | Supprimer | |
| Delete (the key) | Suppr (Windows) / Supprimer (Mac) | the key's own name on each keyboard: a French Windows keycap reads *Suppr*, and « Supprimer la retire » would read as *deleting removes it* (#242) |
| Remove | Retirer | from a library or a page |
| Discard | Abandonner | |
| Share | Partager | #378, the OS share sheet row in the More menu |
| Share without saving | Partager sans enregistrer | #378, the unsaved-changes dialog's Share-flow button — deliberately not "Discard"/"Abandonner", since nothing is discarded (Fable review, 2026-09-26) |
| Couldn't share. Try again. | Impossible de partager. Réessayez. | #378, Android |
| share sheet | feuille de partage | #378, store copy only: Apple's own French for the iOS share sheet, and the phrase Google's French uses for Android's Sharesheet; neither app has the words as a string |
| Open with | Ouvrir avec | #376, #377, store copy only: the OS's own menu (Android's chooser, iOS's Files and Mail) that now offers MegaPDF; not an app string |
| tab | onglet | #348, the noun: Windows, Mac, Linux and every store block |
| Close tab | Fermer l'onglet | #348; the Mac menu writes it Close Tab, in title case, and the French does not change |
| Close window | Fermer la fenêtre | #348 |
| New window | Nouvelle fenêtre | #348 |
| Show Next Tab / Show Previous Tab | Afficher l'onglet suivant / Afficher l'onglet précédent | #348, macOS Window menu |
| {0}, tab | {0}, onglet | #348, the accessible name of a tab |
| {0}, unsaved changes, tab | {0}, modifications non enregistrées, onglet | #348, the same with the unsaved dot |
| Box to tick / Box, ticked | Case à cocher / Case cochée | #2, Windows: what a screen reader says for a drawn box before and after it is ticked |
| Don't save | Ne pas enregistrer | |
| Restore | Restaurer | |
| Signatures | Signatures | |
| Your signatures | Vos signatures | |
| Sign | Signer | |
| Draw | Dessiner | |
| Draw your signature | Dessinez votre signature | |
| Type your signature | Tapez votre signature | |
| Add from image… | Ajouter à partir d'une image… | |
| Photos | Photos | iOS picker |
| Signature name | Nom de la signature | |
| My signature | Ma signature | default name |
| Remove from library | Retirer de la bibliothèque | |
| Remove from Recent | Retirer des récents | #165 |
| Whiteout | Correcteur | the toolbar noun |
| Cover | Masquer | macOS verb |
| Cover an area with whiteout | Masquer une zone avec du correcteur | |
| Add text | Ajouter du texte | |
| Edit text | Modifier le texte | |
| Text | Texte | |
| Size | Taille | |
| Font | Police | |
| Find | Rechercher | |
| Search | Rechercher | |
| More | Plus | overflow menu: phones and the desktop toolbars (#143, #144); *Plus d'options* where the label is "More options" |
| Find in document | Rechercher dans le document | |
| No results | Aucun résultat | |
| Not found | Introuvable | |
| {0} of {1} | {0} sur {1} | match counter and page counter |
| Page {0} of {1} | Page {0} sur {1} | |
| Extract text | Extraire le texte | #142, #168, #356 — `megapdf-cli extract`; not yet a UI string, added ahead so #168's reflow view reuses it |
| Markdown | Markdown | #142, #168, #356 — `megapdf-cli --format md`; unchanged in French, as elsewhere on the platforms (product/brand-name-like technical terms) |
| Export as Markdown | Exporter en Markdown | #386, #409 — the phones' menu row beside Save a copy (iOS since #386, Android since #409) and their redact-confirmation dialog; the desktops' Save As Markdown option; a one-way, lossy text export, not a Save-a-copy PDF variant — the word is *Exporter*, not *Enregistrer*, on every platform |
| Markdown document | Document Markdown | #386, macOS/Linux Save As picker's Markdown file-type choice — "Markdown" itself unchanged, as above |
| Exported | Exporté | #386, the Markdown export's completion message — deliberately not "Saved"/"Enregistré" |
| Exported as Markdown | Exporté en Markdown | #386, Android's completion message |
| Couldn't export as Markdown. | Impossible d'exporter en Markdown. | #386, Android |
| Exported {0}. | {0} exporté. | #386, macOS/Linux's parameterized form of the same completion message, filename included |
| Exporting… | Exportation… | #386, busy strip while a Markdown export is written |
| Couldn't prepare the export. | Impossible de préparer l'exportation. | #386, Markdown export failure — the export-side twin of "Couldn't prepare the copy." |
| Couldn't export the text. | Impossible d'exporter le texte. | #386 |
| Could not export. | Impossible d'exporter. | #386, macOS/Linux's generic form of the same export-failure family — deliberately not "Could not save."/"Impossible d'enregistrer." |
| Next match | Résultat suivant | |
| Previous match | Résultat précédent | |
| Zoom in | Zoom avant | |
| Zoom out | Zoom arrière | |
| Fit width | Ajuster à la largeur | |
| Fit page | Page entière | |
| Actual size | Taille réelle | |
| Zoom and fit | Zoom et ajustement | |
| Zoom level | Niveau de zoom | #144, the toolbar's zoom menu button |
| {0}% | {0} % | #144, a zoom level; non-breaking space (U+00A0) before % |
| File, Edit, View, Tools | Fichier, Édition, Présentation, Outils | #144, macOS menu bar: the names macOS itself uses in French |
| Ctrl+Plus, Ctrl+Minus | Ctrl+Plus, Ctrl+Moins | #144, Windows zoom shortcuts in tooltips; Shift is *Maj* |
| Settings | Paramètres | Windows |
| Options | Options | macOS |
| Checkbox mark | Marque des cases à cocher | |
| Cross | Croix | |
| Check | Crochet | ✓ is a *crochet* in Canada |
| Filled square | Carré plein | |
| Theme | Thème | |
| Use system setting | Selon le système | |
| Light | Clair | |
| Dark | Sombre | |
| Language | Langue | |
| Use Windows setting | Selon Windows | |
| Reopen last file at startup | Rouvrir le dernier fichier au démarrage | |
| Make marks & signatures permanent when saving | Rendre les marques et signatures permanentes à l'enregistrement | |
| Flatten when saving | Aplatir à l'enregistrement | macOS |
| Check for updates at startup | Vérifier les mises à jour au démarrage | |
| About MegaPDF | À propos de MegaPDF | Windows Settings panel |
| Version {0} | Version {0} | |
| Free & open source · Apache-2.0 | Libre et à code source ouvert · Apache-2.0 | |
| Copyright © 2026 ElectricRV.ca Corporation. All rights reserved. | © 2026 ElectricRV.ca Corporation. Tous droits réservés. | |
| Special thanks to Mega Woman. | Merci tout spécial à Mega Woman. | |
| Third-party notices | Avis de tiers | |
| Recent | Récents | |
| Drag a PDF here, or click Open | Glissez un PDF ici ou cliquez sur Ouvrir | |
| Open. Fix. Save. Done. | Ouvrir. Corriger. Enregistrer. Terminé. | tagline |
| Password | Mot de passe | |
| Password required | Mot de passe requis | |
| That password wasn't right — try again. | Ce mot de passe est incorrect. Réessayez. | |
| "{0}" is protected. Enter its password to open it. | « {0} » est protégé. Entrez son mot de passe pour l'ouvrir. | |
| Owner password | Mot de passe du propriétaire | #131 |
| Unlock | Déverrouiller | #131 |
| Unlock with owner password… | Déverrouiller avec le mot de passe du propriétaire… | #131 |
| restricted (changes) | restreint (les modifications) | #131: « Le propriétaire de ce document a restreint les modifications. » |
| Set password | Définir un mot de passe | #131 |
| Remove password | Retirer le mot de passe | #131; *Retirer*, as for Remove |
| crash recovery | récupération après une fermeture inattendue | #131, desktops |
| This form is built to be filled in with Adobe Reader. You can still view, print, save and share it here — only filling it in isn't possible. | Ce formulaire est conçu pour être rempli dans Adobe Reader. Vous pouvez tout de même l'afficher, l'imprimer, l'enregistrer et le partager ici — seul le remplir n'est pas possible. | #456, #457: the dynamic-XFA notice (desktop banner/InfoBar, iOS's persistent `DynamicXfaBanner`). Says what still works before naming the one thing that doesn't, per the house rule against implying the whole document is broken |
| This form needs Adobe Reader to fill in. | Ce formulaire doit être rempli dans Adobe Reader. | #457: said instead of arming signature/checkmark placement on a dynamic-XFA document (desktop, iOS) — explains rather than silently landing the mark nowhere |
| Get Adobe Reader | Obtenir Adobe Reader | #457, the notice's action button/link (desktop, iOS). Opens the same reader-download page (adobe.com/go/reader_download) the document's own placeholder text already names (#456), not a different link of our own |
| Save over the signed original? | Enregistrer par-dessus l'original signé? | #476, #481 phase 1 (iOS): the Save confirmation's title when the open document carries a signature and no redaction marks are pending. Reused for the Password sheet's own confirmation, which writes over the file the same way |
| This document has a digital signature. Saving here will invalidate it. Save a copy to keep the signed original intact. | Ce document porte une signature numérique. L'enregistrer ici invalidera la signature. Enregistrez une copie pour garder l'original signé intact. | #476, #481: the ordinary-signature wording — says what will happen, then the safe alternative |
| This document is certified as closed to changes. Saving here will invalidate that certification. Save a copy to keep the signed original intact. | Ce document est certifié fermé aux modifications. L'enregistrer ici invalidera cette certification. Enregistrez une copie pour garder l'original signé intact. | #476, #481: the certification (`/DocMDP`) wording — measured as the common case, not the edge case (33/33 of #476's real corpus), so it says the document is *certified closed to changes*, not merely that a signature will stop verifying |
| Overwrite the signed original | Écraser l'original signé | #481, the Save confirmation's destructive button, ordinary signature |
| Overwrite the certified original | Écraser l'original certifié | #481, the same button, certification signature |
| This document is also digitally signed; overwriting it will invalidate the signature too. | Ce document porte aussi une signature numérique; l'écraser invalidera aussi la signature. | #481: appended to the redaction confirmation's (#173) message when the document is also signed, rather than asking a second question for the same write |
| This document is also certified as closed to changes; overwriting it will invalidate that certification too. | Ce document est aussi certifié fermé aux modifications; l'écraser invalidera aussi cette certification. | #481, the certification twin of the note above |
| The signature on this document doesn't carry over to the copy. | La signature de ce document ne sera pas reportée dans la copie. | #481: the quiet, once-per-open note on Save a copy for a signed document — the common, already-safe path still deserves to know the copy isn't signed |
| This document is signed | Ce document est signé | #476, #481: Save's warning before overwriting a signed original (ordinary/approval signature), desktop |
| Saving over this document will invalidate its digital signature. Save a copy instead to keep the signed original intact. | L'enregistrement par-dessus ce document invalidera sa signature numérique. Enregistrez plutôt une copie pour garder l'original signé intact. | #476, #481 |
| This document is signed and certified against changes | Ce document est signé et certifié contre toute modification | #476, #481: shown instead of the row above when the signature carries a /DocMDP certification — measured as the common case, not the rare one, on real signed documents (33/33 of #476's corpus) |
| This document's signature certifies it — its author declared that it should not be changed at all. Saving over it will invalidate the signature. Save a copy instead to keep the signed original intact. | La signature de ce document le certifie — son auteur a déclaré qu'il ne devait subir aucune modification. L'enregistrer par-dessus invalidera la signature. Enregistrez plutôt une copie pour garder l'original signé intact. | #476, #481 |
| The signature on the original doesn't carry over to this copy. | La signature de l'original ne s'applique pas à cette copie. | #476, #481: said once, quietly, after Save a copy on a signed document — never a dialog to dismiss |
| Change this page? | Modifier cette page? | #139, title of the warning below |
| Changing this page may slightly alter parts of it you haven't touched. | Modifier cette page pourrait légèrement changer des parties que vous n'avez pas touchées. | #139, once per page before a whiteout, text box or removal on a page PDFium's rewrite would alter; *changer*, not *altérer* |
| Unsaved changes | Modifications non enregistrées | |
| Save changes to {0}? | Enregistrer les modifications de {0}? | |
| Your changes will be lost if you don't save them. | Vos modifications seront perdues si vous ne les enregistrez pas. | |
| Restore unsaved changes? | Restaurer les modifications non enregistrées? | |
| This document has unsaved changes. They won't be in the shared copy unless you save first. | Ce document contient des modifications non enregistrées. Elles ne feront pas partie de la copie partagée si vous ne l'enregistrez pas d'abord. | #378, unsaved-changes alert's Share-case message |
| Couldn't open that file | Impossible d'ouvrir ce fichier | |
| This file is too large for MegaPDF to open. | Ce fichier est trop volumineux pour que MegaPDF puisse l'ouvrir. | #147; *volumineux* for a file's size, never *gros* |
| Couldn't save | Impossible d'enregistrer | |
| Couldn't shrink | Impossible de réduire | |
| Couldn't export | Impossible d'exporter | Windows Save As → Markdown export error title (#386); short-title twin of the fuller iOS "Couldn't export the text."/"Couldn't prepare the export." above |
| Couldn't update | Impossible de mettre à jour | |
| Save first | Enregistrez d'abord | |
| Nothing to shrink | Rien à réduire | |
| Smaller copy saved | Copie réduite enregistrée | |
| Saved | Enregistré | |
| Saving… | Enregistrement… | |
| Opening… | Ouverture… | busy strip (#145) |
| Checking the saved file… | Vérification du fichier enregistré… | busy strip, while a save is read back (#145) |
| Checking this page… | Vérification de cette page… | page spinner, the #139 check and the text-edit check (#145) |
| Applying… | Modification en cours… | page spinner while a change is made (#145); « Application… » alone would read as *the app*, and the long form was three times the English on the smallest label in the app (#242) |
| Searching… | Recherche… | busy strip (#145) |
| Making a smaller copy… | Création d'une copie réduite… | busy strip, shrink for email (#145) |
| Preparing to print… | Préparation de l'impression… | busy strip (#145) |
| Restoring your edits… | Restauration de vos modifications… | busy strip, crash recovery (#145) |
| Do you want to save the changes made to the document “{0}”? | Voulez-vous enregistrer les modifications apportées au document « {0} »? | macOS unsaved-changes sheet (#145); Windows keeps "Save changes to {0}?" |
| Don't Save | Ne pas enregistrer | macOS button, title case (#145) |
| Couldn't print | Impossible d'imprimer | (#145) |
| Couldn't make that change | Impossible d'effectuer cette modification | (#145) |
| The change could not be made. | La modification n'a pas pu être effectuée. | macOS status line (#145) |
| Couldn't restore your edits | Impossible de restaurer vos modifications | (#145) |
| Couldn't search this document. | Impossible de rechercher dans ce document. | Android (#145) |
| Couldn't move this signature | Impossible de déplacer cette signature | Android (#145) |
| PDF document | Document PDF | file-type name |
| Markdown document | Document Markdown | Windows Save As file-type name (#386); *Markdown* itself is unchanged in French — see its own glossary row above |
| {name} - edited | {name} - modifié | suggested file name |
| {name} - smaller | {name} - réduit | suggested file name |
| Scanned image | Image numérisée | |
| Esc cancels | Échap pour annuler | |
| Update | Mettre à jour | |
| Restart now | Redémarrer maintenant | |
| A new version of MegaPDF is available ({0}). | Une nouvelle version de MegaPDF est disponible ({0}). | |
| Make MegaPDF your PDF app? | Faire de MegaPDF votre application PDF? | |
| Choose default apps | Choisir les applications par défaut | |
| Something went wrong. | Une erreur s'est produite. | generic error body, technical detail follows on its own line |
| Tap the page where the text should go | Touchez la page à l'endroit où placer le texte | mobile |
| Tap the page where the signature should go | Touchez la page à l'endroit où placer la signature | mobile |
| Signature added | Signature ajoutée | |
| Signature {0} | Signature {0} | default mobile name, persisted |
| Redact | Caviarder | #173. The verb Termium and the Quebec public service use for removing content from a document; "expurger" is the alternative and is less specific. **For francophone review.** |
| Redaction | Caviardage | #173 |
| Marked for redaction | Marqué pour caviardage | #173 |
| Remove content from the file | Retirer du contenu du fichier | #173. "Retirer" (take out), not "supprimer" (delete), because the point is that it leaves the file |
| Cover an area — this doesn't remove what's under it | Masquer une zone : ce qui est dessous n'est pas retiré | #173. The whiteout tooltip, reworded so the tool says what it does |
| Whiteout covers — it doesn't remove | Le correcteur masque sans rien retirer | #173 first-use hint; no comma splice (#242) |
| Save as a copy | Enregistrer une copie | #173 |
| Overwrite the original | Remplacer l'original | #173 |
| -redacted | -caviardé | #173 file-name suffix. With its accent, like the app's other suggested names (`{0} - modifié`, `{0} - réduit`); without it it reads as the verb *il caviarde* (#242) |
| {0} areas redacted: {1} | {0} zones caviardées : {1} | #173 summary after saving |
| {0} off | {0} : désactivé | #268, Windows: spoken when Add text, Whiteout or Redact turns off; {0} is the tool's label |
| Nothing was removed | Rien n'a été retiré | #173, when a redaction fails closed |
| About MegaPDF | À propos de MegaPDF | #176, macOS app menu and the window it opens; every platform spells the product MegaPDF (2026-09-18) |
| Version {0} ({1}) | Version {0} ({1}) | #176; the build number only appears once it differs from the version |
| Third-Party Notices… | Avis de tiers… | #176, the button that opens the notices window |
| The notices file is missing from this build. | Le fichier des avis est absent de cette version. | #176 |
| Window | Fenêtre | #176, macOS menu bar |
| Help | Aide | #176, macOS menu bar |
| Minimize | Minimiser | #176, macOS Window menu. macOS's own word, read from AppKit's MenuCommands.loctable (fr and fr_CA alike) on the Mac mini, 2026-09-19 (#242) |
| Zoom | Zoom | #176, macOS Window menu; the same word in French |
| Hide {0} | Masquer {0} | #191, macOS app menu. These five are macOS's own words, copied from TextEdit's Edit.loctable rather than translated — fr and fr-CA are identical there |
| Hide Others | Masquer les autres | #191 |
| Show All | Tout afficher | #191 |
| Quit {0} | Quitter {0} | #191 |
| Services | Services | #191, the system's submenu; the same word in French |
| Show in Finder | Afficher dans le Finder | #165, macOS context menu on a recent document. Apple's own wording (Migration.loctable) |
| Show in Files | Afficher dans Fichiers | #165, iOS context menu. **For francophone review** — not confirmed against an Apple catalogue |
| {0}, in {1} | {0}, dans {1} | #165, the accessible name of a recent document: the file name and where it lives |
| Not found · {0} | Introuvable · {0} | #165, a recent document that is no longer where it was |
| iCloud Drive | iCloud Drive | #165; the Files app's sidebar says iCloud Drive in French too |
| On My iPhone / On My iPad | Sur mon iPhone / Sur mon iPad | #165, where a recent file lives |
| Downloads | Téléchargements | #165; on macOS the folder's name comes from the system (displayNameAtPath:), not from here |
| Couldn't show {0} in the Finder. | Impossible d'afficher {0} dans le Finder. | #165 |
| Built for Adobe Reader | Conçu pour Adobe Reader | #456/#457, the dynamic-XFA banner's title — persistent, on the document, not a dialog or a snackbar |
| This form is designed to be filled in using Adobe Reader. MegaPDF can't fill it in, but you can still view, save, share and export it. | Ce formulaire est conçu pour être rempli avec Adobe Reader. MegaPDF ne peut pas le remplir, mais vous pouvez quand même le consulter, l'enregistrer, le partager et l'exporter. | #456/#457, the banner's body — says plainly what still works, rather than implying the document is unusable |
| Get Adobe Reader | Obtenir Adobe Reader | #456/#457, the banner's link — the same address (adobe.com/go/reader_download) the form's own placeholder page names |
| This form can only be filled in using Adobe Reader. | Ce formulaire ne peut être rempli qu'avec Adobe Reader. | #456/#457, shown when Sign or Add text is armed on a dynamic-XFA document: explains rather than silently doing nothing |
| This will invalidate the signature | Ceci invalidera la signature | #476/#481, the overwrite-warning dialog's title for an ordinary (non-certification) signature — asked only at the point of Save, never a banner on open |
| MegaPDF rewrites the whole file when it saves, so the document's digital signature will no longer verify once you save here. Save a copy instead to leave the signed original untouched. | MegaPDF réécrit le fichier au complet lors de l'enregistrement, donc la signature numérique du document ne sera plus valide une fois que vous aurez enregistré ici. Enregistrez plutôt une copie pour laisser l'original signé intact. | #476/#481, the ordinary case's body — Save a copy is offered as the prominent, already-safe choice |
| This document does not allow changes | Ce document n'autorise aucune modification | #476/#481, the certification (/DocMDP) case's title — stronger wording: a DocMDP permission can forbid modification outright, not merely be invalidated by it. Measured as the common case on real signed documents (33/33 in the #476 corpus), not the edge case |
| This document was certified not to be modified — its author declared that no changes are allowed at all. Saving here will break that certification as well as the signature. Save a copy instead to leave the signed original untouched. | Ce document a été certifié comme ne pouvant être modifié — son auteur a déclaré qu'aucune modification n'est permise. L'enregistrer ici brisera cette certification en plus de la signature. Enregistrez plutôt une copie pour laisser l'original signé intact. | #476/#481, the certification case's body |
| Saved. The signature doesn't carry over to a copy. | Enregistré. La signature ne se transfère pas à une copie. | #476/#481, shown once and quietly after Save a copy of a signed document — the signed original is untouched, but the new copy is not itself signed either |
| Reading mode | Mode lecture | #168 decision 1 / #505 — the name on every platform. Not *Mode de lecture*: the shorter form is what the View menu and the mobile More menus carry, and it matches Acrobat's and Word's French |
| Enter Full Screen / Exit Full Screen | Activer le mode plein écran / Désactiver le mode plein écran | #505, the View menu item — macOS's own French for this item, so it reads as the system's rather than as ours |
| Reading mode on / Reading mode off | Mode lecture activé / Mode lecture désactivé | #505, announced to a screen reader on entering and leaving |
| Exit | Sortir | #505, the reading pill's last button. Never *Quitter*, which is Quit |
| Exit reading mode | Sortir du mode lecture | #505, the accessible name and tooltip of that button |
| Reading controls | Commandes de lecture | #505, the accessible name of the floating pill |
| Previous page / Next page | Page précédente / Page suivante | #505, the reading pill. Distinct from the find bar's *Précédent* / *Suivant*, which are about matches |
| Go to page | Aller à la page | #505, the reading pill's page number opens a box to type one into |
| Page colours | Couleurs de la page | #511, the Options row. British spelling in the English, as everywhere else in this app |
| Normal / Sepia / Night | Normales / Sépia / Nuit | #511, the three page colours. *Normales* agrees with *couleurs*, the label above the list |
| Night inverts the page, pictures included. | Le mode nuit inverse la page, images comprises. | #511, said in the settings copy because it is a decision (#168 decision 3), not a defect: a photograph reads as a negative at night |
| Open documents in reading mode | Ouvrir les documents en mode lecture | #511, the Options checkbox; off by default (#168 decision 2) |
| Hide the tools and read the page (Ctrl+H) | Masquer les outils et lire la page (Ctrl+H) | #504, the Windows-only tooltip on the Reading mode command. "The tools", not "the toolbar": on Windows the command also sits in the "…" overflow, where the row itself is not what goes |

## Français (France)

France French is not translated separately: `tools/gen_strings.py fr-fr`
derives it from the Canadian catalogues with the table below, then applies
France punctuation (a non-breaking space before `?`, `!` and `;` as well as
`:`). Add a row here **and** to `FR_CA_TO_FR_FR` in the script when a new
Canadian term needs a France counterpart; never edit a derived file.

| Français (Canada) | Français (France) |
|---|---|
| courriel | e-mail |
| pour courriel / pour le courriel | pour l'e-mail |
| infonuagique | cloud |
| crochet | coche |
| Merci tout spécial à | Un grand merci à |
| fin de semaine | week-end |
| ramassage | enlèvement |

The demo documents the store captures show are the exception: their text is
written per listing language, not derived (#310, Dave 2026-09-26).
`tools/gen_test_fixtures.py` (`DEMO_TEXT["fr"]` for fr-CA, `DEMO_TEXT["fr-FR"]`)
and `tools/screenshots-windows/gen_store_docs.py` (`--lang fr-CA` / `fr-FR`)
each carry both pages: Quebec's says *fin de semaine*, *ramassage*, pi, lb and $;
France's says *week-end*, *enlèvement*, m, kg and €. Same layout, same demo
person per language (Hélène Bélanger, Céline Lefèvre), and the lines the
screenshot poses key on — *location* three times, *sections 1 à 4* on one
line — are the same in both.
