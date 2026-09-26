---
name: Release checklist
about: One issue per release. Work down it in order; every run id, submission id and hash goes in the comments. The runbook is docs/RELEASING.md.
title: "<version> release checklist"
labels: release
---

<!-- Replace <ver> (x.y.z), <ver4> (x.y.z.0) and <vc> (Android versionCode) once, then work down.
     docs/RELEASING.md has the order, the gates and the traps; the deep steps are linked from it.
     2.0 was #146, 2.1.1 was #395: one comment per finding, as it happens. -->

Dave's go-ahead: <quote and date>. Live before this release: Windows <>, iOS <>, Mac <>, Play <> (vc <>), Linux <>.

## What this release carries
- #… — one line each, the user-visible thing.

## 1. Version (`docs/RELEASING.md` § 1)
- [ ] `Version <ver> everywhere`, one commit, alone: both `.csproj`, `Package.appxmanifest` (`<ver4>`), `ios/project.yml`, `build.gradle.kts` (`versionCode` <vc>), `core/cli/megapdf_cli.cpp` `kVersion`, `tools/msstore_submit.py` `VERSION`, `Shoot-Set.ps1` default, metainfo `<release>` (dated today), `tools/linux/PACKAGE-REVISION` → `<ver> 1`, `website/megapdf/linux/index.html` — `grep -rn '<old version>' src ios android core tools website` finds only history.
- [ ] Milestone re-homed: everything shipping here is in it, nothing else is.

## 2. Gates, in order
- [ ] **CI green on the release commit** — `gh run list --commit <full sha> --json name,conclusion,url`; run id: ___. (An Android/iOS-only commit has no `ci.yml` run: name the run whose tree matches.)
- [ ] **Corpus batteries on kdocker2** — `tools/stress/structure-battery.sh … --reference --cli …`, `tools/stress/markdown-battery.sh …`, `MegaPDF.Stress run …` + `compare_runs.py` against the previous release; F1 ≥ 0.998, τ ≥ 0.9, 0 crashes / hangs / cmark failures / read-back failures; results in the gitignored `tests/Private/stress/release-<ver>-kdocker2/`, no corpus filename anywhere in the repo.
- [ ] **Windows, real machine** (GPD-DAVE) — `gh run download <run> -n MegaPDF-store-packages`; `check-native-arch.py` x64 + arm64, identity, resource map; `Remove-AppxPackage` then `Add-AppxPackage`; `artifacts/store/rc-<ver4>/SHA256SUMS`; click list: tabs, open from Explorer into the running window, fill / tick / sign / Save / reopen (read back with `pdftotext` / `qpdf`), Save As Markdown (PDF sha unchanged), About, fr-CA chrome, dirty close per tab.
- [ ] **Mac, real machine** (Mac mini) — `tools/build-macos-app.sh`, `--self-test`, then the app: same click list, Finder open, Info.plist version.
- [ ] **iOS, real simulator** (Mac mini) — `xcodebuild test`, then Files → share sheet → MegaPDF, Share (Save / Share without saving / Cancel), Export as Markdown, fr-CA, built Info.plist version.
- [ ] **Android, emulator** — Files → Open with, Share, Export as Markdown, fr-CA; `aapt dump badging` = <ver> / <vc>.
- [ ] **Linux, clean containers** — `tools/linux/check-apt-repo.sh <repo> <fixtures>` (five distributions, runs `megapdf-cli`), `package-check.sh`, tarball `install.sh` as non-root; `MegaPDF --install-kind`, `megapdf-cli --version`.
- [ ] **Show-stoppers**: none open — or each one's fix merged and the release commit moved to the merge: ___.
- [ ] **Screenshots re-shot** where chrome changed, en / fr-CA / fr-FR: Windows `Shoot-Set.ps1 -Version <ver4>` per language; iOS `tools/ios-screenshots.sh`; Mac `tools/macos-store-captures.sh`; Android `android-screenshots.yml` or the emulator; `tools/capture-gate/gate.py`. Resting only in `artifacts/store/captures-<ver>/{ios-screenshots,macos-screenshots,play/phone}/<lang>/` and `artifacts/store/screenshots/<lang>/`.
- [ ] **Every image read by eye** — no keyboard tip, grabber, real clock, cog; **no file picker, Explorer, Finder or recent list with personal files** in any public capture.
- [ ] **Copy** — `docs/release-notes/<ver>/` (four store files, long form, README with the delta table, the issue per line, counts, French review); `python3 docs/release-notes/<ver>/check_copy.py --check`; `python3 tools/gen_listing_copy.py`; limits MS 1500 / Apple 4000 / Play 500 / App Review notes 4000; Apple copy names no other platform and no ✕.
- [ ] **French** — every new app string and every new store block reviewed; fr-FR only by `python3 tools/gen_strings.py fr-fr` and `check_copy.py`; the review recorded in the README.
- [ ] **WACK** at <ver4> on the CI x64 package — `wack-prep.ps1`, the `MegaPDF WACK` scheduled task, Dave clicks Yes; PASS with only the two known optional FAILs; report renamed `wack-report-<ver4>-<date>.xml`; task deleted.
- [ ] **Docs** — README, TESTING, `tools/Linux-Packaging.md`, `tools/linux/megapdf-cli.1`; website: New in <x.y> above the previous block, gallery from this release's sets, footer source link, privacy date. Not deployed yet.

## 3. Submit (`docs/RELEASING.md` § 3) — each tag from that platform's final tree, tagged once
- [ ] **Windows** — `tools/msstore.sh build` → `signin` → `status` → `plan` → `submit` → `status`. Submission id: ___, publish Immediate.
- [ ] **iOS** — `git tag ios-v<ver> <sha> && git push origin ios-v<ver>` → `ios-release.yml` run ___ → build N = ___. Then `tools/asc-publish.sh version <ver>` → `copy` → `screenshots artifacts/store/captures-<ver>` → `review` → `assets-state` (loop to COMPLETE) → `build <N>` → `version-info` → `submit`.
- [ ] **Mac** — `gh workflow run macos-appstore.yml -f upload=true` → run ___ → build ___. Then the same `asc-publish.sh` sequence with `ASC_PLATFORM=MAC_OS`.
- [ ] **Play** — `git tag android-v<ver> <sha> && git push origin android-v<ver>` → `android-release.yml` run ___ → internal. Then `tools/play.sh listing status`; `push artifacts/store/captures-<ver> --production <vc> --phone-only` (validate) → the same with `--commit` → `readback …`. Edit id: ___.
- [ ] **Linux** — `git tag -a linux-v$(tools/linux/package-version.sh <ver>) <sha> -m "MegaPDF <ver> for Linux"`; run ___; `gh release edit linux-v<ver> --draft=false`; `gh run download <run> -n MegaPDF-apt-repository -D website/megapdf/apt`; `python3 website/deploy.py --dry-run --linux --privacy` → without `--dry-run`.
- [ ] **CLI archives** — `windows-cli-v<ver>` and `macos-cli-v<ver>` tags; download each draft's zip and **run it** (sha256, `--version`, an extract) on GPD-DAVE / the Mac mini; `gh release edit <tag> --draft=false`.
- [ ] **Snap** — skipped until #314.
- [ ] **Website** — after the go-lives: the 200 check, `python3 website/deploy.py --dry-run --privacy` → `--privacy` (`--linux` when APT is up).

## 4. Read back from the outside, then close
- [ ] Windows: `tools/msstore.sh status` and https://apps.microsoft.com/detail/9PF4TRRH4M76 show <ver4>.
- [ ] Apple: `curl -s 'https://itunes.apple.com/lookup?id=6799522972'` shows <ver> (iOS and Mac go together).
- [ ] Play: the store page returns 200 and shows <ver>; `tools/play.sh listing status` shows production vc <vc> completed.
- [ ] APT: `InRelease` and `Packages` at https://electricrv.ca/megapdf/apt/ name <ver>; a clean container installs it from the live page's commands.
- [ ] GitHub: `gh release view` for `linux-v<ver>`, `windows-cli-v<ver>`, `macos-cli-v<ver>` lists every asset with its `.sha256`.
- [ ] Last comment here: what went live where, ids and hashes.
- [ ] Milestone closed; anything left moved to the next one first.
- [ ] Handoff memory written.
- [ ] Machines as found: WACK task deleted, nothing left installed or open on GPD-DAVE, `~/megapdf-work/<task>/` gone on kdocker2 and the Mac mini, worktrees pruned (`git cherry origin/main <branch>` before deleting), `D:\megapdf-qa\` emptied of this release's scratch.
