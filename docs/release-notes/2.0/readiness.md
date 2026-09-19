# MegaPDF 2.0 — release readiness, audited against main (#146)

Audited at `73670fb`'s tree on 2026-09-18, re-checking the 03:00 EDT audit of `a639467`,
updated at 07:35 EDT for **Dave's decisions of 07:15 EDT** (§3), and again at 08:35 EDT
for what landed with #243, #245 and Dave's 08:00 EDT call on #173. Every claim below was
checked against the repository rather than taken from a comment: each SHA resolved and
tested for ancestry of `main`, each `#N` queried for its real state, each artefact stat'd
in the tree, and CI read per workflow.

**Verdict: 2.0 is not ready to submit, and nothing is blocked by anything unknown — and
the open product calls are no longer open.** The engineering is done and the QA passes are
done on all five platforms. The version bump has merged, the listing copy has been
re-read, and **all six store capture sets are now shot from 2.0.0 builds and
gate-clean**, the Microsoft one included. What is left is one Android pose, the preview
videos, two fixes already in progress, the store builds, and five things only Dave can
do.

---

## 1. Where #146 and main disagree

**Nowhere, as of this audit.** The issue body was re-synced against this tree on
2026-09-18 and every box in it now matches what the tree, the merged PRs and the issues
themselves say. All the commits it names exist and are ancestors of `main`, and every
artefact it claims — five screen inventories, the Android QA results, the release-note
files, the French review pack, the capture-gate report, the large-fixture generator, the
PDFium README, the leakcheck sources — is in the tree.

For the record, this is what the previous audit's five discrepancies turned into:

| | What the audit said | What main says now |
|---|---|---|
| **#144, #145, #139, #143** | ticked in the issue, but all four issues open | **#144, #139 and #143 are closed** (2026-09-18), each with what closed it recorded. **#145 stays open on purpose**: P1 — every data-loss and correctness item — is merged; P2 is untouched and P3 is one of four, and the issue now says exactly which. None of the remainder is a correctness or data-loss risk. |
| **Version bump** | prepared on a branch, deliberately unmerged, 44 commits behind | **merged: `edd68ec`.** Windows 2.0.0 / 2.0.0.0, Mac and iOS 2.0.0, Android 2.0.0 / versionCode 9. Seven workflows green on that full SHA, and the numbers were confirmed in the *built* artefacts — the Mac About window, the APK's badging, the MSIX identity — rather than in the source. |
| **#220** | open, 2.0 milestone: `core/` test target unbuildable on Windows | **fixed and closed** — `86afbd5`. `cmake -E copy_if_different` onto a directory that does not exist writes a *file* with that name; `make_directory` first. Core tests green on all three operating systems. |
| **#165** | open, 2.0 milestone; a comment claimed iOS had no location | **closed.** All four platforms draw it: Windows `MainWindow.xaml:353`, Mac `MainWindow.axaml:562`, iOS `HomeView.swift:45`, Android `HomeScreen.kt:178`. |
| **"CI green on all workflows"** | true only for the four that run on `main`; `macOS app` never does | Still true that **`macOS app` has no `push` trigger** — it is `pull_request` + `workflow_dispatch`. But because every merge here is a fast-forward, the PR run's head SHA *is* main's SHA, so main's commits do carry a Mac result. At `c64a440`: `MegaPDF.app (osx-arm64)`, `(osx-x64)`, `App Sandbox viability (osx-arm64)`, `Core tests on macOS`, `Linux app`, `Linux packages`, `Build on Windows` — all green. The gap is narrower than it reads: only a commit that reached main *without* a PR would go uncovered. |

## 2. Per platform

| | Engineering | §1b QA pass | Store captures | Tests | Outstanding |
|---|---|---|---|---|---|
| **Windows** | merged, **and renamed to MegaPDF** (`36ebfd3`) | done — inventory, captures (en/fr-CA/fr-FR × light/dark × 1280/1000/800/480 + 200 %), all flows on the installed package, plus an RC sweep on 2026-09-18 | **done (`149e17b`): 15 images from the installed 2.0.0.0 package built from the rename, title bars reading `— MegaPDF`, gate-clean in all three languages** | CI green on main; Core tests 276/276 and `ctest` 1/1 on Windows | **WACK at 2.0.0.0 — one elevated click**, the package rebuilt from `8a77b8c` |
| **macOS** | merged | done (PR #159) — inventory, 117 captures, every flow incl. protection and `kill -9` recovery; re-walked in the 2026-09-18 RC sweep | **18 images at 1440×900, from a 2.0.0 build — gate-clean**, and read image by image first | `macOS app` green on the PR run whose head is main's head | **no 200 % / Retina set** — no HiDPI display, and `--scale 2` misplaces every page overlay (measured, and the capture now refuses). Covered by Dave's 2026-09-16 Retina call |
| **iOS** | merged | done (PR #159) — inventory, 159 captures incl. AX5; RC sweep 2026-09-18 | **36 listing images (1320×2868, 2064×2752) plus 30 review shots, from a 2.0.0 build — gate-clean** | iOS CI green on main | **#172 moved to 2.1** (Dave, 2026-09-18) — 2.0 ships the iPhone layout on the iPad, which ticks §1b's "findings filed" box. Minor: the Redact hint string in `Localizable.xcstrings` is not rendered anywhere in Swift, so iOS carries the same dead string Android has just shed |
| **Android** | merged | done (PR #182) — inventory, 1,347 captures over 27 cells, **and the same set again after the fixes**, 18/18 flows, all 13 large fixtures; RC sweep 2026-09-18 added a redaction flow group the rig had never had | **phone 24 at 1080×2400 and tablet 24 at 1600×2560, from the 2.0.0 APK — both gate-clean.** One pose (`redact`) needs re-shooting: #173's Save fix makes Save live and blue where it was grey | Android CI green on main | the Play Console's two questions (9:20 aspect ratio, whether there is a tablet slot); the app module still has no `androidTest` harness |
| **Linux** | merged | done — inventory, 234 captures, 18 integration checks, a battery matching Windows and Mac | not a store yet; the QA set is where #237 was found | CI green on main (`Linux app`, `Linux packages`) | **#158 open by design** — ships after the others. Printing in the sandbox landed 2026-09-18; the portal file dialog, recents across a restart and one real print still need a GNOME session |

## 3. What is left

### Blocking — no store gets a build until these are done

1. **§2 store builds.** The version bump has landed, so the tags that gate them are now
   possible — but tagging is a release action, so **all four builds wait on Dave's single
   go-ahead** rather than on any separate permission.
   - **Windows:** `MegaPDF.App_2.0.0.0_x64.msix` is built from `edd68ec` with the #166
     clean-output recipe and installed; About reads 2.0.0 and the resource map is right.
     **WACK has not run at 2.0.0.0.** The rename has landed, so the package has been
     rebuilt from `8a77b8c` and WACK is raised and waiting against the build that ships.
     `appcert` needs elevation and the UAC prompt was cancelled twice at 23:41 EDT on the
     17th with nobody at the console. One approval, about half an hour. The 2.0 candidate at 1.7.0.0 already
     passed overall, with only the two known optional FAILs — both Windows App SDK noise.
   - **Mac:** `macOS App Store package` has only ever run on pull requests, which validate
     the signing chain and deliberately do not deliver.
   - **iOS and Android:** `ios-release` and `android-release` fire only on `ios-v*` /
     `android-v*`. Last runs were 2026-09-11 and 2026-08-29, both before 2.0's work.
2. **§3, the rest of the capture gate.**
   - ~~The Microsoft Store set.~~ **Done** (`149e17b`): 15 images shot 07:14–07:22 EDT
     from the installed 2.0.0.0 package built from the rename, gate-clean in all three
     languages. The `microsoft` profile had never met a real set and flagged all 15 — and
     every flag was the profile rather than an image: the slot size, a 2 px border over
     the wallpaper, the title-bar icon's blue, Mica hiding the toolbar's edge, and the
     title bar being read as the zoom row. Each fix was a measurement and each was
     re-proved against a planted defect, so a clipped word, an accent selection box and a
     spelling squiggle are all still caught. Written up in `capture-gate-report.md` §5.
     Two re-shoots on the way, neither the app's fault: a desktop-sharing banner carrying
     an email address landed over fr-FR, and Spotlight rotated the wallpaper so Mica took
     its colour. Windows alone had still been posing "Dana" — the English demo person is
     Jane Whitfield there now too.
   - ~~**The preview videos** (iPhone, iPad, Mac) — not started.~~ **Cut 2026-09-18**
     (main `862e029`): nine clips, 270 stills, read one by one and measured by
     `gate.py --store video`. The recording scripts had never met a 2.0 build and
     six defects came out of the frames — the iOS clips opened on the iOS home
     screen, the Mac clip ran at four times speed, and the French Mac clips typed
     the demo person without her accents. All fixed and re-shot;
     `preview-videos.md` is the report.
   - **One Android pose**, `redact`, for the reason in the table above.
   - **The human read.** The five shot sets were each read image by image *and* measured by
     `tools/capture-gate`, and the two reads agree — 0 images to look at in all five. What
     no comparison can clear is anything identical in every image of a set: one viewer
     image per device, five in all, named in §2 of the report. The launcher taskbar that
     sat in 24 Play tablet captures is the worked example — the gate clears that set
     today, knowing it is there.
3. **Two fixes Dave called for on 2026-09-18**, both in progress on kdocker2 and neither
   discovered by a test run:
   - **#241** (2.0 milestone, `megapdf-241`) — removing protection must write an
     unencrypted file. Today it leaves an encryption dictionary with an empty user
     password: the file opens with no prompt, shows no restricted banner and is fully
     editable, so nothing is wrong from a user's side, but the dictionary should not be
     there. Found on the Mac in the §1b pass; Windows does not reproduce it.
   - **#237** (`megapdf-237`) — Linux's 480×360 minimum window, fixed at the declared
     minimum rather than by raising it. Not a 2.0 blocker; Linux ships after the others.
4. ~~The rename to `MegaPDF` on Windows.~~ **Done** (`36ebfd3`), and the crossing point
   this document flagged was handled rather than missed: the Microsoft set was shot *from*
   the renamed build and its title bars read `— MegaPDF`. The notices on all four
   platforms, the privacy policy and the Windows listing copy moved with it. **One thing
   deliberately left spaced:** `Package.appxmanifest`'s `DisplayName` (twice) and tile
   `ShortName` stay "Mega PDF", because the Store checks the manifest against the reserved
   Partner Center name and "MegaPDF" was taken — so Start and Settings ▸ Apps keep showing
   the spaced form until Dave changes the reservation. A trap caught on the way:
   `gen_listing_copy.py` had never been given `c1b0a4a`'s Apple 2.3.10 fix, so re-running
   it would have put "MegaPDF for Windows and Android" back into the App Store
   description; the generator is fixed.

### Dave's decisions, 2026-09-18 (about 07:15 EDT)

Recorded here because six of them closed items this document previously listed as open.

| | |
|---|---|
| **The name** | **MegaPDF**, everywhere. Windows moves — title bar, About/Settings heading, package resources — and `tools/gen_third_party_notices.py`'s `NAME` map moves with it. After the Microsoft gate set. The Partner Center reservation is Dave's to change at submission. |
| **#172 iPad** | 2.0 ships the iPhone layout on the iPad; the iPad's own toolbar is 2.1, and the issue has moved to that milestone. This ticks #146's "findings filed" box. |
| **fr-CA and fr-FR sets** | Upload both as shot. The demo name is the only difference and it is intended. |
| **#237** | Fix the layout at the declared minimum, not by raising it. |
| **Remove protection** | 2.0 must write an unencrypted file — **#241**, 2.0 milestone. |
| **#186** | Stays accepted, as of 2026-09-17. Not fixed for 2.0. |
| **The French** | A human reviewer has the pack and is the authority. An independent Claude Fable read runs in parallel, findings only. |

### Waiting on Dave

Five things, now that the decisions above are taken.

1. **The human francophone review.** The pack is ready and merged:
   `docs/release-notes/2.0/french-review.md`, 230 rows, 37 needing judgement, with
   captures. **31 of the 37 are redaction, and most of those are one word — `Caviarder`
   for *Redact*.** It gates the release notes and the French listing copy, and it is the
   authority; the parallel read below is a second opinion, not a replacement.
2. **The final capture review** — §3's last line, and by decision the last thing before
   submitting. In practice it is five images, one viewer shot per device, because
   anything identical in every image of a set has no sibling for the gate to compare it
   against.
3. **The Partner Center name change**, at submission. The reserved Windows Store name is
   Dave's to change; the code side is in hand.
4. **One elevated click on GPD-DAVE** so WACK can run. The rename has landed, the package
   is rebuilt from `8a77b8c`, and **WACK is raised and waiting on the prompt** — so this is
   now a single click rather than a piece of work.
5. **The go-ahead itself.** Release hold since 2026-09-14 — and now the single gate on all
   four store builds, since `ios-release` and `android-release` fire only on a version tag
   and tagging is a release action.

**No longer waiting on anyone**, and moved off this list by the decisions above: the app's
name, #172, the identical French sets, #237, the leftover encryption dictionary (now
#241), and #186. Two further items gate nothing and belong on their own issues rather than
here — with one exception now pulled forward. **#173 is closed**: Dave decided at 08:00
EDT that Android gets no hint strip, the armed state being announced is what a screen
reader needs, and the two dead strings are removed (`73670fb`). **#145 keeps its P2/P3
remainder, except for one item Dave pulled into 2.0** — on Windows, launching with a file
skips the crash-recovery offer (`App.xaml.cs:78-79` returns before
`OfferCrashRecoveryAsync()` on `:82`), so a crash followed by double-clicking a PDF never
offers to restore. The Windows agent is fixing it now.

### The independent French read — #242, findings only

`docs/release-notes/2.0/french-review-fable.md`. No strings changed. **52 findings: 10
error, 19 should, 23 taste**, with a verdict on every one of the 37 flagged rows.
**`Caviarder` stands** (the OQLF/Termium term; Acrobat's *Biffer* noted for the reviewer)
and so does *Afficher dans Fichiers* — which settles the question the pack said would
settle most of the section.

Most of the ten errors are in the store copy and the fr-FR derivation rather than in the
app: the App Store block names whiteout in the iOS copy against its own header; the
redaction paragraph added on 2026-09-17 never went through `fix_french_spacing.py`, so it
carries a plain space before its colon in App Store, Mac App Store and Play in both
variants; the redaction heading lacks the blank line every other heading has; the fr-FR
Play long form carries a Quebec idiom ("étaient rendues à 1.7") the derivation cannot
catch; the fr-FR Microsoft listing reads "adaptée au e-mail" because the *courriel →
e-mail* table never contracts the article; the long notes carry "texte contourné",
"éditeur en ligne" and "Les gros fichiers" against the glossary's own *volumineux*; and
`redact_hint` promises an Esc key on phones, an English defect the French inherited.
Every block was recounted: all counts exact, all inside their limits, Play fr at 489/500.

**The ten are fixed and on main** — #245, fast-forwarded at 08:15 EDT after CI went green
on the rebased head `08c3865`: the fr-FR derivation contracts « au courriel » to
« à l'e-mail » (`5043f6d`), the phones' Redact hint stops promising an Esc key (`01ba248`),
the store copy loses the iOS whiteout claim and gains its blank line with
`fix_french_spacing.py` re-run and the France Play idiom removed (`3965654`), and three
words in the long notes are corrected (`08c3865`). Every block recounted and equal to its
stated count: App Store 1760 / 2100 / 2100 of 4000; Mac 2402 / 2886 / 2886 of 4000;
Microsoft 1483 / 1479 / 1479 of 1500; Play 495 / 489 / 489 of 500. `gen_strings.py fr-fr`,
`gen_listing_copy.py` and `fix_french_spacing.py --check` all leave the tree clean.
The human review then signed the French off, and on Dave's word (2026-09-18, 23:55 EDT)
**the rest of the review was implemented too**, in PR #242: all 19 *should* items and 15 of
the 23 *taste* items (app strings `98c388c`, store copy and listings `a48f0d6`; the review
marks every row applied or skipped, with the reason). Recounted: App Store 1760 / 2130 /
2130 of 4000; Mac 2402 / 2910 / 2908 of 4000; Microsoft 1483 / 1485 / 1485 of 1500; Play
495 / 492 / 492 of 500. One store capture changes: the Mac redact pose in both French sets,
where the hint now says « l'élément actif ».

### Deliberately deferred — recorded, not forgotten

- **Linux ships after** the other four (§5, Dave 2026-09-17). #158 stays open.
- **2.1:** #142 text extraction (first), #168 reading mode, #174 page tools, #153 stale
  Mac recovery prompt, #154 re-deflate on save, and **#172 the iPad's own toolbar**
  (Dave, 2026-09-18). **Backlog:** #175 OCR.
- **Parked:** #2 keyboard accessibility.
- **Accepted:** #186 (Android find bar and three-button prompt at the largest text size)
  — closed as *not planned*, so the decision was taken.
- **#226 fixed and closed** (`8a77b8c`): the stray "Ctrl+F" chip was WinUI's own
  accelerator tooltip for `RootGrid`, whose `KeyboardAcceleratorPlacementMode` was never
  `Hidden`. Verified gone on a reinstalled package through UI Automation, with Ctrl+F
  still opening Find.
- **Not blocking by standing decision:** #1, #3, #4, #5, #6, #9, #29, #33, #98, #129.

## 4. Risks worth knowing before "go"

**Almost everything here was proved by an agent, not by a person** — with one change since
the last audit. The Windows, Mac, iOS, Android and Linux QA passes were all driven by build
assistants: real windows, real devices and emulators, real files, but no human looked at
1,347 Android captures or 234 Linux ones. **The store sets are different**: each of the
five was read image by image by a person *and* measured by `tools/capture-gate`, and the
two reads agree. That is the control §3 exists to be, and it has now run on five of six
sets. What it still does not cover is whether a set makes someone want MegaPDF.

**The gate cannot see anything that is the same in every image of a set.** Every check is a
comparison — against a slot size, against the same pose in another language, against a set
already signed off — so device chrome has no sibling to disagree with it. The launcher
taskbar sat in all 24 Play tablet captures and the tool cleared them. Two rules were tried
against the data and the data dropped both. Hence the five named images in §2 of the
capture-gate report: that is where a constant hides.

**Redaction's known limit is an image limit.** 1,502 of 4,158 corpus documents refuse, 855
of them because the picture cannot be written back — a scan stored at one bit per pixel
cannot go through PDFium's 8-bit bitmap API. Written up in ADR-005 and stable across the
whole battery, including the 2026-09-18 RC re-run, which matched the baseline count for
count. It is the right behaviour (refuse rather than half-redact) but it is the thing a
user is most likely to hit and be surprised by. Worth recording beside it: a greyscale
image that *is* redacted comes back DeviceGray → DeviceRGB, roughly tripling in size.

**Redaction's accessibility gaps are closed, and were only found in real windows.** On the
Mac and iOS (`98a893d`) and on Android (`476ce15`), a screen reader could not tell whether
Redact was armed, and a mark had no accessible presence. Three platforms, three different
causes, one symptom — and on Android the same walk found that **a drag along a line of text
marked nothing at all**, because a mark needed both extents over a threshold and such a
drag is flat. Every one of these was invisible to a green test run. The generalisation
worth keeping: the headless checks call the engine directly and never touch the commands,
so anything between a keystroke and the engine was uncovered until someone drove a window.

**iOS uses the `shareddocuments:` URL scheme** to reveal a recent file in Files
(`ViewerModel.swift:555`). It is a public scheme rather than a private interface, it is
deliberately not in `LSApplicationQueriesSchemes`, and the app falls back to a message
when the system declines. Low risk, but it is the kind of thing an App Store reviewer asks
about, and the answer should be ready rather than improvised.

**ITMS-90886 (the asset-catalog app icon warning) is not recorded anywhere in this repo.**
The precondition is satisfied — `ios/project.yml` sets
`ASSETCATALOG_COMPILER_APPICON_NAME: AppIcon` and `AppIcon.appiconset` exists — but
whether the warning appears can only be established by an upload, and none has been made
for 2.0. The same is true of every other ITMS-class warning: they are found at delivery.

**An iOS capture run that crosses midnight produces a set that disagrees with itself.**
`simctl status_bar` overrides the time and the indicators but not the date, and the iPad's
status bar carries one. It happened once; the English iPad set was re-shot. The gate
compares the whole status bar across languages now and would catch a repeat, but only
across languages within one run — which is, as it happens, where it appeared.

**Google Play is five minor versions behind.** Its notes carry 1.2 → 2.0 in a
500-character field, so features that shipped to the other stores a while ago are new
there. That is handled in the copy, but it means the Play listing is the one most likely
to need a second pass.

## 5. What I could not verify from here

- **That any 2.0 store build has been *delivered*.** The Windows 2.0.0.0 MSIX exists and
  was installed on the machine that built it; no WACK report at that version, no `.pkg`,
  no processed TestFlight or Play build was found. I looked; absence of evidence is what I
  am reporting.
- **Anything needing a device or a person**: the 200 % Mac captures (no HiDPI display on
  the capture Mac), the Play Console's aspect ratio and tablet slot, WACK's one elevated
  click, Linux's portal file dialog and one real print, VoiceOver and Narrator beyond the
  scripted checks, and the register of the French.
- **The Android `redact` pose**, which is now the one capture still to be re-shot. The
  Microsoft set left this list on 2026-09-18, gate-clean; the **preview videos** left it
  the same day, nine clips cut and read frame by frame (`preview-videos.md`). Four things
  in the clips are a decision rather than a defect, and they are in that report's §5.
- **Whether the captures are good enough to list.** They are measured and they were read;
  whether each set sells the app is §3's last question and only Dave can answer it.
