# Google Play — what's new, 2.4

Play Console → the release → **Release notes**, per language. `tools/play_listing.py` pushes it once `tools/gen_listing_copy.py` has copied it into `android/RELEASING.md`. Limit **500 characters**.

Shape set by Dave on 2026-10-23 ("Spanish support and minor bug fixes", recorded
on #724): one line saying Spanish is supported, then one generic line for the other fixes.
No feature is named. Approved by Dave (2026-10-25) and **released**: 2.4.0 (versionCode 16) is on the production track with status completed, and the Play page showed 2.4.0 on 2026-10-25. **`FR-REVIEW #724`, `ES-REVIEW #724`: the
French and the Spanish below are not yet reviewed.** Fable reads the fr-CA and es-419
blocks in the Wave 3 string pass; any change after that needs another review. The France
and Spain blocks are derived by `check_copy.py` in this folder: edit the Canadian or Latin
American block, then run it.

## English (Canada) — `en-CA`

**Release notes** [500] (82)

```
MegaPDF now speaks Spanish, for Latin America and for Spain. Plus minor bug fixes.
```

## Français (Canada) — `fr-CA`

**Notes de version** [500] (118)

```
MegaPDF est maintenant offert en espagnol, pour l'Amérique latine et pour l'Espagne. Et quelques corrections mineures.
```

## Français (France) — `fr-FR`

**Notes de version** [500] (118)

```
MegaPDF est maintenant offert en espagnol, pour l'Amérique latine et pour l'Espagne. Et quelques corrections mineures.
```

## Español (Latinoamérica) — `es-419`

**Notas de la versión** [500] (116)

```
MegaPDF ahora está disponible en español, para Latinoamérica y para España. Además, correcciones de errores menores.
```

## Español (España) — `es-ES`

**Notas de la versión** [500] (116)

```
MegaPDF ahora está disponible en español, para Latinoamérica y para España. Además, correcciones de errores menores.
```
