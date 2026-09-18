# MegaPDF 2.0 — release readiness, audited against main (#146)

Audited at `c64a4409`'s tree on 2026-09-18, re-checking the 2026-09-18 03:00 EDT audit of
`a639467`. Every claim below was checked against the repository rather than taken from a
comment: each SHA resolved and tested for ancestry of `main`, each `#N` queried for its
real state, each artefact stat'd in the tree, and CI read per workflow.

**Verdict: 2.0 is not ready to submit, and nothing is blocked by anything unknown.**
The engineering is done and the QA passes are done on all five platforms. The version bump
has merged, the listing copy has been re-read, and **five of the six store capture sets are
shot from 2.0.0 builds and gate-clean**. What is left is one capture set, the store builds,
the preview videos, and the decisions only Dave can take.

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
| **Windows** | merged | done — inventory, captures (en/fr-CA/fr-FR × light/dark × 1280/1000/800/480 + 200 %), all flows on the installed package, plus an RC sweep on 2026-09-18 | **the one set not yet shot from 2.0.0.** The 2.0.0.0 MSIX is built and installed on the machine that holds the set; the `microsoft` gate profile has never met a real set | CI green on main; Core tests 276/276 and `ctest` 1/1 on Windows | the capture set, and **WACK at 2.0.0.0 — one elevated click** |
| **macOS** | merged | done (PR #159) — inventory, 117 captures, every flow incl. protection and `kill -9` recovery; re-walked in the 2026-09-18 RC sweep | **18 images at 1440×900, from a 2.0.0 build — gate-clean**, and read image by image first | `macOS app` green on the PR run whose head is main's head | **no 200 % / Retina set** — no HiDPI display, and `--scale 2` misplaces every page overlay (measured, and the capture now refuses). Covered by Dave's 2026-09-16 Retina call |
| **iOS** | merged | done (PR #159) — inventory, 159 captures incl. AX5; RC sweep 2026-09-18 | **36 listing images (1320×2868, 2064×2752) plus 30 review shots, from a 2.0.0 build — gate-clean** | iOS CI green on main | **#172 open** — the iPad has no iPad-specific UI, which is a product decision and the last thing holding §1b's "findings filed" box |
| **Android** | merged | done (PR #182) — inventory, 1,347 captures over 27 cells, **and the same set again after the fixes**, 18/18 flows, all 13 large fixtures; RC sweep 2026-09-18 added a redaction flow group the rig had never had | **phone 24 at 1080×2400 and tablet 24 at 1600×2560, from the 2.0.0 APK — both gate-clean.** One pose (`redact`) needs re-shooting: #173's Save fix makes Save live and blue where it was grey | Android CI green on main | the Play Console's two questions (9:20 aspect ratio, whether there is a tablet slot); the app module still has no `androidTest` harness |
| **Linux** | merged | done — inventory, 234 captures, 18 integration checks, a battery matching Windows and Mac | not a store yet; the QA set is where #237 was found | CI green on main (`Linux app`, `Linux packages`) | **#158 open by design** — ships after the others. Printing in the sandbox landed 2026-09-18; the portal file dialog, recents across a restart and one real print still need a GNOME session |

## 3. What is left

### Blocking — no store gets a build until these are done

1. **§2 store builds.** The version bump has landed, so the tags that gate them are now
   possible — but **tagging is a release action and the hold since 2026-09-14 covers it.**
   - **Windows:** `MegaPDF.App_2.0.0.0_x64.msix` is built from `edd68ec` with the #166
     clean-output recipe and installed; About reads 2.0.0 and the resource map is right.
     **WACK has not run at 2.0.0.0**: `appcert` needs elevation and the UAC prompt was
     cancelled twice at 23:41 EDT on the 17th with nobody at the console. One approval,
     about half an hour. The 2.0 candidate at 1.7.0.0 already passed overall, with only
     the two known optional FAILs — both Windows App SDK noise.
   - **Mac:** `macOS App Store package` has only ever run on pull requests, which validate
     the signing chain and deliberately do not deliver.
   - **iOS and Android:** `ios-release` and `android-release` fire only on `ios-v*` /
     `android-v*`. Last runs were 2026-09-11 and 2026-08-29, both before 2.0's work.
2. **§3, the rest of the capture gate.**
   - **The Microsoft Store set**, which is the one set not shot from a 2.0.0 build. It
     lives on the Windows machine; §5 of `capture-gate-report.md` has the command. Expect
     the slot sizes and pose names to want a line each in `tools/capture-gate/stores.py` —
     that profile is written from a runbook and has never met a real set.
   - **The preview videos** (iPhone, iPad, Mac) — not started. The recording scripts take
     a language now, so a French clip is French throughout.
   - **One Android pose**, `redact`, for the reason in the table above.
   - **The human read.** The five shot sets were each read image by image *and* measured by
     `tools/capture-gate`, and the two reads agree — 0 images to look at in all five. What
     no comparison can clear is anything identical in every image of a set: one viewer
     image per device, five in all, named in §2 of the report. The launcher taskbar that
     sat in 24 Play tablet captures is the worked example — the gate clears that set
     today, knowing it is there.
3. **#172 — a decision, not necessarily a fix.** It is the one open finding from the §1b
   sweep and it is why #146's "findings filed" box is unticked. The iPad has no
   iPad-specific UI at all, which is a product call rather than a defect to patch before a
   deadline. Either accept it for 2.0 and say so, or move it to 2.1. The iPad capture set
   is already shot and is its evidence.

### Waiting on Dave

- **The francophone review.** The pack is ready and merged:
  `docs/release-notes/2.0/french-review.md`, 230 rows, 37 needing judgement, with
  captures. **31 of the 37 are redaction, and most of those are one word — `Caviarder`
  for *Redact*.** It gates the release notes and the French listing copy. Two pieces of
  French arrived *after* the pack was built and need the same read: the redaction copy
  added to all four listings (`e9aefb0`) and the reworded Linux print-portal message
  (`c64a440`).
- **`Mega PDF` against `MegaPDF`.** The two desktops disagree in a way a listing
  screenshot shows: Windows titles its window `— Mega PDF`
  (`src/MegaPDF.App/MainViewModel.cs:441`, "the reserved Store name and the manifest
  DisplayName") where the Mac shows `— MegaPDF`. The spaced form is also in
  `THIRD-PARTY-NOTICES.txt`, which both Apple apps show in-app, five times over, from the
  `NAME` map in `tools/gen_third_party_notices.py`. Everywhere a person otherwise looks it
  is unspaced. Whichever way it goes, the notices generator and the Windows title move
  together — and changing the French strings would cut across the review pack, so this
  wants deciding *before* that review rather than after.
- **Whether the two French listings should differ by more than one name.** fr-CA and
  fr-FR are the same image in 5 of 6 Mac poses, 4 of 12 iOS listing poses and 7 of 8 Play
  poses, because the demo person is the only string that differs between the two
  catalogues on any listing screen. Correct rather than a defect; App Store Connect wants
  a set per localisation, so both go up looking alike.
- **#237** — at Linux's 480×360 minimum window the find bar overflows the frame and the
  empty state runs off the bottom. Make that size lay out, or raise the declared minimum
  and stop offering a size the window cannot draw. Not a 2.0 blocker.
- **#173's last open item** — `redact_hint` and `redact_tip` are in all three Android
  catalogues and unused, so Android's armed state is audible but unexplained where the
  other three platforms show the hint. Wiring it adds a visible strip, which moves a store
  capture that has just been certified.
- **The Mac's leftover encryption dictionary.** After protection is removed the file keeps
  an encryption dictionary with an empty user password: it opens with no prompt, shows no
  restricted banner and is fully editable, so nothing is wrong from the user's side. The
  question is only whether stripping it entirely was the intent. Windows does not
  reproduce it — there `qpdf` reports "File is not encrypted" after removal.
- **#145's remainder** — whether P2 and P3 become a 2.1 item or stay open on that issue.
- **The final capture review** — §3's last line, and by decision the last thing before
  submitting.
- **The go-ahead itself.** Release hold since 2026-09-14.

### Deliberately deferred — recorded, not forgotten

- **Linux ships after** the other four (§5, Dave 2026-09-17). #158 stays open.
- **2.1:** #142 text extraction (first), #168 reading mode, #174 page tools, #153 stale
  Mac recovery prompt, #154 re-deflate on save. **Backlog:** #175 OCR.
- **Parked:** #2 keyboard accessibility.
- **Accepted:** #186 (Android find bar and three-button prompt at the largest text size)
  — closed as *not planned*, so the decision was taken.
- **Open and not a blocker:** #226 (a stray "Ctrl+F" chip after the first … menu of a
  Windows session is dismissed by clicking the page) — characterised, two fixes tried and
  reverted.
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
- **Anything needing a device or a person**: the iPad question in #172, the 200 % Mac
  captures (no HiDPI display on the capture Mac), the Play Console's aspect ratio and
  tablet slot, WACK's one elevated click, Linux's portal file dialog and one real print,
  VoiceOver and Narrator beyond the scripted checks, and the register of the French.
- **The Microsoft Store capture set**, which is on the Windows machine and is being shot
  there; and **the preview videos**, which have not been cut.
- **Whether the captures are good enough to list.** They are measured and they were read;
  whether each set sells the app is §3's last question and only Dave can answer it.
