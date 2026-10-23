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
| `0024-parser-keep-large-streams-in-file` | #147 | memory for large documents: the document's parser keeps every stream of 64 KB or more backed by the file, read when it is used, instead of copying it into memory as it is parsed (upstream copied every stream, and the document holds every object it has parsed until it closes, so measuring the pages of a 2.5 GB document at open held 2.6 GB). Measured on `huge-2_5gb.pdf`: loading all 1,000 pages 15.9 s and 2.64 GB → 1.1 s and 18 MB. Streams in a memory-backed parser (object streams, FDF) are still copied; an encrypted stream is still decrypted into memory |
| `0025-page-image-cache-reduced-huge-images` | #151 | render speed of huge images at every zoom after the first: when a render needs at most a quarter of the pixels of an image of 60 MB or more decoded, the page's image cache keeps an area-averaged copy at twice the device size, else 1.5 times, else the device size (the first under 96 MB) instead of the whole image. Upstream never realizes such an image, so the cache held a lazy decoder and every render inflated the whole stream again: 1.0–1.7 s per render of a 20,000 x 15,000 px Flate image at any zoom. A smaller render draws from the copy and keeps it; a larger one lets it go, decodes again and keeps a new copy. Measured on `huge-image-page.pdf` (Linux x64): first render unchanged, every later render 1.0–1.7 s → 80–330 ms; peak memory 452 → 530 MB, resident about 90 MB higher while the page is open. Corpus (4,263 documents, first and last page at 1,200, 2,000, 2,800 and 4,000 px): identical at 1,200 px; at larger sizes 5 documents with scanned or printed images differ, where the copy's area averaging removes the moiré that upstream's bilinear sampling of the whole image shows on halftone screens (up to 73 levels on at most 7% of pixels), and 200 px renders stay within 4 levels |
| `0026-pageobj-clip-path-and-rect-regions` | #173 | `FPDF_CreateClipPathFromRects` and `FPDFPageObj_AppendClipPath`: a clip path made of several rectangles combined under one fill rule, and a page object given a clip path. Upstream can read an object's clip path and transform it, never set one, and `FPDF_CreateClipPath` can only describe a single rectangle — so nothing in the API could express a region with a hole in it. A redaction clips a path or shading that crosses the edge of a marked area to "the page minus the areas" (an even-odd path of the page with the areas as inner subpaths) instead of removing it whole and taking what it draws outside with it |
| `0027-document-remove-text-outside-the-pages` | #173 | the four places a document's text hides where a redaction of the page content cannot reach, each of them something upstream can read and not write. `FPDF_RemoveMetadata`: every entry of `/Info`, the XMP packet (`/Metadata`) on the catalog and on every page, and any `/PieceInfo` private application data. `FPDFBookmark_Remove`: an outline entry and its subtree, unlinked from its siblings and its parent's `/First`, `/Last` and `/Count` — the corpus battery found this one on its first run, a heading redacted out of the page and still in the outline where a raw byte search finds it. `FPDF_RemovePageLabels`: the catalog's `/PageLabels` number tree, whose prefixes are free text. `FPDF_RemoveStructureTextMatching`: the `/ActualText`, `/Alt`, `/E` and `/TU` entries of a tagged document's structure tree that carry a given string — tagged PDF repeats a page's words outside its content stream so a screen reader can say them, and only the entries carrying the removed text are cleared, so the alternate text of content that was not redacted keeps working |
| `0028-annot-remove-form-field` | #173 | `FPDFDoc_RemoveFormField`: the form field a widget belongs to leaves `/AcroForm /Fields` (and any `/Kids` array it sits in) with its `/V`, `/DV`, `/RV`, `/TU` and `/AP`, and a parent left with no kids goes too. `FPDFPage_RemoveAnnot` takes the widget off its page and no more, so the field dictionary stays reachable from the catalog and the value a redaction was meant to remove is still in the saved file |
| `0029-save-drop-the-removed-encryption-dictionary` | #241 | a copy saved with `FPDF_REMOVE_SECURITY` keeps nothing of the security. Upstream's `RemoveSecurity()` drops the handler and the trailer's `/Encrypt`, so the copy is genuinely in the clear, but the body is written from `GetObjectsWithReferences()`, whose walk starts at the *parser's* trailer — the one the file was read with, which still says `/Encrypt N 0 R`. Object N stayed reachable and was copied verbatim into the plain file: an orphan encryption dictionary carrying `/O`, `/U`, `/OE`, `/UE`, `/Perms` and the old `/P`, so the original password could still be attacked offline from a file the user had asked to have protection removed from. The dictionary's object number is now remembered and left out of the body |
| `0030-save-with-new-security-name-the-dictionary-written` | #246 | a copy saved with `FPDF_SaveAsCopyWithSecurity` names an encryption dictionary that is in it. The body wrote the dictionary at `last_obj_num_ + 1` and the trailer named `document_->GetLastObjNum() + 1`; `WriteOldObjs()` lowers `last_obj_num_` to the last object it could reach, so for any document whose highest object number is one nothing refers to — a stale object, or an incremental-update history — the two differed by one. The trailer then pointed `/Encrypt` at an object that was never written, every reader concluded the file was not encrypted, and its enciphered streams would not decode (30% of the 4,337-document corpus). The number is now chosen once, past every object number the document has, and the trailer uses it. Where the two already agreed the bytes are unchanged. Upstream's fallback, reachable for a document that had no security because 0010's dictionary is inline |
| `0031-save-sixty-four-bit-cross-reference-offsets` | #267 | the cross-reference table of a file over 2 GiB. `object_offsets_` holds `FX_FILESIZE`, and both classic-table writers formatted it through `"%010d"`, so every object past 2 GiB was listed at a **negative** offset — in a real 2.68 GB save, 672 of 3,242 entries, the document catalog among them, and every reader but PDFium fell back to rebuilding the table (poppler had not finished after twenty minutes on 2.5 GB; PDFium itself never noticed, because its parser rebuilds one too — the #246 blind spot). Now `"%010lld"`, and an offset of 10^10 or more, which ten digits cannot express in any format string, is refused rather than written as a 21-byte entry that shifts the rest of the table. `startxref` and `/Prev` were already right: both go through `IFX_WriteStream::WriteFilesize()`. The cross-reference *stream* writer had the same bug one line further out — `OutputIndex()` wrote four offset bytes under a fixed `/W[0 4 1]`, so it wrapped at 4 GiB; the field is now as wide as the largest offset needs (four for every file under 4 GiB, so those bytes do not change), and `/Length` counts the entries actually written instead of `last_obj_num_ * 5`. And the stream object it wrote was not a conforming indirect object at all, at any size: no `/Type /XRef`, no `endobj`, and no entry for itself, so `/Size` named an object no reader could see. qpdf called every incremental save of such a document damaged and rebuilt its table; it now passes `--check` clean |
| `0032-parser-sixty-four-bit-cross-reference-stream-offsets` | #270 | reading a document over 4 GiB whose cross-reference is a *stream* — which is what every producer of PDF 1.5 or later writes. `GetVarInt()` accumulated the offset field into a `uint32_t` whatever `/W` said, so every offset past 4 GiB came back wrapped, the table pointed at nothing and the parser rebuilt it by scanning the whole file: **19,177 ms to open a 4.50 GiB document, against 0.9 ms for the same document with a classic table**. Everything downstream was already 64-bit (`CPDF_CrossRefTable::AddNormal` takes an `FX_FILESIZE`, and the range checks at the call sites were written for a value that might not fit and could never fail). `GetVarInt` and the three entry accessors now return 64 bits, and a value that genuinely does not fit is dropped rather than wrapped. Found while testing 0031's stream half: without it the widened field would have produced a file every conforming reader could read and the engine that wrote it could not |
| `0033-page-organizer-copy-field-hierarchy` | #452 | `FPDF_ImportPagesByIndex` (page combine/split/extract) copies a widget's `/Parent` field hierarchy instead of leaving it pointing at the source document. `CPDF_PageOrganizer::UpdateReference` treats a dictionary's `Parent` key as a structural back-edge and skips it — correctly for a page's own `/Parent` (the source `/Pages` tree) and an outline item's, but a hierarchical field's name, type and value live on exactly that key, so the widget arrived without them and a save wrote `/Parent` naming an object that was not its parent in the new file. `CPDF_PageExporter` now copies the chain explicitly after every requested page is copied: each ancestor field dictionary is cloned once (memoised in the same object-number map `UpdateReference` already keeps), its `/Kids` rebuilt to hold only the kids this export actually copied — another widget on one of the exported pages, or a child field whose own copy is not empty — and its root registered under the destination's `/AcroForm /Fields`. Measured on the 1,349-document `tools/stress/public-corpus`: `pages-battery.sh`'s `extract: refused-fields` (#452's own count) goes from 209 to 0, all as `ok`, 0 new qpdf, count-mismatch, crash or hang outcomes; on the 136 real IRS forms alone, refusal goes from 90% to 0%. MegaPDF's own field-hierarchy refusal (`MEGAPDF_ERR_FIELDS`, `core/megapdf_core.h` contract 10) is narrowed to the one case this patch cannot fix by itself: `megapdf_pages_import`'s clash rename renames a clashing widget's own `/T`, and a hierarchical widget has none, so an import whose hierarchy's top-level name already exists in the destination is still refused whole (`megapdf_pages_extract`, which never has a pre-existing name to clash with, no longer refuses at all) |

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

**Form XObjects are not editable through the writer (#173).** `FPDFFormObj_RemoveObject`
reports success and changes nothing: it marks the page's form *object* dirty, but not the
form's own content stream, so `CPDF_PageContentGenerator::ProcessForm`'s
`if (form_xobject->HasDirtyStreams())` is false and the form's stream is never regenerated
(measured: removing the only text object inside a form and saving leaves the file
byte-for-byte as it draws). Fixing it needs more than a patch. The regeneration writes the
form's stream where it is, and a form XObject may be drawn from more than one page's
resources, so an edit on one page would change the other; writing to a copy means giving
the form a stream of its own, and both `CPDF_Form::form_stream_` and
`CPDF_PageObjectHolder::dict_` are `const` members set at construction. That is an upstream
change, not a patch. Until it is made, redaction **refuses** a page whose form XObject
reaches into a marked area (`MEGAPDF_REDACT_SHARED_FORM`) rather than leaving content
behind — fail-closed, with the page and the reason named.

**Guard tolerances (#128).** No change is needed. Every difference that remains on the corpus
already sits under the per-pixel threshold (60 of 765) or the page budget (0.05% of pixels), and
the run check (text and bounds within 0.5 pt) has never refused an edit on its own. Loosening
either would only admit changes the patches now fix.

## Building

`build-pdfium.sh` drives [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)
at the tag matching the pinned Chromium branch, adds this series after its own patches,
and stamps `staging/VERSION` with `MEGAPDF_PATCHES` (the fix level: the highest patch number,
which is also the number of patches while none has landed upstream; see Upstream below) and
`MEGAPDF_SERIES` (a hash of the patch and `.upstreamed` files).

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

## Upstream (#129)

Every fix is offered to PDFium, one Gerrit change per behaviour, with a PDFium-native test;
the procedure is below. A patch whose fix has reached the pinned branch becomes an
`.upstreamed` marker (same number) and is no longer applied; `MEGAPDF_PATCHES` is the
highest number in the series, so nothing that checks `>= N` notices. **Nobody ever
`git rm`s a patch**: that is how the level would drop (it used to be the file count).

Agents prepare a change (steps a-g below); **Dave uploads it, answers every reviewer and
attests to it himself** (step h, under the Electric RV corporate CLA; #129, decision of
2026-10-21 09:51). Chromium's AI policy puts the human account owner on the hook, so nothing
is uploaded, and no bug is filed, by an agent.

| Patch | Class | Bug | CL | Status | Upstream commit | First branch | Marker since pin |
|---|---|---|---|---|---|---|---|
| 0001 | A | | | not sent | | | |
| 0002 | A | | | not sent | | | |
| 0003 | A | | | not sent | | | |
| 0004 | A | | | not sent | | | |
| 0005 | A | | | not sent | | | |
| 0006 | A | | | not sent | | | |
| 0007 | A | | | not sent | | | |
| 0008 | A | | | not sent | | | |
| 0009 | D | | | ours | | | |
| 0010 | C | | | not sent | | | |
| 0011 | C | | | not sent | | | |
| 0012 | A | | | not sent | | | |
| 0013 | A | | | not sent | | | |
| 0014 | A | | | not sent | | | |
| 0015 | A | | | not sent | | | |
| 0016 | A | | | not sent | | | |
| 0017 | A | | | not sent | | | |
| 0018 | A | | | not sent | | | |
| 0019 | A | | | not sent | | | |
| 0020 | B | | | not sent | | | |
| 0021 | B | | | not sent | | | |
| 0022 | B | | | not sent | | | |
| 0023 | C | | | not sent | | | |
| 0024 | B | | | not sent | | | |
| 0025 | B | | | not sent | | | |
| 0026 | C | | | not sent | | | |
| 0027 | C | | | not sent | | | |
| 0028 | C | | | not sent | | | |
| 0029 | A-other | | | not sent | | | |
| 0030 | C | | | not sent | | | |
| 0031 | A-other | | | not sent | | | |
| 0032 | A-other | | | not sent | | | |
| 0033 | A-other | | | not sent | | | |

**Class** says how a patch is offered:

- **A** is a content-writer fix (2.4 scope): a bug in existing behaviour, with a unit test in
  `cpdf_pagecontentgenerator_unittest.cpp`. Sent in this order, smallest and least contentious
  first: 0001, 0003, 0005, 0007, 0008, 0016, 0019, 0015, 0004, 0014, 0006, 0017, 0012, 0002,
  0018, 0013. Several split into more than one change (see "Offering a patch", c).
- **A-other** is a clear bug outside the writer (save, parse, page organizer), sent after the
  A series. 0033's change description must carry the stale-`/Parent` note from #129: a widget
  written *directly* in `/Annots` is copied with a stale `/Parent`, which is upstream's
  behaviour for non-indirect annotations (`UpdateReference` skips the `Parent` key), not a
  regression of ours.
- **B** changes pixels or object lifetimes (a trade-off): mailing-list discussion first
  (`CONTRIBUTING.md` step 1). Not before 2.5.
- **C** is new public API: `// Experimental API.`, a bug and mailing-list feedback first. These
  stay in our series whatever upstream says, because the core calls them (the `>= N` gates).
  0030 fixes 0010's own path.
- **D** is ours by design. 0009 supersedes 0007 in our build with a different approach (each
  stream starts and ends at the page CTM); upstream gets 0007, the standalone fix.

**Status** is one of `not sent`, `discussing` (mailing list or bug only), `in review`, `merged`,
`merged (partial)`, `abandoned` (the reason goes in the issue the Bug cell names) or `ours`
(class D, and any class C that upstream declines).

### Markers

A patch whose fix is in the pinned Chromium branch is replaced by a marker with the same
number and the extension `.upstreamed`, for example
`patches/0001-content-generator-write-char-and-word-spacing.upstreamed`: four lines, nothing
else.

```
Upstream: https://pdfium-review.googlesource.com/c/pdfium/+/<CL number>
Commit: <full pdfium commit sha>
First-branch: chromium/<NNNN>
Issue: #121
```

`build-pdfium.sh` then:

- applies only the `.patch` files, so a marker never reaches `03-patch.sh`;
- stamps `MEGAPDF_PATCHES` as the highest number among the `.patch` and `.upstreamed` files, so
  turning a patch into a marker changes neither the level nor what any consumer believes
  is present;
- refuses a series in which a number is missing (a dropped patch must leave a marker) or appears
  twice;
- refuses a marker whose `First-branch` is newer than `PDFIUM_BRANCH`: a build at an older pin
  would silently lose a fix that is no longer in the series;
- hashes the markers into `MEGAPDF_SERIES`, so the change is traceable.

If upstream took only part of a fix (a reviewer narrowed it), the number stays a `.patch` holding
only the remainder and its row says `merged (partial)`. A number is never both a patch and a
marker. The series check is written for bash 3.2, because the macOS and iOS targets run the
script on the hosted macOS runners (no `declare -A`; `${arr[@]+...}` where an array can be
empty under `set -u`).

### Offering a patch

Agents do a-g; h is Dave's. Every command here was run for 0001.

a. **A PDFium checkout at upstream `main`, on kdocker3**, in a throwaway Ubuntu 24.04
   container. `/home/claude/pdfium-upstream` is the bind mount and the long-lived upstream tree
   for #129: **do not clear it as scratch**. It is separate from `~/pdfium-build`, our pinned
   build tree. Remove the container, not the folder, when you finish.

   ```bash
   docker run -d --name megapdf-129-pdfium -v /home/claude/pdfium-upstream:/work ubuntu:24.04 sleep infinity
   docker exec -u root megapdf-129-pdfium bash -c 'apt-get update && apt-get install -y git curl python3 \
       ca-certificates xz-utils file lsb-release sudo build-essential pkg-config'
   ```

   Then as uid 1000 (`ubuntu` in the container, which is `claude` on the host) with
   `HOME=/work/home` and `/work/depot_tools` on `PATH`:

   ```bash
   git clone --depth 1 https://chromium.googlesource.com/chromium/tools/depot_tools.git /work/depot_tools
   git config --global --add safe.directory '*'
   mkdir -p /work/gclient && cd /work/gclient
   gclient config --unmanaged https://pdfium.googlesource.com/pdfium.git --name pdfium
   gclient sync --no-history -j8                      # about 12 GB with its toolchain
   ```

   Then, as root in the container only: `cd /work/gclient/pdfium && build/install-build-deps.sh
   --no-prompt --no-chromeos-fonts --no-arm --no-syms --no-nacl`. If you set
   `DEPOT_TOOLS_UPDATE=0` (to stop depot_tools updating itself mid-task), run
   `/work/depot_tools/ensure_bootstrap` once first: without it depot_tools' `gn` wrapper stops
   with "python3_bin_reldir.txt not found". Its closing `luci-auth` traceback is harmless.
   Generate and build:

   ```bash
   cd /work/gclient/pdfium
   gn gen out/Debug --args='is_debug=true is_component_build=false pdf_is_standalone=true pdf_enable_v8=false pdf_enable_xfa=false use_remoteexec=false pdf_use_skia=false'
   autoninja -C out/Debug pdfium_unittests pdfium_embeddertests
   ```

   (1,552 steps, 1 min 42 s on kdocker3's 16 cores for `pdfium_unittests` the first time.)

b. **One branch per change:** `git checkout -b megapdf-<NNNN>[-<part>]` from `origin/main`, then
   `patch -p1 < <MegaPDF>/tools/pdfium/patches/<NNNN>-....patch`. The patches carry `a/`/`b/`
   labels, so it is `-p1`. If it does not apply, rebase the change by hand: upstream `main` has
   moved since `chromium/7934`. `patch` leaves `*.orig` files; delete them before committing.

c. **Split to Gerrit size.** One behaviour per change, under about 200 changed lines including
   the test. Split along the behaviours the patch's row in the first table lists. 0002 splits
   into CMYK/ICC, then Cal*/Lab, then Separation/DeviceN/Indexed, then coloured patterns. 0013
   splits into the ExtGState after `cm`, then the matrix fold, then ExtGState reuse, then the
   retire-and-restore, then the one-name-per-object fix. A refactor found on the way is its own
   change (`CONTRIBUTING.md`). 0001 needs no split: 13 added lines in one function, and one test.

d. **The test, PDFium's way.**
   - A writer fix gets a `TEST_F(CPDFPageContentGeneratorTest, <Name>)` in
     `core/fpdfapi/edit/cpdf_pagecontentgenerator_unittest.cpp`. Copy the setup of
     `ProcessStandardText`: `CPDF_TestDocument`, `CreateNewPage(0)`, a
     `CPDF_PageContentGenerator`, the object, then `TestProcessText`/`TestProcessPath` into an
     `fxcrt::ostringstream`. Assert the exact bytes.
   - Name tests after the behaviour in PDFium's CamelCase (`ProcessTextWithCharAndWordSpacing`),
     not after our issue numbers.
   - A fix that needs a parsed page (state that straddles streams: 0005, 0007, 0009) gets an
     embedder test in `fpdfsdk/fpdf_edit_embeddertest.cpp` over a `testing/resources/<name>.in`
     file, which `testing/tools/fixup_pdf_template.py` turns into the PDF.
   - Our `core_tests.cpp` `test_rewrite_fidelity` case for the patch tells you what to assert.
     Do not port it wholesale: PDFium wants the smallest input that shows the bug. Upstream's
     generator skips objects that are not dirty (`ProcessPageObjects`), so an end-to-end repro
     must dirty the object the fix is about (`FPDFPageObj_Transform`); adding another object
     does not regenerate it.
   - Compare from a `Find()` anchor, not `Last(n)`: `ByteString::Last(n)` is empty when the
     string is shorter than `n`, which hides what was generated when the test fails.

e. **Prove the test.** Build and run `out/Debug/pdfium_unittests
   --gtest_filter='CPDFPageContentGeneratorTest.*'`: it must pass. Then
   `git stash push -- <the non-test source files>`, **rebuild**, run again: the new test must
   fail, and the failure must show the missing bytes. `git stash pop`, rebuild again, and run the
   whole `pdfium_unittests` and `pdfium_embeddertests` once. Name every target you run in the
   rebuild: ninja relinks only what you ask for, and a stale test binary passes or fails for
   the wrong reason.

f. **Format and commit.** `buildtools/linux64-format/clang-format --style=file -i <files>`
   (`git cl format` printed nothing on this checkout, so it was not relied on); confirm with
   `git show --check`. The commit message is a subject under about 60 characters in PDFium's
   imperative style, a blank line, a body wrapped at 72 columns that says what was lost, when,
   and why the fix is right, then a `Bug: <number>` footer, for a bug filed at
   issues.chromium.org under the PDFium component with a minimal PDF attached. No MegaPDF issue
   links in the message; they belong on our side. The first change also adds the organisation
   line to `AUTHORS` (organisations section, sorted). Commit with a placeholder identity
   (`GIT_AUTHOR_NAME` / `GIT_AUTHOR_EMAIL`); Dave runs `git commit --amend --reset-author` with
   the address the corporate CLA covers before uploading.

g. **Hand over.** `git format-patch -1 --stdout > /home/claude/pdfium-upstream/out-cls/<NNNN>.patch`
   (the folder is never committed). Post a comment on #129 with: the patch number, the branch
   name and commit, the test name, the fail-without proof (the gtest failure), a bug-report draft
   with its minimal PDF (kept beside the patch in `out-cls/`), and a "questions a reviewer will
   ask" paragraph. Fill in the row's `Bug` cell once Dave has filed it.

h. **Dave uploads and answers.** `git cl upload -b <bug>`, pick reviewers with Gerrit's "Suggest
   Owners", then "Send and Start Review". Every reply on the change is his.

i. **On merge**, an agent fills in the row: `Status: merged`, the upstream commit and the first
   branch. The first branch is the lowest `chromium/NNNN` branch that contains the commit: in
   the checkout, `git fetch origin '+refs/branch-heads/*:refs/remotes/branch-heads/*'`, then
   `git branch -r --contains <sha> | sort -V | head -1`. Branches are cut about every four
   weeks, so expect a few weeks between merge and branch.

### When the pin moves

When `PDFIUM_BRANCH` moves to `chromium/MMMM` (its own task and issue, using the matching
`bblanchon/pdfium-binaries` tag in `BINARIES_COMMIT`):

1. Each patch whose row says `merged` with `First branch` at or below MMMM: `git mv NNNN-x.patch
   NNNN-x.upstreamed` and replace the content with the four-line marker. The row's last column
   becomes `chromium/MMMM`.
2. Apply the remaining `.patch` files in order on the new branch. Regenerate each one that does
   not apply cleanly, using "Writing a patch" steps 1-3.
3. Run the core tests against the new build ("Writing a patch", step 4). The `fixed_by` cases
   still gate on the same numbers, and now test upstream's version of each landed fix. A case
   that fails means upstream took less than we had: turn that number back into a remainder
   `.patch`, as in the partial rule above.
4. Run the corpus diagnosis probe; no regressions allowed ("Writing a patch", step 5).
5. Ship it as any PDFium release ("Shipping a release").
