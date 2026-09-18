# MegaPDF 2.0 — release readiness, audited against main (#146)

Audited at `a639467558d9168a6e6f294d776ef60da359e1d7`'s tree on 2026-09-18. Every claim
below was checked against the repository rather than taken from a comment: each SHA
resolved and tested for ancestry of `main`, each `#N` queried for its real state, each
artefact stat'd in the tree.

**Verdict: 2.0 is not ready to submit, and nothing is blocked by anything unknown.**
The engineering is done and the QA passes are done on all five platforms. What is left is
release preparation (§2) and the capture gate (§3), both of which are sequenced work that
has not started, plus the three things only Dave can do. One open QA finding (#172) needs
a decision rather than a fix.

---

## 1. Where #146 and main disagree

Everything else in §1 checks out: all fourteen commits the issue names exist and are
ancestors of `main`, and every artefact it claims — five screen inventories, the Android
QA results, seven release-note files, the large-fixture generator, the PDFium README, the
leakcheck sources — is in the tree. These five are the discrepancies.

| | What #146 says | What main says |
|---|---|---|
| **#144, #145, #139, #143** | ticked `[x]`, "merged" | the merges are real and on main — but **all four issues are still open**. Housekeeping, not risk; someone should close them or say what is left on each. #139 and #145 still carry `bug`. |
| **Version bump** | "`release-prep/2.0-versions` (9286af1)" | the branch is at **`dc3e8ba`** and is **44 commits behind main**. `9286af1` is a real commit on it, just not its tip. It will need a rebase before it merges. |
| **#220** | not mentioned | open, in the **2.0 milestone**, filed an hour before this audit: `core/` with `-DMEGAPDF_CORE_TESTS=ON` cannot be built on Windows (a stray file named `Release`). Developer tooling only — CI never sees it, and it does not touch a shipping binary. |
| **#165** | not mentioned | open, in the **2.0 milestone**. The code says it is finished on all four platforms — Windows `7647831`, Mac `7db2a67`, Android `df98bd7`, **iOS `d5b3109`, and `ios/MegaPDF/HomeView.swift:45` draws the location today**. Its most recent comment says "iOS has no location at all", which the file contradicts. Verify and close. |
| **"CI green on all workflows"** | ticked | true for the four that run on `main` (CI, Android CI, iOS CI, Core tests — each green on the last commit that touches its paths). **`macOS app` never runs on `main`**: it is `pull_request` + `workflow_dispatch` only. Its last green is a PR run at `f5ec580`. Not a gap, but "green on main" cannot include it. |

## 2. Per platform

| | Engineering | §1b QA pass | Captures gate-ready | Tests | Outstanding |
|---|---|---|---|---|---|
| **Windows** | merged | done — inventory, captures (en/fr-CA/fr-FR × light/dark × 1280/1000/800/480 + 200 %), all flows on the installed package | harness updated (`9a42a71`), French demo names in `gen_store_docs.py` | CI green on main | **#220** (dev tooling only) |
| **macOS** | merged | done (PR #159) — inventory, 117 captures, every flow incl. protection and `kill -9` recovery | `macos-screenshots.yml` exists; not re-shot on 2.0 builds | `macOS app` green on its last PR run | **#173** — a redaction mark has no accessible presence; no 200 % capture (no HiDPI display on the capture Mac) |
| **iOS** | merged | done (PR #159) — inventory, 159 captures incl. AX5 | `ios-screenshots.yml` + `tools/ios-screenshots.sh`; not re-shot | iOS CI green at `6901665` | **#172 open** — iPad has no iPad-specific UI; **#173** — a screen reader cannot tell Redact is armed |
| **Android** | merged | done (PR #182) — inventory, 1,347 captures over 27 cells, **and the same set again after the fixes**, 18/18 flows, all 13 large fixtures | rig **and runbook** done (`docs/qa/android-store-captures.md`); two Play Console questions written down | Android CI green on main | aspect ratio and the tablet slot need the Play Console |
| **Linux** | merged | done — inventory, 234 captures, 18 integration checks, battery | `.deb` + Flatpak build and are checked in CI | CI green on main | **#158 open by design** — ships after the others; Flathub submission needs the Print portal route |

## 3. What is left

### Blocking — no store gets a build until these are done

1. **§2 version bump.** `release-prep/2.0-versions` is prepared and deliberately unmerged.
   44 commits behind; rebase, then merge.
2. **§2 store builds.** WACK pass (Windows), sandbox probe and Mac App Store package,
   and the `ios-release` and `android-release` builds. **None has been run for 2.0**, and
   the run history says so rather than merely failing to mention it: the last
   `iOS release` was **2026-09-11** and the last `Android release` **2026-08-29**, both
   before 2.0's work began, and both fire only on a version tag (`ios-v*`, `android-v*`)
   — which cannot exist until the version bump merges. `macOS App Store package` ran
   three times on 2026-09-17, all on **pull requests**, which validate the signing chain
   and deliberately do not deliver.
3. **§3 the capture gate, in full.** Re-shoot every capture on the final 2.0 builds, in
   every listing language; re-cut the iPhone, iPad and Mac preview videos; check every
   image and frame by eye; re-read the listing copy. Nothing in §3 has run against a 2.0
   build — the rigs are ready, the shoot is not done.
4. **#172 — a decision, not necessarily a fix.** It is the one open finding from the §1b
   sweep and it is why #146's "Findings filed" box is unticked. The iPad has no
   iPad-specific UI at all, which is a product call rather than a defect to patch before
   a deadline. Either accept it for 2.0 and say so, or move it to 2.1.

### Waiting on Dave

- **The francophone review.** The pack is ready: `docs/release-notes/2.0/french-review.md`,
  230 rows, 37 needing judgement, with captures. It gates the release notes and the
  French listing copy.
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
- **Not blocking by standing decision:** #1, #3, #4, #5, #6, #9, #29, #33, #98, #129.

## 4. Risks worth knowing before "go"

**Almost everything here was proved by an agent, not by a person.** The Windows, Mac, iOS,
Android and Linux passes were all driven by build assistants: real windows, real devices
and emulators, real files — but no human looked at 1,347 Android captures or 234 Linux
ones. The §3 gate exists precisely because Dave's own eyes are the control on that, and it
has not run yet. Treat §3 as the first human review of the 2.0 UI, not a formality.

**Redaction's known limit is an image limit.** 1,502 of 4,158 corpus documents refuse, 855
of them because the picture cannot be written back — a scan stored at one bit per pixel
cannot go through PDFium's 8-bit bitmap API. Written up in ADR-005 and stable across the
whole battery. It is the right behaviour (refuse rather than half-redact) but it is the
thing a user is most likely to hit and be surprised by.

**Two accessibility gaps in redaction are open on #173** — on neither Mac nor iOS can a
screen reader tell that Redact is armed, and a mark has no accessible presence on the Mac.
A reviewer is unlikely to find them; someone using VoiceOver would.

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

**Google Play is five minor versions behind.** Its notes carry 1.2 → 2.0 in a
500-character field, so features that shipped to the other stores a while ago are new
there. That is handled in the copy, but it means the Play listing is the one most likely
to need a second pass.

**`macOS app` has no run on main.** Every macOS build is proved on a pull request. If
something lands on main by a route that skips a PR, no Mac build covers it until the next
one.

## 5. What I could not verify from here

- **That any 2.0 store build exists.** No WACK run, no `.pkg`, no processed TestFlight or
  Play build was found. I looked; absence of evidence is what I am reporting.
- **Whether #144, #145, #139 and #143 have remaining scope.** They are open with merged
  work; only the person who filed them can say whether that is an oversight or a remainder.
- **Anything needing a device or a person**: the iPad question in #172, the 200 % Mac
  captures (no HiDPI display on the capture Mac), the Play Console's aspect ratio and
  tablet slot, VoiceOver and Narrator beyond the scripted checks, and the register of the
  French.
- **The captures themselves.** They exist as files from the QA passes; whether they are
  *good enough to list* is §3's question and has not been asked yet.
