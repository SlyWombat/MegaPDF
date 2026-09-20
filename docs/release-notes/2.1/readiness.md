# MegaPDF 2.1 — release readiness, audited against the 2.1 branches (#146)

Audited on 2026-09-20 against `c8f645d` (**desktops**, PR #338) and `0b4fd35`
(**phones**, PR #337), which merge upward from `main`'s `3c6f8f3`. Two Avalonia
defects were found by the capture pass during the audit and are fixed on the
desktops branch (`57aa5e5`, `0cfd93b`, §4). Every claim below was read out of the
repository, the two pull requests' check runs, or a capture run's own artifacts —
not taken from a comment. **Nothing has been published**: no tag, no release, no
store submission and no packaging artifact has been produced, and none may be
until Dave says so.

**Verdict: the engineering and the copy for 2.1 are done; the submission package
is not complete, and two things outside this machine block it.** Both store
capture sets that must come from a real desktop — the Microsoft Store's and the
Mac App Store's — need hardware this machine does not have; the milestone also
carries an open item that gates 2.1 by its own title (#330) and a French-content
defect in the fr-FR sets that 2.0 shipped knowingly (#310). Nothing else is
unknown, and nothing else is blocked.

---

## 1. What 2.1 is

2.1 is the work done since 2.0, on two branches:

| | Issue | What it is | Branch |
|---|---|---|---|
| Core | #332, #333, #334 | one page check at a time on three platforms; one signature-library policy; one copy of the document held while saving | merged to `main` as `3c6f8f3` |
| Phones | #337 | #328 (Redact is a named row in the ⋮ menu), #329 (the mark lifecycle), #336 (a pinch zooms) | `wip/328-329-336-redact-menu-marks-and-pinch` |
| Desktops | #338 | #329 on Windows and the Mac/Linux app (the mark lifecycle) | `wip/329-desktops-marks-and-chrome` |

The headline is **a redaction mark you can take back** (#329 on the phones, #338
on the desktops) — select, move, reshape, remove, Clear all, each as one undo
step, and a mark that no longer outlives the document it was made on. That is
the one item in all four stores' copy; the phones' #328 and #336 are not, because
the desktops do not have them.

### The version numbers, as they stand in the tree

| | File | Value |
|---|---|---|
| Windows | `src/MegaPDF.App/MegaPDF.App.csproj` | `2.1.0` |
| Windows | `src/MegaPDF.App/Package.appxmanifest` | `2.1.0.0` |
| iOS | `ios/project.yml` | `MARKETING_VERSION 2.1.0`, `CURRENT_PROJECT_VERSION 1` |
| Android | `android/app/build.gradle.kts` | `versionName "2.1.0"`, `versionCode 11` |
| Mac and Linux | `src/MegaPDF.Avalonia/MegaPDF.Avalonia.csproj` | `2.1.0` |

Read out of the source, not out of a built artifact: **no 2.1 build has been
packaged anywhere yet**, so the About window, the APK's badging and the MSIX
identity have not been checked against these numbers the way 2.0's were. That
check belongs to the release build, and it is on the list in §3.

### The copy

`docs/release-notes/2.1/` holds all five fields in three languages —
`microsoft-store.md`, `app-store.md`, `mac-app-store.md`, `google-play.md` and
the long-form `release-notes-2.1.md` — with `README.md` recording each block's
count, the issue behind every line, and the French for a reviewer to read. The
What's New text is pasted into the two listing documents the submission tools
read (`docs/app-store-listing.md` ×3 languages, `docs/microsoft-store-listing.md`),
so `tools/msstore_submit.py`, `tools/asc_publish.py` and `tools/play_listing.py`
would carry 2.1's text and not 2.0's if either were run today.

**The French of 2.1's new strings is not signed off.** 2.0's was reviewed by a
francophone and signed off; 2.1 adds four strings on Windows, six on Mac and
Linux, three each on Android and iOS, all new. `README.md` lists them with the
judgement in each.

## 2. Per platform

| | Engineering | Tests | Store captures | Outstanding |
|---|---|---|---|---|
| **Windows** | merged to the branch, `Build on Windows` green on the PR | Core tests, including the new `RedactionMarkTests` | **conformance set only** — six states shot locally on 2026-09-20 from a build of this branch (`artifacts/windows-screenshots/`); the **Store set is still 2.0's** | the 2.1.0.0 Store set needs an interactive desktop (GPD-DAVE); the installed package here is 2.0.0.0, and `Shoot-Set.ps1` refuses unless the version matches. WACK at 2.1.0.0 has not run |
| **macOS and Linux** | merged to the branch; `What the app looks like`, `App Sandbox viability`, `MegaPDF.app` (arm64, x64), `Snap` green on the PR | Core tests on macOS | **none for 2.1 — and 2.1's `redact` slot could not have been shot at all until the pose was fixed on 2026-09-20** (below). All six poses now fire, shot from the Avalonia build on this machine in en and fr-CA | the Mac Store set needs the in-house Mac (`tools/macos-store-captures.sh`): the workflow named "macOS screenshots" is design-review only and says so, and the set must be re-shot anyway — two Avalonia fixes landed on 2026-09-20 (§4) |
| **iOS** | fixed on the phones branch — `ViewerView.swift`'s mark overlay was one expression the Swift type-checker refused | `UndoTests` updated for the history's new hand-back; iOS CI green after the fix | **CI** (`iOS Screenshots`, `35539723837`, dispatched from `6762d77`), en / fr-CA / fr — all three green | #172, the iPad's own layout, is **not** in 2.1 and no copy names it |
| **Android** | merged to the phones branch; `build-and-test` and `instrumented-test` green on the PR | Core tests cover the mark lifecycle | **CI** (`Android Screenshots`, `35540445344`, dispatched from `febf0d7`), en / fr-CA / fr-FR — all three green, and fr-CA carries the mark (§4) | the app module still has no `androidTest` harness; Play Console's aspect-ratio and tablet questions from 2.0 are unanswered (**#345**) |
| **Linux** | merged to the branch | Core tests | not a store; `Snap` and `Linux packages` green | #314 (publishing to the Snap Store) waits on Dave's Snap account |

## 3. What is left

### Blocking — no store gets a build until these are done

1. **The two desktop capture sets.**
   - **Microsoft Store.** `Shoot-Set.ps1` photographs a real screen with
     `CopyFromScreen` and drives it with the mouse, so neither CI (the app
     writes a zero-byte PNG and the step still goes green) nor a locked or
     headless session (pure black, which the script's own brightness check
     reports) can shoot it. It also refuses to start unless the installed
     package's version matches its `-Version`. **This machine's `CopyFromScreen`
     returns mean brightness 0 and the installed package is 2.0.0.0**, so the
     2.1 set needs GPD-DAVE with a 2.1.0.0 package installed.
     `docs/microsoft-store-listing.md` records why the 2.0 set is *not* being
     kept in place of it: keeping it is defensible — the six states are ones
     2.1 does not change — and it is written down there as a finding, with what
     a re-shoot needs. Tracked as **#339**.
   - **Mac App Store.** `tools/macos-store-captures.sh <lang>` on the in-house
     Mac: six images per language at 1440×900. There is no workflow for it and
     cannot be: the Mac app needs a real display. **This set has to be shot
     after the two Avalonia fixes of 2026-09-20 (§4)** — 2.1's slot 5 pose wrote
     no image at all before them, and the ✕ it draws moved. Tracked as **#340**.
2. **The version check in the built artifacts.** Nothing has been packaged, so
   the numbers in §1 have not been confirmed in a *built* Windows MSIX, a Mac
   `.pkg`, an APK's badging or an iOS archive. 2.0's audit found this worth
   doing because the tree and the artifact can disagree. Tracked as **#342**.
3. **WACK at 2.1.0.0** — one elevated click on GPD-DAVE, as at 2.0.0.0.
   Tracked as **#341**.
4. **The French sign-off** (§1). Tracked as **#343**.
5. **#330, the test matrix, which its own title says gates 2.1.** Dave asked for
   it on 2026-09-19, and 2.1 as scoped here does not close it. It is open, and
   this document is not the place to decide whether 2.1 ships without it.

### Dave's, at submission

| | |
|---|---|
| **The go-ahead.** | Tagging is a release action. `ios-release`, `android-release`, `macos-appstore` and the Windows package all fire from a version tag or an explicit run, so nothing can be built until he says so. |
| **#310, the fr-FR sets** | The fr-FR store screenshots show Quebec content — *Tarif fin de semaine prolongée*, and on some platforms *pi*, *lb*, *$* — while the release notes avoid *fin de semaine*. For 2.0 Dave decided both French sets go up as shot (2026-09-18). 2.1 is the "next re-shoot" the issue was filed for, and the demo documents still carry the Quebec line, so the same decision is being asked again. Fixing it means giving fr-FR its own staging text in the generators (`tools/screenshots-windows/gen_store_docs.py`, the phones' demo PDFs, the Mac's), which changes every fr-FR image. |
| **#309, the Windows Store name** | The Store still shows *Mega PDF* (spaced) against the MegaPDF name decision, because the Partner Center reservation is the authority and the manifest is checked against it. Dave's to change at submission. |
| **#305, the Windows Store logo** | Only `en-us` has a Store logo. |
| **#314, the Snap Store** | Waits on his Snap account; not part of 2.1's four stores. |

### Deliberately not in 2.1

Named here so that nothing has to be re-derived at submission time, and because
every one of them is in the **2.1 milestone** — which is why the milestone cannot
be closed by this release:

- **#172 iPad's own toolbar** — moved to 2.1 on 2026-09-18 and still not done.
  2.1's copy does not mention it.
- **#330 the test matrix** — see above.
- **#310 fr-FR screenshots** — see above.
- **#142** text extraction, **#168** reading mode, **#174** page tools, **#154**
  re-deflate on save, **#187** toolbar customisation. **#175** OCR stays in the
  backlog.

## 4. Risks worth knowing before "go"

**The fr-CA Android `redact` pose missed twice, and the pose now says so itself.**
The fr-CA `redact` image of the 2026-09-20 Android capture (run `35539439018`) shows
the demo agreement with **no mark on the page** — and the pose exists to show a mark
selected, which is the one image 2.1 changes. English and fr-FR in the same run are
correct.

It is **not** a slow runner, which was the first guess. The re-run (`35539966791`)
reproduced the first exactly — the same three images, mark pixel for mark pixel —
and it puts the difference in the two places a mark would show: the band across the
line's text, and the Save button, which is grey in fr-CA and brand blue in fr-FR,
because Save is enabled by `isDirty || hasMarks`. The app in the fr-CA image agrees
with the image: no mark.

The pose's inputs are provably the same in both languages. Its three strings
(`screenshot_demo_asset`, `screenshot_redacted_word`, `screenshot_search_term`) are
byte-identical in `values-fr-rCA` and `values-fr` — the two files carry 176 strings
each, all identical, none missing — and the pages the two images render are identical
outside that band. The pose finds its line by phrase (`screenshot_redacted_word` =
"client nommé"), which the French agreement breaks across a line ("… et le client /
nommé ci-dessous …"), so both languages fall through to the same fallback — the
longest line on page 1 — and ask for a mark at the same rectangle.

What is left is the pose's **silence**. `markForRedaction` returns without a word
while an edit is in flight (`launchEdit`'s `editingBlocked` guard), and it returns
without a word when the core makes no mark at all (`made.isEmpty()`). A pose that
gets neither a mark nor an error is exactly a page with no mark on it and a green
run — which is what this was.

**The branch closes that hole.** The pose waits for the edit gate to open before
asking, waits for the mark to land rather than guessing at a delay, asks a second
time, and logs `::error::` under the tag `megapdf-screenshot` when it still gets
none — the convention the Mac and Linux scripts already read from stdout; any state
that throws while posing logs the same way. `android/scripts/capture-screenshots.sh`
clears logcat before each launch and reads it after the capture, so a line it finds
can only belong to that state, and the step goes red naming it. The images are still
uploaded (`if: always()`), so a red run can be read.

**The re-run is the test: `35540445344`, dispatched from `febf0d7`.** All three legs
are green, and the fr-CA image was downloaded and looked at: it carries the band
across the line, the selection box, the four corner handles and the ✕, exactly as
fr-FR does. A green run no longer means "the pose said nothing"; it means the mark
was placed or the run went red. What a green run still cannot say is whether the
*image* is right — every state is read by eye as well (last risk in this section).

**The Avalonia app writes no 2.1 `redact` image — in any language.** The first
local run of `--screenshot-state redact` on the Avalonia build printed `nothing
was marked` and wrote no image, and every 2.1 run does the same, so **the Mac and
Linux slot 5 has no 2.1 image and could not have had one** — the capture script
fails the slot on `::error::`, so this would have gone red on the in-house Mac
rather than shipping a wrong picture.

The state itself has written images; it is 2.1 that stopped. 2.0's image is on
disk: `docs/qa/linux-fr/redact.png`, which the 2.0 French review pack captions
"the Redact tool armed, its banner, and a marked line", and whose band measures
(199,206,213) — `#3D16324F` over white, the mark, to the digit. What 2.1 changed
is where the mark is *counted*: in 2.0 the placement ran inline and
`RefreshRedactionMarks()` followed it inside the same call, so the pose's next
statement saw the mark. #329 moved the count to the core, and it is refreshed
after `PlaceRedactionMarkAsync` has finished.

The cause of the silence is #145, two days older than the pose. With a window open
the view model runs work in the background (`RunsInBackground`), so
`AddRedactionMark` starts the placement and returns; the pose's next statement
read `HasRedactionMarks` before the mark existed. The mark was placed a moment
later, on the right page and the right rectangle: the feature was never broken,
the pose was. `AddRedactionMarkNow` takes the synchronous route the view model
already uses for the self-test and the capture runs, and the pose now writes the
image (verified in fr-CA: `artifacts/pose-check/` — untracked scratch, `artifacts/`
is ignored, so that path is on the machine that shot it and not in this repo).

**And the ✕ that takes a mark off was drawn at the page's corner, not the mark's.**
The chip was right-aligned in the chrome panel — but that panel is the whole page
(the chrome Border has no size of its own, so it stretches to the overlay it is
added to, and only the body and the handles are laid out from the page's origin by
margin), so the chip's own corner was the *page's*. Only its bottom ~10 px was on
screen: everything above the page's top edge was cut off. In the capture it read as
a stray ✕ floating at the top of the sheet, ~200 px right of the mark and ~120 px
above it, under the toolbar — which
is what the capture pass is for, and why the images are looked at rather than
trusted. The chip is now placed from the selection rectangle, like the body and the
handles, and `ApplyChromeRect` carries it along a drag. That is what Windows does in
the source; the before/after pair differs in exactly two places per capture, the
chip leaving the page's top edge and arriving at the selection's top-right corner.

**Both are Avalonia-side and both are fixed on the branch** (`57aa5e5`, `0cfd93b`).
They were found by running the Avalonia build on this machine (Windows, the
Avalonia renderer): the *code path* the Mac app runs is the same, the renderer
platform is not, so the Mac and Linux sets are still to be re-shot — and now must
be, since slot 5's chrome changed after the last run that could have shot it.

**All six Avalonia slots were then posed in one pass, in en and in fr-CA**
(`artifacts/pose-check/audit/`, `audit-en/` — again untracked scratch, kept with
`audit.ps1` and each slot's console log): viewer, text, search, sign, redact and
home all wrote a 1440x900 image with no `::error::`. The two states that mattered —
the marked-and-selected mark, and the find bar with its count — were read as images,
not as file sizes.

**The gate cannot see anything that is the same in every image of a set.** Every
check in `tools/capture-gate` is a comparison — against a slot size, against the
same pose in another language, against a set already signed off — so a constant
has no sibling to disagree with it. Both capture runs are read image by image
*as well*, by eye, and that is the only thing standing between a wrong constant
and a store listing.

**2.1's Windows UI work has been compiled and run exactly once, by me, in the
conformance pass** — the six non-empty PNGs in `artifacts/windows-screenshots/`
are the first real evidence that #338's Windows code builds and draws. CI cannot
run the app (no compositor), so every claim about how Windows *looks* in 2.1
rests on that one run, and the redaction-marked state is not one of the six.

**Windows' own selection chrome has not been photographed.** No
`--screenshot-state` shoots a selected item: `textbox` selects one, but the state
renders the window as it comes up, and on 2026-09-20 that was the top of the page
— the box is on the signature line, below the render's bottom edge. The Store
set's `06-redact.png` shows the result of a save, not a selection. So the claim
that Windows places its ✕ correctly, which is what the Avalonia chip was moved to
match, comes from **Windows' source** — `MainWindow.xaml.cs` builds a
selection-sized `Grid` with the chip right-aligned inside it — and not from an
image of it. It is the one claim in this section nothing has looked at. Tracked
as **#344**.

**Android's mark lifecycle has no instrumentation test.** The app module has no
`androidTest` harness, so #329 on Android is covered by the core tests and by
the captures, not by a test that drives the app.

**The consoles are the authority at submission time and this machine reaches
none of them.** Everything in §2 about what is live, in review or public comes
from the repository and the run history.

## 5. What I could not verify from here

- **Anything needing a display or a device**: the Windows Store set, the Mac
  Store set, WACK, the Play Console's two questions, and the 200 % / Retina Mac
  captures (no HiDPI display on the capture Mac, and `--scale 2` misplaces every
  page overlay — the capture refuses rather than write a wrong image).
- **Anything needing a console**: whether prior submissions were accepted,
  whether 2.0 is live on each store, and every listing field as the store
  actually holds it.
- **That any 2.1 artifact will match the tree's version numbers** — nothing has
  been packaged.
- **How the Avalonia chrome looks on macOS and Linux.** The two fixes of
  2026-09-20 were verified on this machine, which runs the Mac/Linux *code*
  against Windows' renderer and Windows' fonts. Slot 5 has now been shot, but
  never as a Mac image or a Linux image: the in-house Mac is the first place that
  will happen, and the renderer is the variable this machine cannot supply.
- **Whether the captures are good enough to list.** They are measured; whether a
  set sells the app is a judgement and it is Dave's.
