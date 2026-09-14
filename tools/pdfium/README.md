# MegaPDF's PDFium patch series

PDFium's content writer (`CPDF_PageContentGenerator`) regenerates a page's content
streams whenever an object on the page changes, and upstream it loses things when it
does: character and word spacing, non-RGB colour, Type3 text, inline images, shadings,
and any state that straddles two content streams (#118, #119). MegaPDF edits body text
through that writer, so the apps ship a PDFium built from this patch series.

| Patch | Issue | What it keeps |
|---|---|---|
| `0001-content-generator-write-char-and-word-spacing` | #121 | `Tc` / `Tw` on every text object |
| `0002-content-generator-keep-colour-spaces` | #122 | CMYK, ICC, Cal*, Lab, Separation, DeviceN, Indexed colour; coloured patterns |
| `0003-content-generator-type3-text-and-distinct-fonts` | #123 | Type3 text; fonts that share a BaseFont stay distinct |
| `0004-content-generator-inline-images-and-shadings` | #124 | inline images; `sh` shading objects |
| `0005-content-generator-regenerate-every-stream` | #125 | pages whose state spans several content streams |
| `0006-content-generator-graphics-state` | #125 | the ExtGState an object was drawn under (soft masks, overprint, …); miter limit; flatness |
| `0007-content-generator-ctm-between-streams` | #125 | a flip or scale a stream leaves open for the streams after it (upstream wrote the correction in the wrong order; superseded in our build by 0009, kept as the standalone upstream fix) |
| `0008-content-generator-open-rectangles` | #125 | open four-cornered paths stay open (upstream wrote them as the closed `re`) |
| `0009-content-generator-self-contained-streams` | #125 | exact geometry when state straddles streams: each regenerated stream starts and ends at the page CTM instead of threading it through float inverses |

The #118 layout guard stays in the core regardless: every body-text edit is rehearsed on
a copy of the page and refused if anything else would change. The patches make that
refusal rare; they do not replace it.

## Building

`build-pdfium.sh` drives [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)
at the tag matching the pinned Chromium branch, adds this series after its own patches,
and stamps `staging/VERSION` with `MEGAPDF_PATCHES` (the count) and `MEGAPDF_SERIES`
(a hash of the patch files).

```bash
tools/pdfium/build-pdfium.sh ~/pdfium-build linux x64            # full build, ~15 min on 16 cores
START_STEP=6 tools/pdfium/build-pdfium.sh ~/pdfium-build linux x64  # rebuild after editing ~/pdfium-build/pdfium
```

The **PDFium (patched) build** workflow builds every target the apps ship. With
`release` ticked it publishes the archives as a release of this repository, tagged
`pdfium-<build>-megapdf-<series>`.

## Writing a patch

1. Snapshot the files you will touch from `~/pdfium-build/pdfium` (the tree already has
   every earlier patch applied).
2. Edit, then `START_STEP=6 tools/pdfium/build-pdfium.sh ~/pdfium-build linux x64`.
3. Write the patch as `diff -u --label a/<path> --label b/<path> <snapshot> <tree>`
   for each file, numbered after the last one.
4. Point the Linux core build at the result and run the core tests:
   `cmake -S core -B <dir> -DMEGAPDF_PDFIUM_DIR=~/pdfium-build/staging ...`.
   `core/CMakeLists.txt` reads `MEGAPDF_PATCHES` from that `VERSION`; each
   `test_rewrite_fidelity` case names the patch that fixes it, and must be refused below
   that level and pass at or above it.
5. Measure on the corpus with the diagnosis probe (no regressions allowed), then commit
   the patch with its fidelity cases.

## Shipping a release

1. Run the workflow with `release` ticked.
2. Put the release's download URL in `libs/pdfium/RELEASE`.
3. `tools/pdfium/install-release.sh` replaces the committed Windows and Android binaries
   and headers and checks every archive carries the same `VERSION`. macOS, Linux and iOS
   fetch from `libs/pdfium/RELEASE` at build time, and the iOS cache key follows its hash.

## Upstream

Each fix is offered to PDFium (#129). A patch is dropped at the first pinned Chromium
branch that contains it.
