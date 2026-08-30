# MegaPDF — design

Everything here is taken from the repository. Where the repo and this document
disagree, the repo is right and this is stale.

---

## The product, in its own words

> **Open. Fix. Save. Done.**
>
> *— `README.md`, under the logo*

> A free, open-source, lightweight PDF editor built for people who find Acrobat
> too bloated and complex.
>
> *— `README.md`, first line*

Four things, deliberately nothing else. The README's own order, which is not the
order the apps present them in:

1. **Edit text** — click any text in the document and type, like editing a Word file
2. **Check boxes** — click an empty square and it becomes a checked box
3. **Apply signatures** — keep a small library; drag one onto the page
4. **Save** — Save overwrites, Save As creates a copy. No export wizards, no "flatten" dialogs

Plus **Find**, added later because long documents need it.

> No account. No cloud. No subscription. All processing is local.

That last line is a design constraint as much as a promise: **there is no sign-in
screen to design, and there never will be.**

---

## What organises the page

Read `src/MegaPDF.Core/Engine/IPdfEngine.cs` and the product's shape is not a
timeline, a catalog or a pipeline. It is **a surface you point at**. Every core
type is the same triple — an identity, a rectangle, and some state:

```csharp
public sealed record StampInfo(string Id, PdfRect Bounds);
public sealed record PdfFormField(string Name, FormFieldKind Kind, PdfRect Bounds, string Value, bool IsChecked);
public sealed record PdfTextRun(int ObjectIndex, string Text, PdfRect Bounds, string FontName, double FontSize, ...);
```

and the central operation is:

```csharp
/// <summary>What is under this point? Drives cursor affordances and click routing.</summary>
PageHit HitTest(PdfPoint point);
```

`PageHit` is the whole interaction model. Everything the user can do is "point at
a rectangle and act on what is there". The product spec says the same thing in
words:

> **P2 — The document is the interface.** Users interact with the page itself
> (click text, click boxes, drop signatures), not with tool palettes that change
> what a click means.

**So the page dominates, and chrome earns its place.** Any design that grows the
toolbar at the page's expense is working against the product's own shape.

---

## The five principles the design answers to

From `SDD.md` §1.3, which states they override feature requests:

| | |
|---|---|
| **P1** | Zero learning curve — *"If a feature needs explaining, the design is wrong."* |
| **P2** | The document is the interface |
| **P3** | Never lose work, never surprise |
| **P4** | Small and fast — under 60 MB installed, under 200 MB idle, sub-2s cold start |
| **P5** | *"It looks like Windows 11."* |

**P5 is now wrong and should be rewritten.** It was authored when the product was
one Windows app. There are four: Windows (WinUI 3), macOS (Avalonia), iOS
(SwiftUI), Android (Compose). The principle it was reaching for — *look native to
the machine you are on* — still holds; the wording does not.

---

## Visual identity, as it exists

> **The framework built from this document is `docs/design-tokens.md`.** This
> file describes what exists; that one says what to build, and names the type and
> spacing scale that is missing below. Where the two disagree, the token file wins.


### The mark

`assets/branding/icon.svg`, with its own comments describing the construction:

- a **Windows 11 style tile**, `256×256`, corner radius `58`
- a **document with a folded corner**, in `#D6E7F8` on `#C9DCEF` text lines
- and then, in the file's own words: *"The one stroke: a calligraphic check,
  curved entry, straight rise"* — `M92 148c8 4 18 13 30 24L168 104`

That single stroke is the idea: not a document, a **document that has been dealt
with**. Whatever replaces it should keep that thought.

### Palette

From `assets/branding/*.svg` — the only place a deliberate palette exists:

| Token | Value | Where |
|---|---|---|
| Brand blue | `#0E6FD8` | gradient start, wordmark |
| Brand cyan | `#18B6C8` | gradient end, wordmark |
| Tile blue | `#0A5BC4` → `#0FA8C6` | icon tile gradient |
| Ink | `#16324F` | "Mega" wordmark, light theme |
| Ink, dark theme | `#F2F7FC` | same, via `prefers-color-scheme` |
| Page | `#D6E7F8` | document face in the mark |
| Page lines | `#C9DCEF` | text lines in the mark |

When this was written, **none of these colours appeared in any of the four
applications** — the Mac app used `#3B82F6` and `#1D4ED8` for selection and
focus, chosen by a developer with no reference to the brand, and it was the
single largest identity gap in the product.

That is fixed. Each app now has one file that holds the palette and is the only
place in it that names a colour: `Themes/Brand.xaml`, `Brand.axaml`, `Brand.swift`
alongside `Assets.xcassets`, and `ui/Brand.kt`. `docs/design-tokens.md` §5 is the
map. The gap this section described is closed; what remains open is the icon work
in #73 and the QA sweep in #80.

### Ink

One value is shared across every platform and is not a preference — it is what
the user's marks are drawn in:

```
#202020   check marks, drawn signature ink
```

Near-black rather than black, so a mark reads as ink on paper rather than as UI.

**Only the check mark honours it.** Drawn signature ink is a different colour on
every platform — `PdfiumEngine` and `PdfEngine+Checkboxes` stroke marks at
`0x202020`, but the pads do not:

| | Value | |
|---|---|---|
| macOS | `SignaturePad.cs:26` — `0x202020` | correct |
| Windows | `MainWindow.xaml.cs:1308` — `Colors.Black` | `#000000` |
| iOS | `SignatureViews.swift:76,146` — `0.10` grey | `#1A1A1A` |
| Android | `DrawSignatureDialog.kt:38` — `0xFF1A1A1A` | `#1A1A1A` |

Background removal and trimming survive all four, so nothing is broken — but a
signature and a tick placed on the same page are drawn in different inks on three
of the four apps.

### Contract values that are not style

These look like design tokens and are not. They are cross-platform behavioural
contracts (`SDD.md` §6.2) — changing one is a breaking change on four platforms:

| | | |
|---|---|---|
| `235` | white-luminance cutoff | signature background removal |
| `16` | ink alpha cutoff | signature trim |
| `4` | trim margin, px | signature trim |
| `6`–`24` | drawn checkbox size, pt | which squares read as tickable |
| `0.25` | squareness tolerance | same |
| `150` | target DPI | shrink for email |

A designer may not change these. They are listed so nobody tries.

### Type and spacing, as currently used

Collected from `src/MegaPDF.Avalonia/Views/*.axaml` — descriptive, not
prescriptive, and the spread shows nobody has set a scale:

- **11** — toolbar labels (16 uses)
- **12** — helper text under a setting
- **13** — the signature dialog's rule marker
- **16** — recovery dialog headline
- **28** — empty-state product name

- **3** — icon-to-label gap (16 uses)
- **6, 8, 14** — toolbar groups, dialog stacks, empty-state stack
- **8,6** and **10,5** — toolbar and status-bar padding
- **16** — gap between pages in the document view
- **24** — `MatchScroll.Margin`, how close a search hit may sit to the edge

There is no scale here, only values that were reasonable one at a time. **A real
type and spacing scale is wanted** and does not exist.

---

## Voice

The app talks in short sentences aimed at someone holding a form. Real strings,
from `MainViewModel`:

- *"Open a PDF to get started."*
- *"demo.pdf — 1 page. Click a checkbox to tick it."*
- *"Drag to move, corners to resize, Delete to remove."*
- *"Click where the new text should go — Esc cancels"*
- *"That text is part of a scanned image, so it cannot be edited. You can cover it
  and type over the top instead."*
- *"Reopen this document before shrinking it."*

Two habits worth keeping: it says what will happen next, and when it refuses it
says why **and offers the thing that would work**. It never names an internal
concept — no "AcroForm", no "annotation", no "object index" — even though the code
is full of them.

Terminology the product uses for itself: the task is **fill-check-sign-save**
(`SDD.md` §1.3). The persona is **"Pat, the office administrator"** (§2.1) — not
technical, does this a few times a week, resents every minute.

---

## Where the current design falls short

Honest reading of `docs/design-assets/` (screenshots produced by
`macos-screenshots.yml`), listed so a designer does not have to be tactful:

1. **The brand is absent from the product.** See the palette above.
2. **The README leads with *Edit text*; every app leads with viewing.** The thing
   the product says it is best at is the fourth button along and looks like a
   text cursor. Either the README's order is wrong or the toolbar's is.
3. **Sixteen toolbar actions in one row**, plus two dropdowns, all equal weight.
   Nothing signals that Sign matters more than Actual size.
4. **The empty state is mostly grey.** Right words, right order, doing nothing
   with the space — and it is the first thing a new user sees.
5. **The Mac app has no icon at all.** No `CFBundleIconFile`; it shows the generic
   blank document in Finder and the Dock. That is a defect, tracked in #73.
6. **`P5` names one OS.** See above.

---

## What is fixed and what is open

**Fixed — technical constraints, not preferences:**

- macOS cannot use an icon font. Windows draws its toolbar from Segoe MDL2, which
  does not exist on macOS; bundling a font for sixteen glyphs adds a licence and a
  download. **Vector source is required.**
- Every app follows the OS light/dark setting. Four states per icon: enabled and
  disabled, light and dark.
- Toolbar glyphs render at **18×18** points on a **24-unit** grid.
- Meaning is never carried by colour alone — the app is fully keyboard-driven
  (#2) and used with screen readers.
- The contract values above cannot move.

**Open — everything else**, including the toolbar's arrangement, the empty state,
the type scale, whether icons are stroked or filled, and the mark itself.
