# Linux — what's new, 2.4

The Linux channel's what's new is a `<release>` entry in
[`tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml`](../../../tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml),
no character limit. **Linux says the same as every other channel:** Spanish is on all five
platforms, Linux included (the shared Avalonia catalogue serves Mac and Linux, #724).

Shape set by Dave on 2026-10-23 (recorded on #724). Approved by Dave (2026-10-25) and **released**: the 2.4.0 `.deb`, tarball and APT repository, and Snap stable (revision 4), are live (2026-10-24/25).
**`FR-REVIEW #724`, `ES-REVIEW #724`: the French and the Spanish below were reviewed**;
they are the same sentences as the store blocks in this folder, and
`check_copy.py` fails if they drift from them. Fable approved the fr-CA and es-419 text on
2026-10-23 ([#724](https://github.com/SlyWombat/MegaPDF/issues/724)), and Dave approved the
wording of all five blocks, Linux included. The markers stay by design.

## What's new — the `<release>` entry to add

First in `<releases>`, above the newest entry. AppStream localises a `<p>` with `xml:lang`,
the way this file already localises `<name>`, `<summary>` and the captions: `fr-CA` and `fr`
for French, `es` (Latin American, the neutral catalogue) and `es-ES` for Spanish. The
metainfo's other `<description>` paragraphs are English only today, so whether a release
`<description>` is localised at all is Dave's call; the English `<p>` alone is a complete
entry.

```xml
    <release version="2.4.0" date="REPLACE-AT-TAG">
      <description>
        <p>MegaPDF now speaks Spanish, for Latin America and for Spain. Plus minor bug fixes.</p>
        <p xml:lang="fr-CA">MegaPDF est maintenant offert en espagnol, pour l'Amérique latine et pour l'Espagne. Et quelques corrections mineures.</p>
        <p xml:lang="fr">MegaPDF est maintenant offert en espagnol, pour l'Amérique latine et pour l'Espagne. Et quelques corrections mineures.</p>
        <p xml:lang="es">MegaPDF ahora está disponible en español, para Latinoamérica y para España. Además, correcciones de errores menores.</p>
        <p xml:lang="es-ES">MegaPDF ahora está disponible en español, para Latinoamérica y para España. Además, correcciones de errores menores.</p>
      </description>
    </release>
```

Nothing here has been applied to the metainfo; that happens at release time, per
`docs/RELEASING.md`.
