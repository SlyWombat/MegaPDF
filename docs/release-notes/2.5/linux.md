# Linux — what's new, 2.5

The Linux channel's what's new is a `<release>` entry in
[`tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`](../../../tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml),
no character limit. Linux says the same as every other channel: the Avalonia app's About
window gained the support link in #730, and that one binary serves Mac and Linux.

Wording set by Dave on 2026-11-16 ("Just say linked to support page, no need for additional
description"): one line, the About link to the support page added on every app by #730
(PR #850). **Approved and released.** Live on 2.5.0: Google Play (vc 17), Linux (APT, `.deb`, tarball) and both CLI archives on 2026-11-16; Snap stable (revision 5) on 2026-11-17 UTC; iOS (build 37, released 2026-11-17T20:34Z) and Windows (2.5.0.0, submission 1152921505702432143) on 2026-11-17; Mac (build 2.5.0) on 2026-11-18. Release checklist #862. **`FR-REVIEW #730`, `ES-REVIEW #730`: the French and the
Spanish below were reviewed.** Fable approved all six fr-CA and es-419 sentences, and the identical fr-FR and es-ES
derivations, on 2026-11-16. Any change needs another Fable review; the markers stay by design. The paragraphs below are the store blocks' sentences, and
`check_copy.py` fails if they drift from them.

## What's new — the `<release>` entry to add

First in `<releases>`, above the 2.4.0 entry, in the same five paragraphs as 2.4.0's
(English, `fr-CA`, `fr`, `es`, `es-ES`).

```xml
    <release version="2.5.0" date="REPLACE-AT-TAG">
      <description>
        <p>About MegaPDF now links to the new support page.</p>
        <p xml:lang="fr-CA">La section À propos de MegaPDF comprend maintenant un lien vers la nouvelle page de soutien.</p>
        <p xml:lang="fr">La section À propos de MegaPDF comprend maintenant un lien vers la nouvelle page de soutien.</p>
        <p xml:lang="es">La sección Acerca de MegaPDF ahora incluye un enlace a la nueva página de soporte.</p>
        <p xml:lang="es-ES">La sección Acerca de MegaPDF ahora incluye un enlace a la nueva página de soporte.</p>
      </description>
    </release>
```

Nothing here has been applied to the metainfo; that happens at release time, per
`docs/RELEASING.md`.
