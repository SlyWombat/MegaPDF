# 2.0 capture gate — the consolidated report (#146 §3)

Every store capture set that exists for 2.0.0, measured by `tools/capture-gate`
and set beside the human-eye review each of those sets already had. Built to be
read at speed: the verdict first, then the short list of images worth opening,
then where the tool and the eye disagreed.

**Nothing here was uploaded anywhere.** Every set is sitting on the machine that
shot it.

---

## 1. The verdict

| store | slot | images | checks passed | not run | **to look at** |
|---|---|---:|---:|---:|---:|
| **Mac App Store** | 1440 × 900 | 18 | 150 | 24 | **0** |
| **App Store (iOS)** — listing | 1320 × 2868, 2064 × 2752 | 36 | 258 | 114 | **0** |
| **App Store (iOS)** — review | the same, plus dark | 30 | 210 | 100 | **0** |
| **Google Play** — phone | 1080 × 2400 | 24 | 171 | 77 | **0** |
| **Google Play** — tablet | 1600 × 2560 | 24 | 171 | 77 | **0** |
| **Microsoft Store** | — | — | — | — | **not run** — see §5 |
| Linux (not a store yet) | 480–1280 wide | 234 | 1 280 | 972 | 10 → **#237** |

All five store sets are **gate-clean**, and every one of them was also read
image by image by a person first. The two reads agree.

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
| **`Mega PDF` against `MegaPDF`** | The gate reads text but has no idea which spelling is meant. A product decision; the list of where each form appears is in the 2026-09-18 comment on #146. |

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

**Not run — the set is not on this machine.** The 2.0.0.0 package was built and
installed on the Windows machine and WACK still wants one click; the captures
are shot there, on a real desktop, and have not been copied anywhere this can
reach.

The `microsoft` profile is written from `tools/screenshots-windows/README.md`
and **has never met a real set**. Run it once on the machine that holds them:

```bash
python3 tools/capture-gate/gate.py --store microsoft artifacts/store/screenshots -o gate-out
```

Expect the slot sizes and the pose names to want a line each in `stores.py` —
that is the only file a store lives in — and expect `toolbar` to need the
frame's scale, because the bar drops its labels below about 1 430 effective px
in English and 1 610 in French.

## 6. Running it again

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

Add `--against <a set you already signed off>` for a re-shoot: it reports
everything that changed, and everything that changed had better be something
you meant. The Android session's `--against` run over the tablet re-shoot lit
up all 24 images at 18–25 % of pixels, which was the taskbar coming out.

Four checks need `tesseract`; the numbers in §1 were measured with it
installed. Without it they stand down and the sheet's header says so.

---

*Measured against `tools/capture-gate` at the commit this file arrived in,
over sets built from main `476ce157` (Mac, iOS), `4200f1c`'s tree (Play) and
`c8b7a7e` (Linux). The Windows set is outstanding.*
