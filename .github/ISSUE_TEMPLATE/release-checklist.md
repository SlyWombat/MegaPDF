---
name: Release checklist
about: One issue per release. Work down it in order; every run id, submission id and hash goes in the comments. The runbook is docs/RELEASING.md.
title: "<version> release checklist"
labels: release
---

<!-- Replace <ver> (x.y.z), <ver4> (x.y.z.0) and <vc> (Android versionCode) once, then work down.
     docs/RELEASING.md has the order, the gates and the traps; the deep steps are linked from it.
     2.0 was #146, 2.1.1 was #395, 2.4.0 was #817: one comment per finding, as it happens.
     Decisions that are Dave's: one at a time, each with a recommendation, his answer a timestamped comment here. -->

Dave's go-ahead: <quote and date>. Live before this release: Windows <>, iOS <>, Mac <>, Play <> (vc <>), Linux <>.

Built from (fill in as each is cut; platforms may differ, `docs/RELEASING.md` § 3 tag rule):

| platform | tag | full SHA | run | what differs from the others |
|---|---|---|---|---|
| Windows (CI packages) | — | | | |
| iOS | `ios-v<ver>` | | | |
| Mac | `ios-v<ver>` (the workflow's `--ref`) | | | |
| Android | `android-v<ver>` | | | |
| Linux (and Snap) | `linux-v<ver>` | | | |
| CLI archives | `windows-cli-v<ver>`, `macos-cli-v<ver>` | | | |

## What this release carries
- #… — one line each, the user-visible thing.

## 1. Version (`docs/RELEASING.md` § 1)
- [ ] `Version <ver> everywhere`, one commit, alone: both `.csproj`, `Package.appxmanifest` (`<ver4>`), `ios/project.yml`, `build.gradle.kts` (`versionCode` <vc>), `core/cli/megapdf_cli.cpp` `kVersion`, `tools/msstore_submit.py` `VERSION`, `Shoot-Set.ps1` default, metainfo `<release>` (dated today), `tools/linux/PACKAGE-REVISION` → `<ver> 1`, `website/megapdf/linux/index.html` — `grep -rn '<old version>' src ios android core tools website` finds only history.
- [ ] Milestone re-homed: everything shipping here is in it, nothing else is.

## 2. Gates, in order
- [ ] **CI green on the release commit** — `gh run list --commit <full sha> --json name,conclusion,url`; run id: ___. (An Android/iOS-only commit has no `ci.yml` run: name the run whose tree matches.) A flake that passed on re-run has its own issue: ___.
- [ ] **Corpus batteries on kdocker3**, all five corpora, the release commit and the previous release built the same way — `tools/stress/structure-battery.sh … --reference --cli …`, `markdown-battery.sh …`, `pages-battery.sh … --jobs 4`, `redaction-battery.sh <megapdf_leakcheck> …`, `MegaPDF.Stress run … --phases edits` + `compare_runs.py` against the previous release; F1 ≥ 0.998, τ ≥ 0.9, 0 crashes / hangs / cmark / qpdf / read-back failures, redaction 0 leaked / 0 changed outside / 0 hung; per-document times against the last run; results in the gitignored `tests/Private/stress/release-<ver>-kdocker3/`, no corpus filename anywhere in the repo. Every line that does not read zero is in the `docs/RELEASING.md` § 2.2 table or has its issue — and was reproduced on the previous release's build before being called pre-existing: ___. Dave's ruling on each: ___.
- [ ] **Windows, real machine** (GPD-DAVE) — `gh run download <run> -n MegaPDF-store-packages`; `check-native-arch.py` x64 + arm64, identity, resource map; `Install-QaTestPackage` from `tools/windows-qa/qa-safety.ps1` after `Start-QaUserDataWatch` (never a bare `Remove-AppxPackage`, #717); `artifacts/store/rc-<ver4>/SHA256SUMS`; click list: tabs, open from Explorer into the running window, fill / tick / sign / Save / reopen (read back with `pdftotext` / `qpdf`), Save As Markdown (PDF sha unchanged), About, fr-CA chrome, dirty close per tab.
- [ ] **Mac, real machine** (Mac mini) — `tools/build-macos-app.sh`, `--self-test`, then the app: same click list, Finder open, Info.plist version.
- [ ] **iOS, real simulator** (Mac mini) — `xcodebuild test`, then Files → share sheet → MegaPDF, Share (Save / Share without saving / Cancel), Export as Markdown, fr-CA, built Info.plist version.
- [ ] **Android, Pixel or emulator** — Files → Open with, Share, Export as Markdown, fr-CA; `aapt dump badging` = <ver> / <vc>.
- [ ] **Android permissions** (#821) — `./gradlew :app:checkNoPermissions` green; on the Pixel `dumpsys package` lists neither `INTERNET` nor `ACCESS_NETWORK_STATE`, the process has no gid 3003, OCR Find on an image-only PDF finds the word; the signed `app-release.aab` manifest read after the tag (§ 3): none. Every "no permissions" / "no network" claim re-read against it — `android/RELEASING.md` Data safety, `docs/app-store-listing.md`, website, privacy policy: ___.
- [ ] **Linux, clean containers** — `tools/linux/check-apt-repo.sh <repo> <fixtures>` (five distributions, runs `megapdf-cli`), `package-check.sh`, tarball `install.sh` as non-root; `MegaPDF --install-kind`, `megapdf-cli --version`.
- [ ] **Show-stoppers**: none open — or each one's fix merged and the release commit moved to the merge: ___.
- [ ] **Screenshots re-shot** where chrome changed, en / fr-CA / fr-FR: Windows `Shoot-Set.ps1 -Version <ver4>` per language; iOS `tools/ios-screenshots.sh`; Mac `tools/macos-store-captures.sh`; Android `android-screenshots.yml` or the emulator; `tools/capture-gate/gate.py`. Resting only in `artifacts/store/captures-<ver>/{ios-screenshots,macos-screenshots,play/phone}/<lang>/` and `artifacts/store/screenshots/<lang>/`.
- [ ] **Every image read by eye** — no keyboard tip, grabber, real clock, cog; **no file picker, Explorer, Finder or recent list with personal files** in any public capture.
- [ ] **Copy** — `docs/release-notes/<ver>/` (four store files, long form, README with the delta table, the issue per line, counts, French review); `python3 docs/release-notes/<ver>/check_copy.py --check`; `python3 tools/gen_listing_copy.py`; limits MS 1500 / Apple 4000 / Play 500 / App Review notes 4000; Apple copy names no other platform and no ✕.
- [ ] **French** — every new app string and every new store block reviewed; fr-FR only by `python3 tools/gen_strings.py fr-fr` and `check_copy.py`; the review recorded in the README.
- [ ] **Spanish** (#724) — every `ES-REVIEW` app string and every new store block reviewed by Fable, the approval recorded on #724 and in the README; es-ES only by `python3 tools/gen_strings.py es-es` and `check_copy.py`; no marker left on a string that shipped unreviewed; the Play and Linux Spanish release notes are not `PENDING`.
- [ ] **WACK** at <ver4> on the CI x64 package — `wack-prep.ps1`, the `MegaPDF WACK` scheduled task started at ___ **with Dave at the console** (told the time beforehand; ~3 minutes); PASS with only the two known optional FAILs (#58, #88); report renamed `wack-report-<ver4>-<date>.xml` and `wack-run-<date>.log` kept; `OVERALL_RESULT` / `APP_VERSION` quoted here; task deleted.
- [ ] **Docs** — README, TESTING, `tools/Linux-Packaging.md`, `tools/linux/megapdf-cli.1`; website: gallery from this release's sets, footer source link, privacy date. Not deployed yet.
- [ ] **Hand-written sweep** (`docs/RELEASING.md` § 2.7) — landing page `website/megapdf/index.html` "New in <x.y>" replaces the old block and the "Private by design" languages are right; support page "The current release is" + version placeholder (worded for what is live at deploy time); README "Store status as of"; `website/README.md` mailbox lines; release-notes "Draft … not yet approved" headers and any "not yet reviewed" sentence; `node website/check-support-page.js` passes.
- [ ] **New locale?** Linux metainfo screenshot URLs for it are in `website/megapdf/screenshots/linux/<locale>/` before the `.deb` is public; Play tablet set for it decided: ___.

## 3. Submit (`docs/RELEASING.md` § 3) — each tag from that platform's final tree, tagged once
- [ ] **Windows** — `tools/msstore.sh build` → `signin` → `status` → `plan` → `submit` → `status`. Submission id: ___, publish Immediate.
- [ ] **iOS** — `git tag ios-v<ver> <sha> && git push origin ios-v<ver>` → `ios-release.yml` run ___ → build N = ___. Then `tools/asc-publish.sh version <ver>` → `copy` → `screenshots artifacts/store/captures-<ver>` → `review` → `assets-state` (loop to COMPLETE) → `build <N>` → `version-info` → `submit`.
- [ ] **Mac** — `gh workflow run macos-appstore.yml -f upload=true --ref ios-v<ver>` → run ___ (`headSha` = the iOS tag's) → build ___ (the version string). Then the same `asc-publish.sh` sequence with `ASC_PLATFORM=MAC_OS`.
- [ ] **Play** — `git tag android-v<ver> <sha> && git push origin android-v<ver>` → `android-release.yml` run ___ → internal. Then `tools/play.sh listing status`; `push artifacts/store/captures-<ver> --production <vc> --phone-only` (validate) → the same with `--commit` → `readback …`; `tools/play.sh listing contact` (the first time also `--website https://electricrv.ca/megapdf/support/ --commit`). Edit id: ___. Run from a tree at `origin/main`.
- [ ] **Linux** — `git tag -a linux-v$(tools/linux/package-version.sh <ver>) <sha> -m "MegaPDF <ver> for Linux"`; run ___; `gh run download <run> -n MegaPDF-apt-repository -D <scratch>/apt` (copy `dists/` and `pool/` in, per RELEASING.md § 3); `python3 website/deploy.py --dry-run --linux --snap --support --privacy` (its `Login file` line says found); then `gh release edit linux-v<ver> --draft=false`; then the same `deploy.py` without `--dry-run`.
- [ ] **CLI archives** — `windows-cli-v<ver>` and `macos-cli-v<ver>` tags; download each draft's zip and **run it** (sha256, `--version`, an extract) on GPD-DAVE / the Mac mini; `gh release edit <tag> --draft=false`.
- [ ] **Snap** — `gh workflow run snap.yml -f upload=true --ref linux-v<ver>` → run ___ → edge revision ___; edge installed and run (`megapdf.cli --version`, an extract); Dave asked, then `-f promote=true -f promote_revision=<n> --ref linux-v<ver>` → run ___.
- [ ] **Website** — after the go-lives, from a fresh worktree at `origin/main`: the 200 check, `python3 website/deploy.py --dry-run --linux --snap --support --privacy` → the same without `--dry-run`; redeploy the same four flags after any later website change. Re-read the stores right before the deploy and word the support page's "current release" line for what is live.

## 4. Read back from the outside, then close
- [ ] Windows: `tools/msstore.sh status` shows <ver4> Published, no pending submission (the public page https://apps.microsoft.com/detail/9PF4TRRH4M76 shows no version).
- [ ] Apple iOS: the iTunes lookup, cache-busted (`&_=$(date +%s)`) and in more than one storefront, shows <ver>; `tools/asc-publish.sh status` says READY_FOR_DISTRIBUTION.
- [ ] Apple Mac: `ASC_PLATFORM=MAC_OS tools/asc-publish.sh status` says READY_FOR_DISTRIBUTION and the public page (`…/id6799522972?platform=mac`) says Version <ver> (the id lookup is the iOS record only).
- [ ] Play: `tools/play.sh listing status` shows production vc <vc> completed (the rollout state, not the review verdict: the Console's Publishing overview has that, and the privacy-policy URL is read there too); the public page returns 200 and shows <ver> once approved.
- [ ] Snap: `curl -s -H 'Snap-Device-Series: 16' 'https://api.snapcraft.io/v2/snaps/info/megapdf?fields=version,revision'` shows stable and edge at <ver> / the promoted revision.
- [ ] Linux metainfo: every `https://electricrv.ca/…png` in the shipped metainfo returns 200 with an image content type (the loop in `docs/RELEASING.md` § 4).
- [ ] Support path: after any support-page change, one real message sent by a person from a real mail client through the live page's button reaches the `MegaPDF support` folder.
- [ ] Support links (#662): `asc-publish.sh version-info` (iOS and Mac) shows support `…/megapdf/support/` in every locale; `msstore.sh status` shows support `…/megapdf/support/`; `play.sh listing contact` says "(the support page)"; the Snap links call (`docs/RELEASING.md` § 4) shows contact `…/support/`.
- [ ] APT: `InRelease` and `Packages` at https://electricrv.ca/megapdf/apt/ name <ver>; a clean container installs it from the live page's commands.
- [ ] GitHub: `gh release view` for `linux-v<ver>`, `windows-cli-v<ver>`, `macos-cli-v<ver>` lists every asset with its `.sha256`.
- [ ] Last comment here: what went live where, ids and hashes.
- [ ] Milestone handed to Dave: he closes it and decides where anything left goes (milestones are his alone; an agent never closes, creates or moves one).
- [ ] Handoff memory written.
- [ ] Machines as found: WACK task deleted, nothing left installed or open on GPD-DAVE (no window or dialog either, after any rig run), `~/megapdf-work/<task>/` gone on kdocker2 and the Mac mini, worktrees pruned (`git cherry origin/main <branch>` before deleting), `D:\megapdf-qa\` emptied of this release's scratch.
