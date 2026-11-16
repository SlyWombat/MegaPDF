# Linux — what's new, 2.5

The Linux channel's what's new is a `<release>` entry in
[`tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`](../../../tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml),
no character limit. Linux says the same as every other channel: the Avalonia app's About
window gained the support link in #730, and that one binary serves Mac and Linux.

Wording set by Dave on 2026-11-16 ("Just say linked to support page, no need for additional
description"): one line, the About link to the support page added on every app by #730
(PR #850). **Draft, not yet released.** **`FR-REVIEW #730`, `ES-REVIEW #730`: the French and the
Spanish below are not yet reviewed** (Fable). The paragraphs below are the store blocks' sentences, and
`check_copy.py` fails if they drift from them.

## What's new — the `<release>` entry to add

First in `<releases>`, above the 2.4.0 entry, in the same five paragraphs as 2.4.0's
(English, `fr-CA`, `fr`, `es`, `es-ES`).

```xml
    <release version="2.5.0" date="REPLACE-AT-TAG">
      <description>
        <p>About now links to the support page.</p>
        <p xml:lang="fr-CA">La section À propos comprend maintenant un lien vers la page de soutien.</p>
        <p xml:lang="fr">La section À propos comprend maintenant un lien vers la page de soutien.</p>
        <p xml:lang="es">La sección Acerca de ahora incluye un enlace a la página de soporte.</p>
        <p xml:lang="es-ES">La sección Acerca de ahora incluye un enlace a la página de soporte.</p>
      </description>
    </release>
```

Nothing here has been applied to the metainfo; that happens at release time, per
`docs/RELEASING.md`.
