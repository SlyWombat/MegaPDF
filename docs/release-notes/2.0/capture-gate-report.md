# 2.0 capture gate — the consolidated report (#146 §3)

Every store capture set that exists for 2.0.0, measured by `tools/capture-gate`
and set beside the human-eye review each of those sets already had. Built to be
read at speed: the verdict first, then the short list of images worth opening,
then where the tool and the eye disagreed.

**Nothing here was uploaded anywhere.** Every set is sitting on the machine that
shot it.

The **preview videos** are the other half of §3 and have their own report:
[`preview-videos.md`](preview-videos.md). Nine clips, 270 stills, six defects
found by reading them, and one measured change to this tool — `ink_tolerance`,
which the `video` profile sets and no other profile does, so nothing below is
measured any differently than it was.

---

## 1. The verdict

| store | slot | images | checks passed | not run | **to look at** |
|---|---|---:|---:|---:|---:|
| **Mac App Store** | 1440 × 900 | 18 | 150 | 24 | **0** |
| **App Store (iOS)** — listing | 1320 × 2868, 2064 × 2752 | 36 | 258 | 114 | **0** |
| **App Store (iOS)** — review | the same, plus dark | 30 | 210 | 100 | **0** |
| **Google Play** — phone | 1080 × 2400 | 24 | 171 | 77 | **0** |
| **Google Play** — tablet | 1600 × 2560 | 24 | 171 | 77 | **0** |
| **Microsoft Store** | 2482 × 1541 | 15 | 137 | 8 | **0** — see §5 |
| Linux (not a store yet) | 480–1280 wide | 234 | 1 280 | 972 | 10 → **#237** |

All six store sets are **gate-clean**, and every one of them was also read
image by image by a person. The two reads agree.

The Linux set is the odd one out because it is a QA matrix rather than a
listing: its ten are all at the 480 × 360 minimum window, and both things they
show are real defects, filed as **#237**. Linux ships after the other four, so
neither blocks 2.0.

*"Not run" is a count, not a gap:* it is checks that stood down and said why —
`zoom` on a platform with no zoom control, `language` on an English set,
`person` on the shots that pose no name.

## 2. What a person should still look at

Short, and none of it is a defect the tool found:

1. **One image per device, properly.** `light-01-viewer.png` (Mac),
   `iphone-6_9-viewer.png` and `ipad-13-viewer.png` (iOS), `android-viewer.png`
   on each of phone and tablet (Play). Everything the gate does is a
   comparison, so anything identical in *every* image of a set is invisible to
   it — see §4. These five are where that lives.
2. **The two French listings, side by side.** fr-CA and fr-FR are **the same
   image** in 5 of 6 Mac poses, 4 of 12 iOS listing poses and 7 of 8 Play poses.
   That is correct — the demo person is the only string that differs between
   the two catalogues on any listing screen — but three listings go up looking
   alike, and whether that is wanted is a decision, not a defect.
3. **Whether each set sells the app.** Nothing here measures whether a capture
   makes someone want MegaPDF, whether the pose matches what the listing copy
   promises, or whether the French sounds like a person wrote it. Those are
   still yours; the difference is that they are now the *only* ones.

## 3. Where the tool and the eye disagreed

Both directions, which is the point of running both.

### What the eye caught that the tool missed

| | |
|---|---|
| **The iPad's date, after a run crossed midnight** | The English set read *Thu Sep 17* and both French sets *Fri Sep 18*: `simctl status_bar` overrides the time and the indicators, not the date. **The gate would have missed it** — and does not now. The status bar is compared whole across languages rather than only its right-hand end, and the band stops per device above the iPad's navigation bar. Planted back into a copy of the set, it lights up all six iPad poses in both French sets at ~37 % of the bar's ink, and leaves every iPhone pose alone. |
| **The launcher taskbar in 24 Play tablet captures** | Still missed, and structurally — §4. |
| **`Mega PDF` against `MegaPDF`** | The gate reads text but has no idea which spelling is meant. Decided 2026-09-18: **MegaPDF** everywhere. The Windows title bar and About said "Mega PDF"; the Microsoft set in §5 was shot after that change and reads MegaPDF. |

### What the tool caught that the eye missed

Nothing, in these five sets — which is the right answer when five people have
already read them image by image. Where it *has* earned its keep is the set
nobody re-read: **234 Linux captures, already reviewed by hand for #158, held
two real defects at the minimum window size** (#237).

### Where the eye was right and the tool was wrong

| | |
|---|---|
| **"Hélène Bélanger without her accents"** on a 420 dpi Android capture | The screen magnified reads *Hélène Bélanger* with all three accents; tesseract returned them flat. The check now refuses to testify about accents on an image where the reader resolved none anywhere — it says so and stands down. (On this machine's container the same image reads correctly, so the reader, not the image, is the variable.) |
| **`size` stood down 48 times on Play** | It was looking for a slot named `desktop` that the Play profile does not define. It now finds the slot by size, names it, and prints the ratio — which is what the open Play Console question needs: **1080 × 2400 is 9:20, ratio 2.222**. |

### What both reads agree on, independently

- The Mac's fr-CA and fr-FR sets are the same image in five of six poses.
- iOS's captures are not pixel-deterministic and the Mac's are: the gate's
  layout comparison tolerates the one-to-two pixel drift of a presented sheet,
  and its status-bar comparison does not touch it.
- Play's status bars are **byte-identical** across the three languages on both
  devices — the posed 9:41, the full battery, no badge.

## 4. What the gate cannot see, and why

**Anything that is the same in every image of a set.** Every check is a
comparison — against a slot size, against the same pose in another language,
against a set already signed off. Device chrome has no sibling to disagree with
it, so:

- the **launcher taskbar** across all 24 Play tablet captures is cleared by this
  tool, today, knowing it is there;
- the **Wi-Fi badge** of the September dry run was the same shape of thing.

Two rules were tried against the data and neither survived it:

| rule tried | why it was dropped |
|---|---|
| *foreign colour in the top and bottom bands* — the taskbar's icons are green, blue and yellow, and MegaPDF's chrome is grey, black, white and one blue | the **fixed** tablet set carries more of it than the broken one: 4 947 px against 4 528, and the clean phone 2 911. The app's own bar and Android's battery are colourful too. |
| *the bottom band should be painted in a tone the app uses elsewhere* | the clean tablet's bottom band tone covers 4.3 % of the rest of its image and the broken one's 2.5 %. Too close to separate, and the ordering is not even stable between poses. |

So the sheet names the constants instead: one image per device and appearance,
with a line saying why that one needs an eye. A check that agreed with the
answer would have been tuned to it; this is the honest version.

## 5. The Microsoft Store set

Shot 2026-09-18 07:14–07:22 EDT on GPD-DAVE, from the installed
**ElectricRV.MegaPDF 2.0.0.0** package (checked before each run; About reads
2.0.0), built from the tree that renamed the product to MegaPDF. English, fr-CA
and fr-FR, five poses each, 2500 × 1550 window at 150 % scale (1667 effective px,
full-label toolbar), 100 % zoom, one-entry MegaWoman signature library. The
driver is `tools/screenshots-windows/Shoot-Set.ps1`. The set is in
`artifacts/store/screenshots/{en,fr-CA,fr-FR}/` and its sheet is beside it in
`artifacts/store/gate-microsoft/`. The pre-2.0 set is kept aside in
`artifacts/store/screenshots-pre2.0/`.

| language | images | checks passed | not run | **to look at** |
|---|---:|---:|---:|---:|
| en | 5 | 39 | 6 | **0** |
| fr-CA | 5 | 49 | 1 | **0** |
| fr-FR | 5 | 49 | 1 | **0** |

`person` read *Jane Whitfield*, *Hélène Bélanger* and *Céline Lefèvre* with
their accents intact in the four poses that show the name. `zoom` read 100 % in
all 15. `toolbar` found one row. `language` found no English on either French
set, and `privacy` found no home-folder path. The shrink pose poses no name, so
`person` stands down there, and `language` does the same on English.

**The English person is now Jane Whitfield on Windows too.** Windows alone still
used "Dana Whitfield" (`gen_store_docs.py`), which the gate's `person` check
would have failed and the listing would have shown against every other store's
Jane.

### The `microsoft` profile, the first time it met a real set

It had never met a real set, and the first run flagged all 15 images. None of
those flags was a defect in an image. All of them came from the profile being
written from a README rather than measured. Each fix below is a measurement, and
each fix was then tested by planting the defect it could have hidden, in a copy
of the set:

| flag | what it really was | fix | planted to prove it still bites |
|---|---|---|---|
| `size`: 2482 × 1541 is not a slot | The Store has no fixed desktop slot. Its only rule is **at least 1366 × 768** (docs/microsoft-store-listing.md § Screenshots), and the 1.7 listing went up at this exact non-16:9 size. The profile listed 16:9 sizes and a 2500 × 1550 frame the harness never produces, because the DWM bounds of a 2500 × 1550 window are 2482 × 1541. | The frame is 2482 × 1541. Any size at or above the floor passes. | — |
| `clipping` on three edges of every image | **Real, but not clipped text.** The DWM bounds take in Windows 11's 1 px border, and the capture reads the screen, so the outer **two pixels on every side are border over the wallpaper**. Windows Spotlight had just put a textured photo behind the window, and those lines read as 70–700 "strokes". Two pixels in, nothing. | `edge_inset: 2`. The check starts inside the border. | A word pasted against the right edge is flagged (`right: 3 strokes in 14 px`). |
| `accent` in every image | **The app's own icon** in the title bar: a blue 24 px square at (13, 10), about 400 accent px in every shot. In the two editor poses, the inline editor's **focus underline** is drawn in the accent, which is the edit being shown. | `accent_ignore` for the icon's corner; `edit-text` and `add-text` are poses that draw the accent. | A 2 px accent box around the name in the checkbox pose is flagged (1,112 px). |
| `toolbar`: two rows (after the size fix) | Windows paints the title bar, the command bar and the canvas in one Mica backdrop. So the band had no edge to stop at, ran on to 200 px, and counted **the page's top edge** (row 153) as a second row of buttons. And `zoom`, reading "the first row of ink", read **the title bar**. | The band is fixed at 150 (`fixed`). The title bar, rows 0–45, is its own strip, and `toolbar` and `zoom` start below it. Only gaps of 4 rows or less are closed, because the labels sit beside the icons. | — (no two-row frame exists at this size; a pre-#144 bar would sit a few pixels under the first and stay separate at `gap: 4`) |
| — | The listing order was spelled `textedit`/`checkboxes`/`addtext`, which match no file. | The order is the file numbers: edit-text, checkbox, signature, shrink, add-text. | — |

A red squiggle drawn under the name in the add-text pose is flagged as well
(`squiggle`, 98 px). The fixes left that check as it was.

### Where the tool and the eye disagreed, this set

**What the tool caught that the eye missed.** The two-pixel wallpaper fringe
above: invisible at listing scale, but real, and on every Windows capture the
harness has ever taken. It is not worth a re-shoot. The rounded corners show
the desktop too, which is simply how a Windows 11 window looks.

**What the eye caught that the tool did not look for**, and worth a glance, none
of it a defect:
- **The run was spoiled once, by a person, not by the app.** Partway through the
  first fr-FR run someone started sharing the desktop, and a "Your desktop is
  currently shared with …" banner with an email address landed over the page.
  The later clicks missed, so no box was ticked. Those images were deleted and the
  language was re-shot. The gate would have flagged the missing ticks through
  `siblings`, but a banner like that on an otherwise correct frame is exactly the
  one-image problem §4 describes.
- **The backdrop changed between runs.** Spotlight rotated the wallpaper at about
  07:05, and Mica took on its colour: one French set came out beige while the
  other two were pink and blue. All three were re-shot on the new wallpaper, so
  the set agrees. A capture machine on Spotlight will do this again. A fixed
  wallpaper for the shoot would stop it.
- **Keyboard focus shows in two places:** a dark focus square around the second
  checkbox in every `02-checkbox` (all three languages alike), and a heavier
  outline on the dialog's OK in fr-CA's `04-shrink` only. The accent check can't
  see either one, because neither is drawn in the accent.
- **The inline editor sits over the next line.** In `01-edit-text`, the open
  editor's underline runs through the top of the "Rental period" line under it.
  That is what the app does, so it is a pose choice, not a capture fault.

## 6. The full-set review — Dave's box (2026-09-18, 23:55 EDT)

Dave handed over "Dave reviews the full set" (#146 §3): *"The full screen shot set,
you can review those yourself."* The French was signed off by the human reviewer
the same evening. This section covers the Mac, iOS and Microsoft sets. The two Play
sets belong to the Android track, and are in §7.

**One French pose changes after this read.** Later that night Dave asked for the
Fable French review's recommendations (#242) to be applied, so fr-CA and fr-FR
strings change on every platform. The French agent's list of changed strings
that are visible in a store capture has one entry: the Mac redact hint, where
« …sur l'élément ciblé… » becomes « …sur l'élément actif… ». That puts
`light-05-redact.png` in fr-CA and fr-FR in the re-shoot, from a Mac build that
includes the #242 merge. Every other pose and every preview clip is unaffected,
and English doesn't change.

**How it was read.** Every listing image was opened: 18 Mac, 36 iOS listing, 30 iOS
review and 15 Microsoft. Each pose was also read with en, fr-CA and fr-FR side by
side. Images that are pixel-identical between fr-CA and fr-FR (hashed first) were
read once. The nine preview clips were skimmed at six stills per device rather than
re-read (§3 of `preview-videos.md` already reads all 270).

### Staleness: nothing to re-shoot

Every commit since each set was shot was listed against the code the captures show.
Mac and iOS sets are from `476ce15`; the Microsoft set is from the rename's tree
(`8a77b8c`).

| commit | what changed | does a capture change? |
|---|---|---|
| `136aa6b` #268 | Windows Redact / Whiteout / Add text expose a toggle state | **No.** They are the same `AppBarButton` template, "pixel-identical off and armed", per the commit's own measurement. `05-add-text`, the one armed pose, stands. |
| `0989c70` #259, `771bcae` #261, `7966fc1` #145 | Windows placement below 100 %, first click after a drag, recovery order | No. Every pose is at 100 %, and none shows a drag or a recovery. |
| `01ba248` | the phones' redact hint loses "— Esc cancels" | No on iOS. The review `redact` poses are taken after the mark, with the tool off, so no hint banner is on screen. |
| `476bfff` #172 | iOS toolbar label style removed | No. It was after `476ce15`, but the commit measured that the style never reached the screen: the iPad bar is icon-only either way, and the captures show exactly that. |
| `3d4c1de`, `ef86ca3` #237 | Avalonia find bar and empty state at the 480 px minimum | No. At 1440 × 900 the find bar is at its full step, and the empty state's list margin is 16, the same as `SpaceL` (`Brand.axaml:118`), so `light-03-search` and `light-06-home` lay out as shot. |
| `6033f30`, `a6af591`, PDFium `606c006`/`7cf6ff0` | Linux shortcuts, recovery order, engine | No visible change on these screens. |

### Per set

| set | verdict |
|---|---|
| **Mac App Store**, 18 | **Ready, except `light-05-redact` in fr-CA and fr-FR, which is re-shot for #242.** It leads with a filled, signed agreement. Text, search, the signature picker and redaction follow, then home. That is the story the copy tells, in its order. fr-CA and fr-FR read naturally ("Caviarder", "Masquer", "Ajouter du texte" and "100 %" with its space all fit the toolbar). The weakest image is `light-06-home`: the empty state's grey wall reads like a dimmed modal. It is last in the set and it is what the app looks like. |
| **Microsoft Store**, 15 | **Ready.** Edit text, checkbox, signature, shrink and add text in all three languages, and the title bar reads *— MegaPDF*. The French is consistent with the glossary: *Correcteur* is the toolbar noun and *masquer* the verb. The shrink dialog is correct in each language (fr: "Avant : 3,2 Mo, après : 0,1 Mo", "courriel"). |
| **App Store (iOS)**, 36 + 30 | **Two defects in the listing, both on screen in every set.** Neither is a capture fault, and neither is caught by a comparison, because each is in every image. See below. The composition and the French are otherwise good: the demo person, keyboards per locale (Canadian QWERTY with ç/è/à against French AZERTY) and the dark review poses all have good contrast. |

### Found on this read

1. **iOS, light mode: the document name and the status bar are black on the
   viewer's dark wall.** `Brand.backdrop` is `Color(white: 0.25)`
   (`ios/MegaPDF/Brand.swift:38`). Under iOS 26 the navigation bar is transparent
   over it, so the inline title and the status-bar clock draw in the light
   scheme's black on `#404040`, a **contrast ratio of 2.0 : 1** (1.7 : 1 on the
   `#333` behind a sheet), under WCAG's 3 : 1 even for large text. It is in 8 of
   the 12 listing images per language: every pose but `home`, and `search`, whose
   find bar lightens the top. In iPhone `draw` only the status bar is affected,
   because the sheet covers the title. It is also in the iPhone and iPad preview
   clips. The dark-mode review captures show the same screen correct,
   with white on the same wall, so the fix is to give the viewer's bars the dark
   scheme (`.toolbarColorScheme(.dark, for: .navigationBar)`). This is a **product
   change**: fixing it means re-shooting the iOS listing and the six iOS preview
   clips.
2. **iPad: the iPadOS 26 window-resize grabber is in the bottom-right corner of
   every iPad image and every iPad preview frame.** It is a grey arc about 60 px
   across, drawn because the simulator ran in *Windowed Apps* mode. It is the
   §4 kind of thing, identical everywhere, so no comparison can see it. The
   rig fix is *Settings → Multitasking & Gestures → Full Screen Apps* on the
   capture simulator before `tools/ios-screenshots.sh`. It goes with any iPad
   re-shoot.
3. **The iOS and Windows listings don't show the two 2.0 headline features.**
   The 2.0 copy on every store leads with redaction and fixing the document's own
   text. The Mac listing shows redaction (`light-05`), and Play has both. The
   iOS listing's six slots have neither, although `text-edit` and `redact` are
   shot, gate-clean, and sitting in `review/`. `docs/app-store-listing.md` even
   carries a `text-edit` caption with no slot. The Microsoft set has no redaction
   pose. The App Store and Microsoft Store both take up to ten images, so this is
   a choice about slots, not a limit.

Smaller, and not worth a re-shoot on their own:

- **fr iPhone viewer title truncates** to "Contrat de loc…", because *Enregistrer*
  takes the room. That is iOS behaviour, and the page below names the document
  in full.
- **iPhone `draw`**: two thirds of the frame is an empty white canvas. It is the
  real sheet, but it is the thinnest image in the set.
- **Windows Redact icon is a warning triangle.** Glyph `E7BA` in Segoe Fluent is
  *Warning*, not the "block out" mark that the comment at `MainWindow.xaml` says it
  is. It reads as "careful", which is arguably right for redaction, but the
  comment and the glyph disagree.

### What was done about it (2026-09-19, 00:30–01:10 EDT)

The coordinator made the calls Dave had handed over:

- **iOS (defects 1 and 2, and the `text-edit` and `redact` slots):** the
  light-mode contrast is fixed as a product change, the iPad grabber is dropped in
  the re-shoot, and the iOS listing gains both slots. A separate iOS agent owns all
  of it, including the six iOS preview clips. Nothing in this section touched iOS.
- **Mac `light-05-redact`, fr-CA and fr-FR:** re-shot from a Mac build of `f8eadc9`
  (#242 merged). The only change is the hint bar, « …sur l'élément ciblé… » →
  « …sur l'élément actif… »: 1 258 px, all inside the bar (x 582–743, y 57–74),
  0.097 % of the frame. The gate, with tesseract, `--against` the signed-off set:
  0 to look at in all three languages. The other five poses came out byte-identical
  in every language, apart from `home`, which is kept as it was shot (next point).
  The old pair is in `~/gate-macos-pre242/`.
- **Trap: the Mac `home` pose takes the pointer's hover.** The re-shot `home`
  showed the first recent highlighted, with its "Documents › Clients" tooltip,
  because the mouse was resting over that spot on the capture Mac's display. The
  layout is otherwise identical. The signed-off `light-06-home` stands.
- **Microsoft: `06-redact.png` added** in en, fr-CA and fr-FR
  (`tools/screenshots-windows/Shot-Redact.ps1`). On the finished agreement, it
  marks the customer's name, saves a redacted copy, and shows the result: a black
  bar where the name was, and "1 area redacted: 15 characters" (fr: « 1 zone
  caviardée : 15 caractères »). Read back with qpdf, the name is in no decoded
  stream of any language's copy, and the unredacted control still carries it. The
  gate with tesseract, `--against` the five-image set: 0 to look at, the five old
  images unchanged, and the French `06` has no English words. Slot order and
  captions are in `docs/microsoft-store-listing.md` (`gen_listing_copy.py`).
  The caption is the App Store's approved heading, "Redact, and it really is gone" /
  « Caviardez, et c'est parti pour de bon ». The Microsoft description and feature
  list still don't mention redaction, so the image is ahead of the copy there.
- **The Redact glyph stays for 2.0.** Only the `MainWindow.xaml` comment was
  corrected (`fd12ffa`).

### iOS: defects 1–3 fixed and re-shot (2026-09-19, 00:40–01:40 EDT)

- **Defect 1, the title contrast, is #284**, fixed in PR #285: the viewer's bar
  takes the dark scheme on a visible background of the wall's own colour.
  `ViewerBarContrastUITests` measures the title and the clock off the screen, and
  was proved both ways on the Mac mini: without the fix it fails at **2.03 : 1**
  (this review measured 2.0 : 1); with it, it passes on iPhone in light and dark
  and on iPad. Dark mode was already right and doesn't change: the dark review
  shots differ from the old ones only in the iPad's bottom bar (next point).
- **Defect 2, the iPad grabber:** the rig now puts the capture iPad in *Full
  Screen Apps* through Settings (`CaptureSimulatorSetupUITests`, run by
  `ios-screenshots.sh` and `ios-demo-video.sh`) before shooting. It's gone from
  every iPad image and every iPad clip. Settings writes
  `SBChamoisWindowingEnabled = false`, but the rig drives Settings instead of
  writing a private key that has moved between releases.
- **Defect 3, the listing:** eight slots now, not six. `text-edit` and `redact`
  come straight after the signed agreement, because the 2.0 copy leads with them:
  viewer, text-edit, redact, text, search, sign, draw, home. The captions are in
  `docs/app-store-listing.md` in all three languages (`gen_listing_copy.py`). The
  redact caption is the Mac's, "Redact removes it. It does not just cover it." /
  « Caviardez : c'est retiré, pas seulement couvert. » `review/` now holds only
  the three dark shots per device.
- **The re-shoot** is the full iOS set from a build of `6ab967e` (read out of
  the app: `com.megapdf.ios` 2.0.0, `MEGAPDF_SERIES=71de3e7cee0e`,
  `MEGAPDF_PATCHES=32`), in en, fr-CA and fr-FR on iPhone 17 Pro Max and iPad Pro
  13-inch (M5): 48 listing and 18 review images. **The gate, with tesseract: 0 to
  look at in all six cells.** `--against` the signed-off set (with its
  `text-edit` and `redact` light shots taken from `review/`) flags every image,
  as it should, and the changed pixels were located image by image:
  - **iPhone:** all of them are above the navigation bar's bottom edge (the
    find bar's too in `search`). Below it, 0.000–0.012 %, which is the
    signature thumbnail's anti-aliasing.
  - **iPad:** the same top band, plus the bottom 130–160 px (the floating
    toolbar sits differently in Full Screen Apps) and a 47 × 47 px square in
    `home` where the grabber was. `text-edit` differs by 0.02 % inside its form
    sheet, which blurs the page behind it.
  - **Dark review shots:** iPhone 0.000 %; iPad only the bottom bar band.
- **Read by eye:** all 48 listing images one by one, and the 18 dark review shots on a
  contact sheet. The title and clock are white on the dark bar in every light
  viewer pose, there's no grabber, and each language is its own throughout,
  with Hélène Bélanger and Céline Lefèvre accented. The fr iPhone title still
  truncates to « Contrat de loc… » (iOS behaviour, noted above). Two poses are
  weaker as selling images, and neither is a defect:
  - **iPhone `text-edit`** is the edit sheet over the keyboard, so the page
    isn't in shot; the iPad one shows the sheet over the page. The caption
    carries the story.
  - **`redact`** shows the mark as a thin outline around one line, like
    Android's (§7).
- **The six iOS preview clips are re-cut**; see `preview-videos.md` §7.

### The five images no comparison can clear (§2)

| image | verdict |
|---|---|
| Mac `light-01-viewer.png` | **Clear.** A strong lead: ticked boxes, the signature on its line, the status line saying what to do next. Nothing stray at the edges. |
| iOS `iphone-6_9-viewer.png` | **Was defect 1; clear after the re-shoot.** The content is right, and "Rental Agreement.pdf" and the clock are now white on the dark bar (#284). The page sits vertically centred, so the top fifth is empty backdrop; that is the #48 layout, and it is fine. |
| iOS `ipad-13-viewer.png` | **Was defects 1 and 2; clear after the re-shoot:** the title is white on the dark bar, and there's no grabber. |
| Play `android-viewer.png` × 2 | **Clear**, phone and tablet: dark title on the white app bar, clean status bar, no taskbar. See §7. |

### Where each final set lives

| set | host | path |
|---|---|---|
| Mac | Mac mini | `~/gate-macos/{en,fr-CA,fr}/light-0?-*.png` |
| iOS | Mac mini | `~/gate-ios-284/{en,fr-CA,fr}/{listing,review}/`: eight listing images per device, re-shot 2026-09-19 for #284 (upload `listing/` only; the old set is `~/gate-ios/`) |
| Microsoft | GPD-DAVE | `artifacts/store/screenshots/{en,fr-CA,fr-FR}/0[1-6]-*.png`: six per language, `06-redact` added 2026-09-19 (not `work/`) |
| Preview clips | Mac mini | iOS: `~/captures/video-2.0-284/ios/<lang>/*-preview.mp4` (re-cut 2026-09-19); Mac: `~/captures/video-2.0/macos/`. Stills and sheets on kdocker2: `~/megapdf-284/vout/` (iOS) and `~/megapdf-video-work/mac-out/` (Mac) |
| Play phone + tablet | kdocker2 | `~/megapdf-rc-android-work/{phone,tablet}/{en,fr-CA,fr-FR}/android-*.png` (identical to `~/megapdf-146-work/gate-2.0b/`) |

## 7. The Play sets, re-shot on the RC tree (2026-09-19)

Both Play sets were shot again from main `bcddfb7`: the PDFium 32-patch series,
#241/#246/#267/#270 and the MegaPDF rename. That's everything that landed on
Android after `4200f1c`. The debug APK read `versionCode 9`, `versionName 2.0.0`
out of `aapt2 dump badging` before shooting. Same AVDs (`make-store-avds.sh`), same
script, API 33, launcher disabled.

- **All 48 images are pixel-identical to the signed-off set** (`compare -metric
  AE` = 0 for every image, phone and tablet, en / fr-CA / fr-FR). The only
  Android-side changes since `4200f1c` are two unused strings removed (#173),
  the third-party notices and the engine, and none of them reaches a posed screen.
- The gate, with tesseract, `--against` the signed-off set: **0 images to look
  at** in all six cells (phone 49/73/73 checks passed, tablet the same).
- **The `redact` pose was already current.** #146 §3 listed it as needing a
  re-shoot for #173's Save fix. The fix landed in round 4 and the signed-off set
  was shot in round 5, after it: Save is brand blue in all six `redact` images.

**Read by eye, all 48, including `android-viewer.png` on both devices:** no
defects. The status bar is clean (9:41, full battery, no badge), there's no taskbar
or navigation buttons, the right person appears in each language with every
accent, and nothing is clipped. What a listing reader will notice, though none of
it is a defect:

1. **`redact` is the weakest image.** The mark is a thin outline around one line,
   and Android shows no hint strip (#173, decided). Only the filled Redact button
   says the tool is on. The copy promises "gone for good", and the image shows
   "marked". Put it last, or leave it out of the listing.
2. **Tablet `home` is mostly empty.** It's the phone layout on an 800 dp canvas:
   the recents sit in the top third. Lead the tablet listing with `viewer` or
   `search`.
3. The demo document's French (`fin de semaine`, `ramassage`) is Quebec usage
   and also appears in the fr-FR set. It's document content, not the app's
   strings, so it isn't in any string review.

The set lives at `kdocker2:~/megapdf-rc-android-work/{phone,tablet}/<lang>/`. It's
identical to `~/megapdf-146-work/gate-2.0b/`, and either one is the upload set.

## 8. Running it again

The sheets are self-contained HTML — thumbnails inlined, full images linked
beside them — so one can be mailed or opened on a phone with no network.

| set | where its sheet is | the command |
|---|---|---|
| Mac | `kdocker2:~/megapdf-157-work/cg-gate/final/mac/` | `gate.py --store mac ~/gate-macos -o out` |
| iOS listing | `.../final/ios-listing/` | `gate.py --store ios ~/gate-ios --only /listing -o out` |
| iOS review | `.../final/ios-review/` | `gate.py --store ios ~/gate-ios --only /review -o out` |
| Play phone | `.../final/play-phone/` | `gate.py --store play <set>/phone -o out` |
| Play tablet | `.../final/play-tablet/` | `gate.py --store play <set>/tablet -o out` |
| Linux | `kdocker2:~/megapdf-157-work/cg-out2/linux/` | `gate.py --store linux <shots> -o out` |
| Microsoft | GPD-DAVE `artifacts/store/gate-microsoft/` | `gate.py --store microsoft artifacts/store/screenshots -o out` (tesseract in a container: the WSL box has no sudo) |
| Preview videos | kdocker2 `~/megapdf-video-work/{preview-out,mac-out}/gate/<lang>/` | `preview-gate.py -o out <clip>…` then `gate.py --store video out/frames/<lang> -o out/gate/<lang>`, **one language at a time** — see `preview-videos.md` |

Add `--against <a set you already signed off>` for a re-shoot: it reports
everything that changed, and everything that changed had better be something
you meant. The Android session's `--against` run over the tablet re-shoot lit
up all 24 images at 18–25 % of pixels, which was the taskbar coming out.

Four checks need `tesseract`; the numbers in §1 were measured with it
installed. Without it they stand down and the sheet's header says so.

---

*Measured against `tools/capture-gate` at the commit this file arrived in,
over sets built from main `476ce157` (Mac, iOS), `4200f1c`'s tree (Play),
`c8b7a7e` (Linux) and the MegaPDF rename's tree (Windows, 2.0.0.0).*
