# MegaPDF — design tokens

The framework the four apps follow so they look like one product: **one palette,
one lockup, one semantic type and spacing scale.** Platform chrome stays native —
a Windows app should look like Windows and a Mac app like a Mac — but the colour a
selection is drawn in, and the mark in the corner, are the same everywhere.

`design.md` describes what exists. This file says what to build. Where they
disagree, this file wins and `design.md` gets corrected.

Every value is labelled:

- **Brand** — from `assets/branding/*.svg`. Not open for revision; this is the identity.
- **Proposed** — chosen here because nothing existed. A designer may revise under #73.
- **Contract** — `SDD.md` §6.2. Changing one is a breaking change on four platforms.

---

## 1. Colour

### 1.1 Brand

| Token | Value | Source |
|---|---|---|
| `brand.blue` | `#0E6FD8` | gradient start, wordmark |
| `brand.cyan` | `#18B6C8` | gradient end, wordmark |
| `brand.tile.start` | `#0A5BC4` | icon tile gradient |
| `brand.tile.end` | `#0FA8C6` | icon tile gradient |
| `brand.ink` | `#16324F` | "Mega" wordmark, light theme |
| `brand.ink.dark` | `#F2F7FC` | same, dark theme |
| `brand.page` | `#D6E7F8` | document face in the mark |
| `brand.page.line` | `#C9DCEF` | text lines in the mark |

### 1.2 Semantic — **proposed**

These are what the apps actually reference. Nothing in application code names a
raw hex value; it names one of these.

| Token | Light | Dark | Role |
|---|---|---|---|
| `accent` | `#0E6FD8` | `#4F9BEA` | primary action, selection border, focus ring |
| `accent.pressed` | `#0A5BC4` | `#2D7FD4` | pressed and active states |
| `accent.subtle` | `#0E6FD8` @ 13% | `#4F9BEA` @ 18% | selection fill, hover wash |
| `accent.on` | `#FFFFFF` | `#0B1B2B` | text and icons on top of `accent` |
| `find.match` | `#18B6C8` @ 30% | `#18B6C8` @ 38% | every search hit |
| `find.match.current` | `#0E6FD8` @ 45% | `#4F9BEA` @ 50% | the hit you are on |
| `danger` | `#C0362C` | `#E2685E` | delete, discard, destructive confirm |

Two notes on choices made here:

- **`accent` is the brand blue, not the OS accent.** Windows borrowed the user's
  personalisation colour, which can be any hue at all; the Mac app used Tailwind's
  `#3B82F6`. Neither is MegaPDF. This means *not* using `SystemAccentColor` for
  product chrome — an accepted, deliberate divergence, because brand identity
  across four platforms outranks matching the host on one of them.

  **This extends to the framework's own controls.** The first pass changed only
  the chrome the markup names, which left the app wearing two accents at once: a
  `#0E6FD8` mode banner beside a `#007AFF` toggled toolbar button that Fluent had
  styled from the OS. Two accents is worse than either choice alone, so each
  desktop app also overrides the framework accent ramp — `SystemAccentColor` and
  its six Light/Dark steps — in its own token file.
- **Find highlighting uses cyan for hits and blue for the current one.** Android
  currently uses Material blue for hits and *amber* for the current hit. Amber is
  not in the brand, and two brand hues distinguish the states without importing a
  colour the product does not own.

### 1.3 Contract — do not change

| Value | Meaning | Where |
|---|---|---|
| `#202020` | ink for check marks **and drawn signatures** | `PdfiumEngine`, `PdfEngine+Checkboxes`, every signature pad |
| `235` | white-luminance cutoff | signature background removal |
| `16` | ink alpha cutoff | signature trim |
| `4` | trim margin, px | signature trim |
| `6`–`24` | drawn checkbox size, pt | which squares read as tickable |
| `0.25` | squareness tolerance | same |
| `150` | target DPI | shrink for email |

`#202020` is not a theme colour and never inverts. It is what the *user's* mark is
drawn in — ink on paper, inside the document, not part of the UI. It is the same
value in light theme, dark theme, and the saved PDF.

---

## 2. Type — **proposed**

Semantic steps, mapped to each platform's native metric. The step is the same
everywhere; the pixel size is whatever that platform's users expect.

| Token | Role | Windows | macOS | iOS | Android |
|---|---|---|---|---|---|
| `type.caption` | toolbar labels, status bar, helper text | 12 | 11 | `.caption` | `labelSmall` |
| `type.body` | dialog text, list items, document chrome | 14 | 13 | `.body` | `bodyMedium` |
| `type.subtitle` | dialog headline, section heading | 16 | 16 | `.headline` | `titleMedium` |
| `type.title` | empty-state product name | 28 | 28 | `.largeTitle` | `headlineMedium` |
| `type.display` | splash only | 48 | 48 | — | — |

Faces: **Segoe UI Variable Display** / Segoe UI on Windows, **SF Pro Display** /
SF Pro Text on macOS and iOS, **Roboto** on Android — each platform's system face,
never a webfont.

iOS and Android already work this way and need no size changes; they need the
scale *named* so the desktop pair can be held to it. Windows and macOS hardcode
raw numbers today and are the work.

---

## 3. Spacing — **proposed**

A 4pt grid. Current code contains 3, 6, 10 and 14, which are off-grid and were
each reasonable in isolation.

| Token | Value | Typical use |
|---|---|---|
| `space.xs` | 4 | icon-to-label gap |
| `space.s` | 8 | inside a control, between toolbar items |
| `space.m` | 12 | between toolbar groups |
| `space.l` | 16 | dialog padding, between pages in the document view |
| `space.xl` | 24 | dialog stack gaps, section separation |
| `space.xxl` | 32 | empty-state stack |

Migration for the values that exist: 3 → 4, 6 → 8, 10 → 8, 14 → 16.

`MatchScroll.Margin = 24` is already on-grid and stays.

### Corner radius

| Token | Windows | macOS | iOS | Android |
|---|---|---|---|---|
| `radius.control` | 4 | 6 | 10 | 8 |
| `radius.surface` | 8 | 10 | 14 | 12 |

Platform-native on purpose. The brand tile's own radius (58 on 256, ~23%) belongs
to the icon, not to UI controls.

---

## 4. The lockup

`assets/branding/logo.svg` — the tile plus "MegaPDF", ratio 620:168.

Rules, all four platforms:

- Use the SVG. Never a rasterised copy, never an icon font, never text spelling
  out the product name next to a separate tile.
- Size it by **height**; width follows. It is never stretched, letterboxed, or
  padded into a square.
- Transparent ground. No card, plate or tile behind it.
- Tile-only (`assets/branding/icon.svg`) is correct where the space is square —
  app icon, taskbar, dock, launcher. The lockup is for horizontal space.

**Where it goes on each screen is not specified here.** Placement is a per-app
decision made in the app, against that platform's conventions.

---

## 5. Where each platform defines these

One file per app. Nothing outside it names a raw colour.

| Platform | File | Mechanism |
|---|---|---|
| Windows | `src/MegaPDF.App/Themes/Brand.xaml` | `ResourceDictionary`, merged in `App.xaml`, with `ThemeDictionaries` for Light/Dark |
| macOS | `src/MegaPDF.Avalonia/Brand.axaml` | `ResourceDictionary` with `ThemeVariant` scopes, merged in `App.axaml` |
| iOS | `ios/MegaPDF/Assets.xcassets/*.colorset` + `ios/MegaPDF/Brand.swift` | asset catalog for any/dark appearances; `Brand.swift` names the steps |
| Android | `android/app/src/main/java/com/megapdf/android/ui/Brand.kt` | Compose `lightColorScheme` / `darkColorScheme`; the app is pure Compose and has no `colors.xml` |

iOS has **no `AccentColor` colorset at all** today, so every `Color.accentColor`
in the app resolves to Apple's system blue. Creating it is most of that platform's
colour work.

---

## 6. Reference renders

`docs/design-assets/stitch/` — `windows-*.png` and `macos-*.png`, generated from
this palette.

They show **chrome idiom and colour**, and nothing else is normative:

- Windows: merged title/command bar, caption buttons top-right, bottom status bar,
  centred modal dialog, 4px radii.
- macOS: unified translucent toolbar, traffic lights top-left, no bottom status
  bar (a floating page/zoom pill instead), signature dialog as a top-attached
  sheet, 6px radii.

Logo *placement* in those renders is not a specification — see §4. Sample document
text in them is invented and is not product copy.
