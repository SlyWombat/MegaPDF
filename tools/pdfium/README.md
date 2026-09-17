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
| `0019-content-generator-gray-colour` | #125 | DeviceGray colours stay `g`/`G`, and a regenerated page starts from `0 G 0 g`: upstream wrote gray as RGB, which changed the current colour space too, so a form drawn after the object that sets a colour by its components alone (`0.742 SC`) read them in RGB and its strokes changed colour |
| `0020-icc-transform-real-colour-space` | #152 | render speed of CMYK and other ICC-profiled images: the transform to sRGB is built with the profile's own colour space instead of `PT_ANY`, so lcms precomputes it (with `PT_ANY` it cannot optimise, and every pixel ran the whole profile pipeline, several times slower); colours can move by a CLUT interpolation step |
| `0021-dct-decode-at-reduced-size` | #152 | render speed of large JPEG images: when the render needs at most a quarter of the image's size, a `DCTDecode` image is decoded at 1/2, 1/4 or 1/8 with libjpeg's scaled IDCT, one level fewer than JPX already skips, so the stretch still starts from at least twice the pixels it needs (upstream always decoded, converted and stretched every source row); image masks, `FPDFImageObj_GetBitmap` and thumbnails keep full size |
| `0022-dct-decode-full-size-fallback` | #152 | JPEGs libjpeg will not scale still draw: the decoder asks libjpeg for the reduced size before reporting it, and decodes at full size when libjpeg cannot give it (0021 failed the decode instead, so a lossless JPEG drawn at a quarter of its size or less did not load) |
| `0023-page-user-unit` | #150 | `FPDFPage_GetUserUnit`: a page's `/UserUnit`, the size of its user space unit in points (PDF 1.6), read from the page dictionary and 1.0 when missing or invalid. Upstream has no accessor for it, so a page drawn in 2-point units showed at half size; the core scales every coordinate and size it reports and accepts by it |
| `0024-page-image-cache-reduced-huge-images` | #151 | render speed of huge images at every zoom after the first: when a render needs far fewer pixels than an image of 60 MB or more decoded, the page's image cache keeps an area-averaged copy at up to twice the device size (at most 96 MB, never under the device size) instead of the whole image, and a whole image already cached is reduced the next time a smaller render finds it (upstream never realizes such an image and counts it as small, so it stayed cached as a lazy decoder and every render inflated the whole stream again: over a second for a 20,000 x 15,000 px Flate image even at a tenth of its size); a larger render decodes again and replaces the copy; pixels move by at most a few levels |

The #118 layout guard stays in the core regardless: every body-text edit is rehearsed on
a copy of the page and refused if anything else would change. The patches make that
refusal rare; they do not replace it.

## Known limits

What a regenerated page can still differ in, measured on the 4,337-document corpus at patch 21
(first-line guard scan: 3,085 of 3,085 edits editable; no-op regeneration of the first, middle
and last page of every document: 7,988 pages, none the guard would refuse). Each is deliberate
or harmless, with the evidence for it (#125, #128).

- **Clips PDFium merges when it parses the page again.** A clip rectangle that contains the next
  clip set on it is dropped by PDFium's parser (`CPDF_ClipPath::AppendPathWithAutoMerge`), so an
  object can come back with fewer clip paths than it was written with. The drawn area does not
  change. On the one corpus page this affects, 1,047 of the 1,048 objects lie inside the clips
  they lose; 8 pixels change by at most 45 of 765 (sum of channel differences), under the
  guard's per-pixel threshold of 60, and nothing visible changes.
- **Anti-aliasing noise.** Five corpus pages differ after a regeneration by at most 3 of 765 on a
  few pixels (glyph and path edges rasterised from the rewritten, rounded coordinates). Invisible;
  the guard already accepts them.
- **Text state a form XObject inherits.** A form whose content shows text without setting its own
  font, size, spacing or colour draws with the state set before its `Do`. The writer writes each
  object's own state, never the text state a form inherits, so on a regenerated page that text is
  lost or drawn in the default state. The guard refuses edits on such a page (it is the refusal
  page every platform's layout-guard test uses). No corpus page has it; it can be fixed by writing
  the form object's inherited text state before `Do`.
- **Size of glyph clips (0018).** Text used as a clip is written as the path PDFium clips to, the
  union of the glyph outlines, uncompressed, on every object the clip applies to. The two corpus
  pages with glyph clips grow by 7% and 12% when saved; a clip of 733 glyphs over one box grows a
  page from 1 KB to 381 KB. Over the corpus the regenerated pages are 0.007% smaller in total (0019
  writes gray shorter). A text clip over many objects multiplies its outline per object; a shared
  clip scope would avoid that.
- **Type3 glyph clips (0018).** Text in a Type3 font used as a clip is left out of the written
  clip, because its glyphs are content streams, not outlines. The objects after it are then clipped
  less than they were, never more. No corpus page has it.
- **Image decode at a reduced size (0021, 0022).** A JPEG drawn at a quarter of its size or less is
  decoded at 1/2, 1/4 or 1/8 of it, one level fewer than JPX. Renders at that size differ from a
  full-size decode on at most 0.31% of a corpus page (1.35% of a synthetic page of one-pixel
  hairlines and a checkerboard) by more than 60 of 765. A regeneration is judged against a render
  from the same build, so guard verdicts do not change. As for JPX upstream, the decode is sized
  against the whole render bitmap, not the image's drawn size: MegaPDF always renders whole pages,
  but a caller that renders a zoomed tile with `FPDF_RenderPageBitmapWithMatrix` gets a softer
  image (at 8x zoom, up to 1.7% of a synthetic tile and 0.5% of a corpus tile differ by more than
  60). A JPEG libjpeg will not scale, such as a lossless one, is decoded at full size (0022).

**Guard tolerances (#128).** No change is needed. Every difference that remains on the corpus
already sits under the per-pixel threshold (60 of 765) or the page budget (0.05% of pixels), and
the run check (text and bounds within 0.5 pt) has never refused an edit on its own. Loosening
either would only admit changes the patches now fix.

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
