# MegaPDF — design brief

For a designer producing the visual assets across all four platforms. Written by
the engineering side, so treat every judgement here as a starting point you are
welcome to overturn — the constraints, though, are real.

---

## 1. What the product is

MegaPDF fills in PDFs. Someone is sent a form — a rental agreement, a school
consent slip, an insurance claim — and needs to type into it, tick the boxes,
sign it, and send it back. That is the whole product.

It is deliberately **not** a PDF editor, not a reader, and not a creation tool.
It does not make PDFs, reorder pages, or do OCR.

**The user.** The product spec names her Pat: an office administrator, competent
but not technical, who does this a few times a week and resents every minute of
it. She is not a designer, does not know what a "form field" is, and should never
have to. If a control needs explaining, it is the wrong control.

**The tone we have aimed for.** Plain words, no jargon, nothing that looks like a
professional tool. The status bar says *"Click a checkbox to tick it."*, not
*"AcroForm widget interaction enabled."* Please hold us to that in the visuals
too — this should look calm and unintimidating, closer to a well-made utility
than to Acrobat.

**Non-negotiable, and worth knowing because it shapes the personality:** no
accounts, no cloud, nothing leaves the machine. There is no sign-in screen to
design, and there never will be. That is a selling point and the design can say
so.

---

## 2. Where it runs

Four apps, one product:

| Platform | Built with | Store |
|---|---|---|
| Windows | WinUI 3 | Microsoft Store + direct download |
| macOS | Avalonia | Mac App Store |
| iOS | SwiftUI | App Store |
| Android | Jetpack Compose | Google Play |

The desktops do everything. The phones do a reduced set — view, tick, sign,
find, save — with no text editing, no whiteout, no printing.

---

## 3. What we need from you

### 3.1 App icons — all four platforms

**One is urgent and is a defect:** the macOS app currently ships with **no icon at
all**. It shows the blank generic document icon in Finder, the Dock and ⌘-Tab.

| Platform | Needed | Notes |
|---|---|---|
| macOS | `.icns`, full size set to 1024 | Nothing exists today |
| Windows | `.ico` + Store tile set | Something exists; worth revisiting with the rest |
| iOS | 1024 master for the asset catalog | Exists |
| Android | Adaptive icon: foreground + background layers | Exists |

**The idea we would like the mark to carry:** not "a PDF". Everyone's PDF app is a
red document. This one is about *getting the form done* — filled, ticked, signed,
sent. If there is a mark that says that without being literal about paperwork, it
is worth more than another page-with-a-corner.

### 3.2 Toolbar and in-app icons

Currently sixteen actions: Open, Save, Save As, Print, Shrink, Sign, Add text,
Cover, Undo, Redo, Zoom out, Zoom in, Actual size, Fit width, Fit page, Options.

Each is drawn above a text label, and **the labels should stay** — but they are
currently load-bearing, which is the problem. Three in particular are not
guessable:

- **Cover** — paints a white box over something to hide it (Tipp-Ex, essentially)
- **Shrink** — saves a smaller copy so it can be emailed
- **Add text** — types new text onto the page, as opposed to editing text already there

If those three read at a glance, this has succeeded.

### 3.3 A written set of rules

Grid, stroke weight, corner radius, the metaphor vocabulary. Short is fine. It is
what stops the next feature's icon restarting the drift.

---

## 4. Constraints that are real

- **macOS cannot use an icon font.** Windows draws its toolbar from Segoe MDL2,
  which does not exist on macOS, and bundling a font for sixteen glyphs adds a
  licence and a download. **Vector source (SVG) is what we need**, exported per
  platform.
- **Line art, not filled shapes**, unless you tell us otherwise — the current set
  is stroked. If you prefer filled, say so and we will change the rendering.
- **Both themes.** Every app follows the OS light/dark setting. Every icon appears
  enabled *and* disabled, on light *and* dark: four states, contrast must hold in
  all of them.
- **Small sizes.** Toolbar glyphs render at 18×18 points. A Dock icon is glanced at.
- **Accessibility.** Meets contrast guidance, and **never carries meaning by colour
  alone** — the app is fully keyboard-navigable and used with screen readers.
- **Each platform's own idiom.** We want one family in four dialects, not one set
  pasted four times. An icon that looks native on Windows should look native on
  macOS too, and those are different things.

---

## 5. What it looks like today

`docs/design-assets/` holds current screenshots of the macOS app. Windows, iOS and
Android screenshots come from their own workflows and can be regenerated on
request — ask and we will attach a fresh set.

Our own read of the current state, offered so you do not have to be polite about
it:

- **The macOS toolbar icons were drawn by an engineer** (me) while closing a
  feature gap. They are consistent and they scale. That is the extent of their
  ambition, and Cover, Shrink and Sign are guessable at best.
- **The toolbar is crowded** — sixteen actions plus two dropdowns in a single row.
  Grouping, overflow, or a different arrangement is entirely open.
- **The empty state is mostly grey.** It says the right words in the right order;
  it is not doing anything with the space.
- **The document itself renders well.** Text, tick marks and signatures all look
  right. The page is not the problem — the chrome around it is.

---

## 6. Deliverables

1. **`CFBundleIconFile` + `.icns` for macOS** — first, because it is a live defect
2. App icons for the other three platforms in their native formats
3. Toolbar icons as SVG source plus per-platform exports
4. The short written rules from §3.3
5. Optional and welcome: a view on the empty state and the toolbar arrangement

## 7. What we will do

Wiring assets in is straightforward and ours. Say what you need in what format and
we will make sure it is exactly that. If a constraint above is getting in the way
of something better, push back — most of them are technical facts, but the
technical facts have workarounds.
