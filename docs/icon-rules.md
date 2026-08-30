# MegaPDF — icon rules

Deliverable 4 of #73: enough written down that the next feature's icon does not
restart the drift. It describes the rules the existing set already follows, and
names the ones it does not yet meet.

`docs/design-tokens.md` covers colour, type and spacing. This covers shape.

---

## 1. The app mark

`assets/branding/icon.svg` — a rounded tile holding a document with a folded
corner, and one stroke across it. The file's own comment says what the idea is:

> The one stroke: a calligraphic check, curved entry, straight rise

Not a document — **a document that has been dealt with**. Anything that replaces
the mark keeps that thought.

Generated per platform from the same 256-unit geometry, so the four stay in step:

| Platform | Generator | Output |
|---|---|---|
| macOS | `tools/gen_macos_icon.py` | `assets/branding/MegaPDF.icns`, committed |
| iOS | `tools/gen_ios_icon.py` | 1024 PNG for the asset catalog |
| Android | `tools/gen_android_icons.py` | adaptive foreground/background + legacy mipmaps |
| Windows | — | `megapdf.ico` + Store tiles, drawn separately |

### Masking is not universal

The one place the four genuinely differ, and the one that has already caused a
bug:

- **iOS** clips its own squircle. Draw edge to edge; a margin becomes a gap.
- **Android** masks the adaptive layers to whatever shape the launcher wants,
  and animates within a safe zone. Keep the mark inside the inner 66%.
- **macOS masks nothing.** The rounded shape, the transparent margin and the
  drop shadow all have to be in the pixels. Apple's grid: a 1024 canvas, the
  shape in the middle **824** with a **100px margin all round**, corner radius
  **185.4** — 22.5%, which is the brand tile's own 58-on-256 to within a
  rounding error. An icon that fills the canvas renders square and visibly
  larger than every neighbour in the Dock.
- **Windows** masks nothing either, but has no margin convention: the tile
  fills its square.

macOS is the one to get wrong, so `macos-app.yml` measures it. The check runs
`iconutil` against the built bundle — macOS's own reader, not ours — asserts all
ten representations survive the round trip, and measures the opaque bounding box
of the 512 to confirm the margin is Apple's. A hand-rolled container that our
own tooling reads back is not evidence the platform accepts it.

---

## 2. Toolbar icons

### The grid

**24 × 24**, stroked, never filled. Stroke weight **1.6** at 24 units, round caps
and round joins. Rendered `Stretch="Uniform"` into an 18px slot.

Stroked rather than filled is not a style preference — it is a mistake already
made. `PathIcon` fills its geometry, so the first pass at these rendered as solid
black blobs. `Path` with `Stroke` is what draws line art.

`Stretch` is the second one. `Stretch="None"` draws 24-unit geometry at 24px in
an 18px slot, which overflows into the label beneath it.

### They inherit the foreground

`Stroke="{Binding $parent[Button].Foreground}"` — so an icon is the same colour
as its label, dims with it when the button disables, and follows the theme. No
icon names a colour. That is also why enabled and disabled need no separate
artwork.

### Colour never carries meaning

An icon must be identifiable in one colour. This matters more now that #2 has
people driving the toolbar from the keyboard, and it is what lets the whole set
inherit one brush.

### The label stays

Icon above label, on both desktop apps. These are unfamiliar glyphs on a first
run, and a toolbar you have to decode is worse than one that spells itself out.
The label is the safety net — but it is a net, not the floor. An icon you cannot
guess without reading the label has failed.

Three currently fail that test, named in #73 and left for the designer:
**Cover**, **Shrink for email**, **Add text**. Redrawing them by hand is what
produced them; the metaphors want reconsidering, not another pass.

### macOS cannot use an icon font

The Windows app draws its toolbar with **Segoe MDL2** glyphs, which is correct
Windows practice. That font does not exist on macOS, and bundling one for
seventeen glyphs adds a licence and a download. So the Mac app carries the
geometry inline in `Icons.axaml`.

**This is a real divergence and it is deliberate.** Do not "fix" it by replacing
Windows' `FontIcon` with the hand-drawn geometry — that would make Windows worse
to make a document tidier. iOS uses SF Symbols and Android uses Material icons
for the same reason: each is native to its host.

What has to match across the four is the **metaphor**, not the artwork. Save is a
downward arrow to a line everywhere, or it is four products.

---

## 3. What is fixed and what is open

**Fixed** — change these only with a reason written down:

- the 24-unit grid, 1.6 stroke, round caps and joins
- stroked, not filled; `Stretch="Uniform"`
- icons inherit `Foreground` and name no colour
- meaning never carried by colour alone
- macOS icon metrics: 824 in 1024, 100px margin, radius 185.4
- one metaphor per action across all four platforms

**Open** — #73's commission, and a designer may change any of it:

- the app mark itself
- the Cover / Shrink / Add text metaphors
- whether 1.6 is the right weight at 18px on a retina panel
- whether the four platforms should share more artwork than a metaphor
