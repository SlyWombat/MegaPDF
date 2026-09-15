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
| `0010-save-with-new-security` | #131 | `FPDF_SaveAsCopyWithSecurity`: a copy encrypted with AES-256 (R6) under new user and owner passwords and permissions — upstream can only keep or remove security, and never wrote AES-256 owner entries |
| `0011-font-has-glyph` | #130 | `FPDFFont_HasGlyph`: whether a font program has a real glyph (not .notdef, with an outline) for a character, which reading text back cannot show |
| `0012-content-generator-reuse-resource-names` | #138 | regeneration time and file size: images and forms keep their resource names, and direct shadings, patterns and colour spaces keep one indirect copy, across regenerations (upstream minted a new name, and 0002/0004 a new copy, every time, so each regeneration of an image-heavy page was slower than the last) |
| `0013-content-generator-soft-mask-space` | #140 | soft masks in the space they were set in: the ExtGState is written after the object's `cm` when the mask's matrix is the object's, and otherwise replaced by a copy with the mask's matrix folded into its group's `/Matrix` (upstream, and 0006, replayed every ExtGState at the identity CTM, so a mask set under a scaled or flipped CTM covered another part of the page); the writer's own `ca`/`CA`/`BM` dictionaries are reused once the page is parsed again instead of added every reload; an ExtGState retired while its object was off the page is put back before it is written; each ExtGState name is written once per object (a reparsed stream repeated names, and every regeneration wrote them once more) |
| `0014-content-generator-inline-font-widths` | #141 | a font dictionary written directly in the page's `/Resources /Font` keeps its name and every entry (upstream rebuilt it from its base font and encoding, dropping `/Widths`, so its text moved, and merged direct fonts sharing a base font) |
| `0015-content-generator-inherited-resources` | #125 | the resources of a page whose form XObject (or Type3 font) has no `/Resources` of its own and draws with the page's: upstream kept only the names the page's own objects use, so the image or font only such a form names was removed, and the form drew nothing or its text in a substitute font |
| `0016-content-generator-stroked-text-ctm` | #125 | text stroked under a scaled or flipped CTM keeps its outline: the CTM PDFium keeps apart for the stroke is written as `cm` with the text matrix relative to it (upstream wrote the text matrix alone, so the line width and dashes applied in page space and the outline came back many times thicker) |
| `0017-content-generator-pattern-colour-space-arrays` | #125 | a pattern set through a `[/Pattern base]` colour space: a coloured tiling pattern or shading pattern is written through the Pattern space, and an uncoloured tiling pattern with its base space and components (upstream, and 0002, dropped any pattern whose space was an array, so what it filled painted solid black) |
| `0018-content-generator-text-clips` | #125 | glyphs used as a clip (`4`–`7 Tr`): every object drawn after them keeps the clip, written as the path PDFium clips to, the union of the glyph outlines with `W n` (upstream wrote only path clips, and the clipping text as its own object inside `q … Q`, so the clip ended there and an image drawn through a word covered the page; written back as text, the clip would come back as more text objects each time the page is parsed) |

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
