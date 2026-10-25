# Releasing MegaPDF

The order of operations for a release, on every channel at once: the gates, the command
that proves each one, and what has bitten before. The deep steps live in the documents
this one links to; this is the one you open first. Written after 2.1.1 (#395, 2026-09-26),
the release that found four show-stoppers *after* CI was green, and revised after 2.4.0
(#817, 2026-10-25), whose gates found a permission 2.3.0 had shipped (§2.3), three
redaction defects (§2.2) and a Linux listing pointing at 404s (§3).

| For | Read |
|---|---|
| Microsoft Store package, WACK, the submission API | [`tools/Store-Submission.md`](../tools/Store-Submission.md) |
| App Store / Mac App Store listing, screenshots, review notes | [`docs/app-store-listing.md`](app-store-listing.md), [`docs/app-review-notes.md`](app-review-notes.md) |
| Google Play | [`android/RELEASING.md`](../android/RELEASING.md) |
| Linux: the .deb, tarball, APT repository, the day | [`tools/Linux-Packaging.md`](../tools/Linux-Packaging.md) § Going live |
| The website | [`website/README.md`](../website/README.md) § Launch runbook |
| The Mac mini (Mac and iOS builds, captures) | [`tools/mac-mini.md`](../tools/mac-mini.md) |
| Windows captures | [`tools/screenshots-windows/README.md`](../tools/screenshots-windows/README.md) |
| Copy and what each store's notes cover | `docs/release-notes/<x.y.z>/README.md` (2.1.1's is the model) |

Start by opening an issue from the **Release checklist** template
(`.github/ISSUE_TEMPLATE/release-checklist.md`) and work down it. 2.0 was #146, 2.1.1 was
#395, 2.4.0 was #817; every finding, run number, submission id and hash goes in that issue's
comments as it happens, because the consoles are reachable from nowhere else in this repo.

Keep one line per platform at the top of the issue — tag, full SHA, workflow run — filled in
as each is cut (§3, the tag rule): 2.4.0's platforms were not all built from one commit.
**A decision that is Dave's** (a defect that is already live, a promotion, wording) goes to
him one at a time, each with a recommendation, and his answer is recorded as a timestamped
comment on the issue the moment he gives it (2.4.0's two are the Android permissions and the
privacy policy, both on #817).

## 0. Always ready

Run `tools/release-readiness.sh` and clear what it lists before starting §1. It prints one
line per check, `OK` or `ATTENTION`, with the evidence, and exits non-zero if anything needs
attention: `main` CI, open PRs, stale or orphaned `wip/*` branches and worktrees, the state
of the checkouts, the twelve version locations of §1's table (parsed from that table, so it
cannot drift from it), the docs that name the shipped version, the open issues in the
matching milestone (printed, never a failure: milestones are Dave's), the coverage map and
the Mac mini CI runner (switched on, picking up jobs).
`--machines` adds the leftovers check on the Mac mini, kdocker2, kdocker3 and GPD-DAVE (§4's
"machines left as found"); `--stores` adds §4's read-backs. `.github/workflows/readiness.yml`
runs the plain check weekly and keeps one "Release readiness" issue in step with it. The aim
is that §1 starts from a repo with nothing on that list. After the version bump two lines
read `ATTENTION` on purpose until go-live: the README's "Store status as of" and the support
page's "The current release is" (§2.7's sweep clears both).

## 1. What a release is

**A release is the checklist issue closed, not a tag pushed.** The version tags are how
each channel is *built*; the release is done when every channel has been read back from
the outside and the checklist issue says so. The milestone is Dave's: it stays the
working milestone until he opens the next one, and nothing is moved out of it or into a
"next" milestone to make a release look finished (2026-09-26: a morning spent re-homing
issues into a 2.2 that had not been started, then moving them all back).

**One version everywhere, and it must be higher than what every store holds.** Each
store refuses a version at or below its live one, so if one store is a version ahead
(2.0.1 on Play after a hotfix) the next release is above *that* on every platform.
Windows is four-part with revision 0 (`x.y.z.0`); Android's `versionCode` goes up by one
per upload, whatever the name does.

**Where a version lives.** The app reports it from six places; the release tooling reads
it from five more; a twelfth, the Linux man page, ships to users but sits outside both
groups and outside the verification grep below, so nothing but someone remembering
checks it.
All twelve change in **one commit, alone**, titled `Version x.y.z
everywhere` — the built artifacts are then checked against it in §2.3, and a bump mixed
into a feature commit is what makes that check inconclusive.

| | File | Field |
|---|---|---|
| Windows | `src/MegaPDF.App/MegaPDF.App.csproj` | `<Version>x.y.z</Version>` |
| Windows | `src/MegaPDF.App/Package.appxmanifest` | `<Identity … Version="x.y.z.0">` |
| Mac, Linux | `src/MegaPDF.Avalonia/MegaPDF.Avalonia.csproj` | `<Version>x.y.z</Version>` |
| iOS | `ios/project.yml` | `MARKETING_VERSION: x.y.z` |
| Android | `android/app/build.gradle.kts` | `versionName "x.y.z"`, `versionCode` +1 |
| CLI | `core/cli/megapdf_cli.cpp` | `kVersion = "megapdf-cli x.y.z"` |
| Windows submission | `tools/msstore_submit.py` | `VERSION = "x.y.z.0"` (its `rsplit` picks the notes folder `x.y/`) |
| Windows captures | `tools/screenshots-windows/Shoot-Set.ps1` | `$Version = 'x.y.z.0'` default (it refuses any other installed package) |
| Linux metainfo | `tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml` | a new `<release version="x.y.z" date=…>` first; dated within 30 days of the tag or the tarball build refuses |
| Linux packages | `tools/linux/PACKAGE-REVISION` | reset to `x.y.z 1` |
| Linux page | `website/megapdf/linux/index.html` | every download link and `.deb` name; `deploy.py --linux` refuses if it disagrees with the repository |
| CLI man page | `tools/linux/megapdf-cli.1` | the `.TH` line's `"MegaPDF x.y.z"` and its date — it ships inside the .deb and the tarball. Listed here only from 2.2.1; 2.1.1 and 2.2.0 both got it right (6a27aee) without the table asking, which is the kind of luck this row exists to replace |
| Linux man page | `tools/linux/megapdf-cli.1` | the `.TH` line: `.TH MEGAPDF-CLI 1 "<date>" "MegaPDF x.y.z" "User Commands"` — ships in the .deb and tarball |

`grep -rn '<old version>' --include='*.csproj' --include='*.yml' --include='*.kts' --include='*.cpp' --include='*.py' --include='*.ps1' --include='*.xml' --include='*.html' --include='*.1' src ios android core tools website` after the bump must find only history (release notes, metainfo's older entries).

`*.1` was missing from that list until 2.2.0: the man page's `.TH` line was left at the
old version, unnoticed by the grep, and failed three Linux CI jobs on the bump PR within
90 seconds of each other — Linux app, Snap build, and the tarball/.deb/APT/Flathub job —
each with "`.TH` line does not say MegaPDF 2.2.0". §2.7 used to list the man page only
as a doc to update, which looked covered and was not; it is a version-bump item (above),
not a docs edit.

Two more places name a version in prose, not as a parseable string, so the bump commit
must leave them alone — they need authoring, not a mechanical swap, when that release's
copy is written (§2.5/§3), not when the table above is bumped: `tools/msstore_submit.py`'s
`CERT_NOTES`, read by a human certification tester, which can go on describing an older
release and claiming to replace the wrong packages (fixed for 2.2.0 in #647); and
`website/megapdf/support/index.html`'s "The current release is **x.y.z** everywhere" line
(§2.7, website).

## 2. Gates, in order

Each gate has the command that proves it and what "done" looks like. A show-stopper at
any gate stops the release; its fix is a PR to `main`, and **the release commit moves to
the merge** — packages and tags are cut from the final tree, never from the commit that
was being verified (§3, the tag rule). A defect found here that is **already in the live
release** is not automatically a show-stopper: put it to Dave with a recommendation and
record his ruling on the checklist issue (2.4.0: the Android rotation bug #826 shipped
unfixed by his decision; the permissions, #821, did not).

### 2.1 CI green on the release commit

```
gh run list --commit <full sha> --json name,conclusion,url
```

`qa-matrix` is among them and is the cheapest thing on this list to read: its log ends with
the gap list from `docs/qa/test-matrix.md`, which is what nothing in CI is looking at on this
commit. It is not a gate on its own — a gap is a known gap, not a red — but a gap that has
grown since the last release is worth a look before submitting.

Done: every workflow that ran on that commit is `success`. **`ci.yml` has `paths-ignore`
for `android/**`, `ios/**` and the Android PDFium**, so an Android- or iOS-only final
commit has no CI run and no `MegaPDF-store-packages` artifact of its own; the Windows
packages then come from the last run whose tree matches (`git diff --stat <that sha>
<release sha> -- . ':!android' ':!ios'` must be empty). Record the run id in the issue.

A job that a version bump cannot have broken and that passes on a re-run of the failed job
is still recorded: file the flake as an issue rather than letting the retry hide it
(2.4.0: Android `ShrinkForEmailTest`, #820).

### 2.2 Corpus batteries, on kdocker3

The corpora are on **kdocker3** — `~/pdf-test` (4,337 PDFs; `/mnt/pdf-test` here),
`~/pdf-public`, `~/pdf-test-ca`, `~/pdf-test-un` and `~/pdf-test-vendor` — not kdocker2,
which has no corpus-battery role; two agents hit this during 2.2.0 and both ran on kdocker3
instead, documenting the deviation. Run all five (vendor is reported, never committed).
Build the release commit **and the previous release's commit** the same way in a throwaway
container (2.4.0: `git archive` of each into `megapdf:dotnet`), so every comparison is the
same machine and the same PDFium. Results go to the **gitignored**
`tests/Private/stress/release-<x.y.z>-kdocker3/`; no corpus filename is ever written into
the repo, an issue or a commit message.

```
tools/stress/structure-battery.sh <megapdf_structure_check> ~/pdf-test <out> --reference --cli <megapdf-cli>
tools/stress/markdown-battery.sh <megapdf-cli> <cmark> ~/pdf-test <out>
tools/stress/pages-battery.sh <megapdf-cli> ~/pdf-test <out> --jobs 4
tools/stress/redaction-battery.sh <megapdf_leakcheck> ~/pdf-test <out>   # leakcheck is built with the core tests
MegaPDF.Stress run --root ~/pdf-test --out <out>/edits --phases edits   # the editing harness, tools/stress/README.md
python3 tools/stress/compare_runs.py <previous release's run> <out>/edits
```

Done: F1 ≥ 0.998, order τ ≥ 0.9, 0 crashes, 0 hangs, 0 cmark failures, 0 read-back
failures; pages 0 qpdf failures, count mismatches and write failures; redaction 0 leaked,
0 changed outside the marked areas, 0 hung (the script's exit code, and `summary-redact.txt`);
and `compare_runs.py` against the previous release's run explains every change. (2.1.1 at
`029c849`: F1 0.998401, τ 0.934, 0/0, and 0 changes in 22,267 edits.) Redaction was not on
this list before 2.4.0 and TESTING.md holds no earlier full-corpus redaction record, so
2.4.0's run is the first baseline.

**A line that does not read zero is "present in the previous release too" only if the
previous release's build, built and run the same way on the same machine, prints the same
line.** 2.4.0 rebuilt 2.3.0 (`80dfb5d`) and got identical per-document redaction lines, which
is how the four below were known to predate it; there was no earlier record to lean on.
Whether a pre-existing line blocks is Dave's call: record it and his ruling on the issue.

Lines known not to read zero at 2.4.0 (delete a row when its issue closes, and re-run
rather than trust this list):

| battery | line | issue |
|---|---|---|
| structure | public corpus F1 0.981815 | #498 |
| edits | 6 `delete_word` read-back failures on two copies of one vertical-writing fixture | #824 |
| redaction | 9 documents leave the redacted word in an `/XFA` packet | #822 |
| redaction | 1 hang: an outline whose entries name each other as `/Next` | #823 |
| redaction | 43 private documents change outside the marked area | #825 |
| pages | 9 "REFUSED, other" on the public corpus | #445 |
| all batteries | the `*.pdf` walk skips the 179 upper-case `*.PDF` files of the private corpus (the edit harness sees them) | #825 |

The time is a gate too (§5, #704): compare per-document times against the previous release's
run, not only the scores.

### 2.3 Real-machine verification, per platform

**This is the gate.** CI proves the code compiles and the engine passes its tests; it
cannot click. Every 2.1.1 show-stopper was found here, after CI was green: #401 (a
Windows click did nothing), #412 (the desktops opened to a dead toolbar), #409 (the
Android picker could not write Markdown), #397 (the CLI would not load on Debian 12).
And 2.3.0's two (#700): the Windows toolbar dead on a document opened from Explorer, and
every document opened after a tab was closed drawing nothing. Both had shipped in 2.2.0,
whose release did not run this gate.

What to do on each platform, with the release build installed the way a user gets it:
open two documents (tabs, one window); open a third from outside (Explorer / Finder /
file manager / Files / Open with) and see it land in the running window; **Share** on the
phones, with unsaved changes (Save / Share without saving / Cancel); **Save As →
Markdown** on the desktops and **Export as Markdown** on the phones, then confirm the PDF's
sha256 is unchanged; fill a line, tick a box, place a signature, Save, reopen and read the
result back **outside MegaPDF** (`pdftotext`, `qpdf --check`); **About** says the version;
switch to **fr-CA** and read the chrome; close dirty and get the prompt, per tab.

On Windows four parts of this *are* automated. They now run in CI too — the
`windows-ui-selftest` job on `windows-latest`, non-blocking until it has held green for a
few weeks (#462) — so what is below is the by-hand gate for a release, and the belt to that
job's braces. From a built tree on GPD-DAVE,
`MegaPDF.exe --screenshot <out.png> --screenshot-state reading <fixture.pdf>` drives
reading mode in a real window — Ctrl+H's command, the Escape ladder, the tab order,
the floating bar's screen-reader rule, the page colours and the shared settings
(#504/#510). `MegaPDF.exe --screenshot <out.png> --screenshot-state zoom-anchor --window
900x700 <fixture.pdf>` drives a menu/keyboard zoom, a clamped zoom and a Ctrl+wheel notch
and asserts the point each one was anchored on has not moved (#528).
`MegaPDF.exe --screenshot <out.png> --screenshot-state close-tabs <fixture.pdf>` drives
closing a tab, replacing a tab's document, and the window closing with tabs still open,
and asserts each document was actually disposed — not just that its tab left the strip
(#543). `MegaPDF.exe --screenshot <out.png> --screenshot-state pages <fixture.pdf>` drives
the page tools: the Pages pane (F4), rotate, delete, reorder, insert, combine and extract,
every undo, the renumbering the app owes core contract 10, the two refusals the engine makes
on purpose, and the recovery journal's replay of all of it — 107 checks, and it prints its
own passed/failed tally on the last line (#174). It builds the two form fixtures it needs
itself, so `fixture.pdf` is all it wants from the fixtures directory. All four print a
PASS/FAIL line per check and **the exit code is the test**. Run
them before the package checks below; `reading` puts the settings it writes back as it
found them, and `pages` closes every tab it opened and deletes its own scratch directory. `reading`'s own multi-tab check opens a second tab on a fixture named
`demo.pdf` beside the one passed on the command line — passing `demo.pdf` itself as
`<fixture.pdf>` makes that check activate the already-open tab instead of opening a
second one and fails for that reason, not a real regression; use `fixture.pdf` (or any
other name) for the primary argument.

| Platform | Where | Build under test | Version read-back |
|---|---|---|---|
| Windows | GPD-DAVE | `gh run download <run> -n MegaPDF-store-packages` → `python3 tools/check-native-arch.py x64/arm64 <msix>`, identity and resource-map checks (`tools/Store-Submission.md` § Verify) → `tools/windows-qa/qa-safety.ps1`: `Start-QaUserDataWatch`, then `Install-QaTestPackage <x64 msix>` (never a bare `Remove-AppxPackage`: it would take a Store install's data with it, #717) | About; `Get-AppxPackage ElectricRV.MegaPDF` |
| Mac | Mac mini | `tools/build-macos-app.sh osx-arm64 ~/app-macos`, `--self-test`, then the real app (`tools/mac-mini.md` § Recipes) | `Contents/Info.plist` CFBundleShortVersionString |
| iOS | Mac mini | `xcodebuild test` then a simulator run of the app; Files → share sheet → MegaPDF | built `Info.plist` |
| Android | the Pixel 4 XL over adb (2.4.0) or an emulator (Pixel 6 / API 33) | the release commit's debug APK (`megapdf-debug-apk` of `android-ci.yml`; the signed build exists only once tagged, so the manifest is checked again below); Files → Open with | `aapt dump badging` |
| Linux | clean containers | `tools/linux/check-apt-repo.sh <repo> <fixtures>` (installs on Debian 12/13, Ubuntu 22.04/24.04/26.04 and **runs `megapdf-cli`** in each); `package-check.sh`; the tarball's `install.sh` as non-root | `MegaPDF --install-kind`, `megapdf-cli --version` |

**Android: read the merged manifest, not ours (#821).** 2.3.0 shipped asking for `INTERNET`
and `ACCESS_NETWORK_STATE`, merged in from ML Kit's library (#676) while our own manifest
declared none, and every store text said the app asks for no permissions and makes no network
connections. On a Pixel, one OCR Find queued ML Kit usage events and POSTed them to
`firebaselogging.googleapis.com` (HTTP 200). So, every release:

- `./gradlew :app:checkNoPermissions` passes. It reads the merged debug and release manifests
  and fails on any `uses-permission` except androidx.core's signature-level
  `DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`; `assembleRelease`/`bundleRelease` depend on it and
  `android-ci.yml` runs it, so a library bump that adds one fails there. Strip an unwanted one
  with `tools:node="remove"` in the app manifest; adding to the allow-list instead is a change
  to the store copy, not a build fix.
- On the Pixel: `adb shell dumpsys package ca.electricrv.megapdf` lists neither permission;
  the app process's `/proc/<pid>/status` `Groups:` has no `3003` (inet: a process outside it
  cannot open a socket); OCR Find on an image-only PDF still finds the word (the scan
  highlight and the "Read from a scanned page" notice).
- The signed bundle, once `android-release.yml` has built it (§3): download `app-release.aab`
  and read `base/manifest/AndroidManifest.xml` — no `INTERNET`, no `ACCESS_NETWORK_STATE`
  (2.4.0 searched the protobuf manifest's strings; that machine had no `aapt2`).
- Every "no permissions" and "no network" claim is re-read against that manifest before
  submitting: `android/RELEASING.md` (Data safety), the generated `docs/app-store-listing.md`,
  the website and the privacy policy.

Done: one comment per platform on the checklist issue, "no show-stopper", naming the
commit and what was clicked — or the issue number of the show-stopper and the hold.

### 2.4 Screenshots

Re-shoot **every platform** in en, fr-CA and fr-FR whenever the chrome changed (2.1.1:
the tab strip and the phones' menus), and any single slot whose pose changed. The Windows
set needs the Store-mode package installed on GPD-DAVE at the version being shot; the Mac
set needs the Mac mini's display; iOS is shot on the Mac mini too (the CI run's
artifact was unlistable at 2.1.1, #406); Android from `android-screenshots.yml` or the
emulator.

```
python3 tools/screenshots-windows/gen_store_docs.py <repo>\artifacts\store\screenshots\<lang> --lang <lang>
.\Shoot-Set.ps1 -Version x.y.z.0 -Lang fr-CA -Dir fr-CA          # GPD-DAVE, one language at a time
tools/ios-screenshots.sh <lang> <out>                             # Mac mini
tools/macos-store-captures.sh <lang> <out>                        # Mac mini
python3 tools/capture-gate/gate.py --store microsoft artifacts/store/screenshots
```

Where they rest, and nowhere else (Dave, 2026-09-26): `artifacts/store/captures-<x.y.z>/`
with `ios-screenshots/<lang>/`, `macos-screenshots/<lang>/`, `play/phone/<lang>/`, and
Windows in `artifacts/store/screenshots/<lang>/` — the layout `asc_publish.py` and
`play_listing.py` read. Never a home folder, a Desktop or a scratchpad; a rig may write
into its own machine's `artifacts/`, then the set is copied here and the remote copy
removed. The folder is named by the checklist's commands: 2.4 staged `captures-2.4`,
matching `docs/qa/captures-2.4`, not `captures-2.4.0` — the tools take it as an argument,
so any name works, but every command on the issue must use the one that exists.

**Every image is read by eye.** The gate compares images with each other, so it cannot
see a constant: a keyboard tip, a resize grabber, a real clock, a debug cog, a file
picker. **No public capture may show a file picker, Explorer, the Finder or a recent
list with personal files** (Dave, 2026-09-26; on GPD-DAVE the Open dialog starts in
OneDrive). Test evidence may; a Store set may not.

### 2.5 Copy

`docs/release-notes/<x.y.z>/` holds the four store files, the long form and a README
that records what each store's notes are the delta from, the issue behind every line,
the counts and the French review. The tools read `docs/release-notes/<x.y>/`
(`msstore_submit.py` from `VERSION.rsplit`, `asc_publish.py` from `ASC_NOTES_VERSION`), so:

```
python3 docs/release-notes/<x.y.z>/check_copy.py          # derive fr-FR, recount, sync ../<x.y>/
python3 docs/release-notes/<x.y.z>/check_copy.py --check  # fail on drift — run before every submit
python3 tools/gen_listing_copy.py                          # listing docs + android/RELEASING.md
python3 tools/gen_strings.py fr-fr es-es                  # the app's fr-FR and es-ES catalogues; never hand-edit a derived one
```

Limits, counted as the field counts: Microsoft **1500**, App Store and Mac App Store
**4000**, Play **500**, App Review notes **4000** (2.1.1's had grown to 4573 and the
submit refused). Apple copy names **no other platform** (2.3.10) and contains **no ✕**
(App Store Connect refuses the character, #340 — write ×, or name no glyph). Nothing
named that the app on *that* store does not have (the CLI is in no store package). The
French review — Fable at Dave's instruction, standing in for #343's francophone — is
recorded in the README with every change and every deliberate non-change.

### 2.6 WACK (Windows)

On a test-signed copy of the **CI** x64 package, never a local rebuild
(`tools/Store-Submission.md` § Certification prep). Get that package into place first —
download it from the release commit's CI run, and write `SHA256SUMS` naming the run and
commit, the same as §3's rule for the submission packages:

```
gh run download <run> -n MegaPDF-store-packages -D artifacts/store/rc-<x.y.z.0>
```

`tools/windows-qa/wack-prep.ps1` takes `-Msix` and defaults to the newest
`artifacts/store/rc-<x.y.z.0>/*_x64.msix`, so it needs no local build directory. (2.2.0:
it instead carried a hardcoded path to a local `2.0.0.0` build directory, contradicting
the never-a-local-rebuild rule above — fixed by the `-Msix` default.) Move the previous
report aside first: `appcert` refuses to overwrite one and exits `-1` *after* the elevated
round-trip.

```
# GPD-DAVE: tools/windows-qa/wack-prep.ps1 [-Msix <path>], then
Register-ScheduledTask -TaskName 'MegaPDF WACK' … -LogonType Interactive -RunLevel Limited   # action: tools/windows-qa/wack-launch.ps1
Start-ScheduledTask -TaskName 'MegaPDF WACK'      # only when Dave is at the console to click Yes
```

**Dave has to be at the console** to click Yes on the UAC prompt, so tell him the time you
will start the task and wait for his word; the run takes about three minutes (2.4.0: 19:52 to
19:55 EDT, `appcert test exit: 0`, `LastTaskResult 0`).

Done: `artifacts/store/wack-done.marker` exists, the report says PASS with only the two
known optional FAILs (#58 "General metadata correctness" and #88 "Blocked executables", both
marked OPTIONAL), and it is renamed `artifacts/store/wack-report-<x.y.z.0>-<date>.xml`. Keep
that report and `wack-run-<date>.log`, and quote `OVERALL_RESULT` and `APP_VERSION` on the
issue. Delete the scheduled task afterwards.

### 2.7 Docs

`README.md`, `TESTING.md`, `tools/Linux-Packaging.md`, and the website — **not**
`tools/linux/megapdf-cli.1`, whose `.TH` version line is §1's version-bump table, not a
docs edit (it fails Linux CI, not a doc review): since #652, this release's
still-true features fold into the highlighted-features section (organised by what the
app does, not by release — nothing added there expires when the next version ships), the
gallery from this release's sets (`docs/release-notes/<x.y.z>/website-renders/`), the
source link in the footer, and the privacy policy's effective date if it changed. The site
is **not deployed** until the go-lives (§3, website).

**The hand-written sweep.** These name the release in prose or in a claim and §1's grep
cannot see any of them (2.4.0's landing page still said "New in 2.3" after its deploy, #830):

- `website/megapdf/index.html`: the **New in x.y** section — a single current block with no
  stack behind it, so replace the previous one — and the "Private by design" card's list of
  languages ("English, French and Spanish", #830).
- `website/megapdf/support/index.html`: "The current release is **x.y.z** everywhere" and the
  version placeholder in the form. Word the sentence for what is live at that moment, and
  re-read the stores immediately before deploying: iOS went live in the middle of 2.4.0's
  go-live deploy, so the page said "arriving on iPhone and iPad" for about twenty minutes
  (#829). Run `website/check-support-page.js` after any change to the page (`npm install
  jsdom` once, `node website/check-support-page.js`).
- `README.md`: "Store status as of x.y.z on <date>", one line per platform as read back (§4).
- `website/README.md`: the support-mailbox and support-system lines, if either changed.
- `docs/release-notes/<x.y.z>/*`: the "Draft … not yet approved" headers once Dave approves
  and the stores are live, and any "not yet reviewed" sentence once the reviewer has approved.
  Never touch a translated line or its `FR-REVIEW`/`ES-REVIEW` marker for this.

## 3. Submission, per channel

All through the committed credential wrappers, from the repo root. Each wrapper loads
its credentials from a file the command line never names, which is why they exist: the
session's secrets hook blocks any command that names the file, its folder or a token.
Use them as written; never a scratch script that does the same.

**The tag rule.** Each platform is built from its own tag (`ios-v`, `android-v`,
`linux-v`, `windows-cli-v`, `macos-cli-v`), cut from **that platform's final tree** — the
merge of its last show-stopper fix, not the commit that was under test. A moved tag
re-runs the workflow and makes a **new build number** (iOS build 33 replaced 32 after
ITMS-90788), so tag once, after the gates, and record the run id in the issue.

Platforms may be cut from different commits: 2.4.0's Android was tagged at `14f7b505`
(#821 moved its tree) after iOS, Mac, Linux and the CLIs were tagged at `72268e9f`. That is
fine when the diff between the two commits (`git diff --stat <tag's sha> <new sha>`) touches
nothing the already-tagged platforms ship — read it, and say so on the issue — and it is why
the per-platform SHA line at the top of the issue exists. Verification done on the earlier
commit still stands for the others, not for the platform whose tree moved.

**Windows** — `tools/Store-Submission.md` § Submitting through the API. Both packages in
`artifacts/store/rc-<x.y.z.0>/` with `SHA256SUMS` naming the CI run.

```
tools/msstore.sh build      # payload.json + upload.zip, checks every limit, no network
tools/msstore.sh signin     # prints only "token OK"
tools/msstore.sh status     # refuses to submit while another submission is pending
tools/msstore.sh plan       # the published submission, before and after; creates nothing
tools/msstore.sh submit     # clone, replace packages and screenshots, upload, commit, poll
tools/msstore.sh status     # read it back: id, status Certification, publish Immediate
```

`build` reads `artifacts/store/rc-<x.y.z.0>/SHA256SUMS` first; without it, it ends in a
traceback, not a message. 2.1.1.0 was submission `1152921505701982919`. Certification takes
hours; the listing goes public on its own (publish mode Immediate).

**iOS** — `docs/app-store-listing.md`. Wait for `ios-release.yml` to finish and App Store
Connect to process the build before `build`.

```
git tag ios-v<x.y.z> <sha> && git push origin ios-v<x.y.z>     # ios-release.yml → TestFlight build N
tools/asc-publish.sh version <x.y.z>
tools/asc-publish.sh copy
tools/asc-publish.sh screenshots artifacts/store/captures-<x.y.z>
tools/asc-publish.sh review
tools/asc-publish.sh assets-state        # loop until every screenshot is COMPLETE, none FAILED; previews carry over (6 iOS, 3 Mac)
tools/asc-publish.sh build <N>
tools/asc-publish.sh version-info        # the record as Apple holds it: version, build, state
tools/asc-publish.sh submit
```

**Mac** — the same record, a second platform: build with
`gh workflow run macos-appstore.yml -f upload=true --ref ios-v<x.y.z>` (the tag, so it
builds the release tree and the run's `headSha` says so; without `--ref` it builds `main`),
then the sequence above with `ASC_PLATFORM=MAC_OS` in front of each `tools/asc-publish.sh`
call (the captures come from `macos-screenshots/<lang>/` in the same folder). The Mac build
number is the version string (`2.4.0`), so `build` takes that, not an integer.

**Google Play** — `android/RELEASING.md`. The tag puts the bundle on **internal**;
production is a separate, deliberate step, and `push` without `--commit` validates only.

```
git tag android-v<x.y.z> <sha> && git push origin android-v<x.y.z>   # android-release.yml → internal
tools/play.sh listing status
tools/play.sh listing push artifacts/store/captures-<x.y.z> --production <versionCode> --phone-only
tools/play.sh listing push artifacts/store/captures-<x.y.z> --production <versionCode> --phone-only --commit
tools/play.sh listing readback artifacts/store/captures-<x.y.z> --production <versionCode> --phone-only
tools/play.sh listing contact                       # says "(the support page)"; the first time add --website https://electricrv.ca/megapdf/support/ --commit (#662)
```

`contact --commit` prints a harmless 400 afterwards (it deletes an already-committed edit),
and the phone reads "the 888 555 placeholder" by Dave's instruction (#417). **A new locale
has no 10-inch tablet set**: `--phone-only` pushes none and leaves the existing ones, so
decide before the release whether a locale without one is acceptable (es-419 and es-ES
shipped without in 2.4.0). Run the tools from a tree at `origin/main` (§5, the first trap):
2.4.0's Play push used a fresh worktree because the shared checkout was behind.

**Linux** — `tools/Linux-Packaging.md` § The day. Not a store: the tag build makes a
*draft* release; publishing it is the go-live. Check `git log --oneline -1 HEAD`
against `origin/main` before any `deploy.py` call below — a checkout even a few commits
behind can pass its own `--dry-run` while still being behind the live site, and a real
deploy from it then regresses the live site rather than advancing it.

The dry run comes **before** `gh release edit --draft=false`. Publishing cannot be taken
back, and the rehearsal exists to fail while failing is free (#668).

**The metainfo's screenshot URLs must answer before the `.deb` is public.** The metainfo
inside the package points at `https://electricrv.ca/megapdf/screenshots/linux/<locale>/…`;
for a **new locale** those images must already be in `website/megapdf/screenshots/linux/` and
go up with the release's `deploy.py`, or the shipped package points at 404s and cannot be
fixed without a new one. 2.4.0's es-419 and es-ES were missing (14 of the 35 URLs, #724).
The read-back is in §4.

```
git tag -a linux-v$(tools/linux/package-version.sh <x.y.z>) <sha> -m "MegaPDF <x.y.z> for Linux" && git push origin linux-v<x.y.z>
gh run download <run> -n MegaPDF-apt-repository -D <scratch>/apt   # NOT straight into website/megapdf/apt: the artifact also carries
                                                                     # the tracked megapdf.asc/.gpg/.sources and a direct download collides on them
diff <scratch>/apt/megapdf.asc website/megapdf/apt/megapdf.asc     # confirm the signing key is unchanged (and the other tracked key files), then
cp -r <scratch>/apt/dists <scratch>/apt/pool website/megapdf/apt/  # only dists/ and pool/ — both gitignored; the tracked key files are untouched
python3 website/deploy.py --dry-run --linux --snap --support --privacy   # read the last four lines: "Login file ... - found" must be one of them; then, and only then, publish
gh release edit linux-v<x.y.z> --draft=false
python3 website/deploy.py --linux --snap --support --privacy
```

**CLI archives** — a draft release per tag; **run the published files** before publishing
(2.1.1: `windows-cli-v2.1.1` on GPD-DAVE, `macos-cli-v2.1.1` on the Mac mini — sha256
matched, `megapdf-cli --version`, a text and a Markdown extract exited 0).

```
git tag windows-cli-v<x.y.z> <sha> && git tag macos-cli-v<x.y.z> <sha> && git push origin windows-cli-v<x.y.z> macos-cli-v<x.y.z>
gh release download windows-cli-v<x.y.z> -D <scratch>      # then run megapdf-cli.exe --version / extract on Windows
gh release edit windows-cli-v<x.y.z> --draft=false; gh release edit macos-cli-v<x.y.z> --draft=false
```

**Snap** — `tools/Linux-Packaging.md` § The Snap Store. The upload goes to **edge**;
promotion to stable is a second, deliberate run of the same workflow (#654, since the stored
login carries `package_release` on stable too), and the revision is **required and never
inferred** — promoting the wrong one publishes the wrong build to everyone on stable. Run
both **`--ref linux-v<x.y.z>`**, so the snap is built from the release tree rather than from
whatever `main` is by then (2.4.0 did; 2.3.0 ran from `main`). Install the edge build and run it
before promoting (2.3.0, in WSL: `sudo snap install megapdf --edge`, `megapdf.cli --version`,
an extract, then remove it), and put the promotion to Dave first: it is public.

```
gh workflow run snap.yml -f upload=true --ref linux-v<x.y.z>                            # ~6 min, edge only
sudo snap install megapdf --edge                                                         # verify edge
gh workflow run snap.yml -f promote=true -f promote_revision=<n> --ref linux-v<x.y.z>    # the exact revision, read off the Store or the upload's output
```

The promote run's log says nothing useful; the evidence is the channel map (§4).

**Website** — after the go-lives, never before (`website/README.md` § Launch runbook).
**A deploy without a flag leaves that flag's part out**: without `--linux` it takes `linux/`
and `apt/` off the server and resolves every page back to "coming soon"; without `--support`
the support page stays off and no page links it; without `--snap` the Snap section is hidden.
Every release deploy is therefore **`--linux --snap --support --privacy`** (`--snap` since
the Snap reached stable with 2.3.0, `--support` since the page shipped with 2.4.0; `--support`
still refuses without `--privacy`, and `--snap` without `--linux`). The Snap
section stays up through a new edge upload, because stable still serves the previous
revision. Deploy again after any later website change, with the same four
flags (2.4.0 deployed four times), each from a fresh worktree at `origin/main`, dry run
first.

```
python3 website/deploy.py --dry-run --linux --snap --support --privacy    # its "Login file ... - found" line must be there
python3 website/deploy.py --linux --snap --support --privacy
```

## 4. Read-backs and close-out

Each channel is read from the outside, as a user reaches it, and the result goes in the
issue:

```
tools/msstore.sh status                                                  # and https://apps.microsoft.com/detail/9PF4TRRH4M76
for c in us ca fr gb; do curl -s "https://itunes.apple.com/lookup?id=6799522972&country=$c&_=$(date +%s)" | python3 -c 'import json,sys; r=json.load(sys.stdin)["results"][0]; print(r["version"], r["currentVersionReleaseDate"])'; done
curl -sL -A 'Mozilla/5.0' 'https://apps.apple.com/ca/app/id6799522972?platform=mac' | grep -o 'Version [0-9.]*' | head -1   # the Mac record
curl -s -o /dev/null -w '%{http_code}\n' 'https://play.google.com/store/apps/details?id=ca.electricrv.megapdf'   # 404 until public; tools/play.sh listing status for the track
curl -s https://electricrv.ca/megapdf/apt/dists/stable/InRelease | head -5
curl -s https://electricrv.ca/megapdf/apt/dists/stable/main/binary-amd64/Packages | grep -A1 '^Package: megapdf'
curl -s -H 'Snap-Device-Series: 16' 'https://api.snapcraft.io/v2/snaps/info/megapdf?fields=version,revision,private,publisher'
curl -s -H 'Snap-Device-Series: 16' 'https://api.snapcraft.io/v2/snaps/info/megapdf?fields=links'   # contact and issues are the support page (#662)
gh release view linux-v<x.y.z> --json assets -q '.assets[].name'      # and the two CLI tags
grep -o 'https://electricrv.ca/[^<"]*\.png' tools/linux/flatpak/ca.electricrv.MegaPDF.metainfo.xml | sort -u | while read -r u; do curl -s -o /dev/null -w "%{http_code} %{content_type} $u\n" "$u"; done   # every line: 200 image/png
```

How to read each store, because the obvious read is often wrong:

- **Windows.** The public Store page shows no version; `tools/msstore.sh status` is the
  evidence (Published, the version, no pending submission).
- **Apple.** The iTunes lookup is cached per storefront: add a throwaway query parameter
  and look in more than one country (2.4.0's `ca` answered a stale 2.3.0 for minutes after
  `us`, `fr`, `gb`, `mx` and `es` had 2.4.0). The id lookup returns the **iOS** record only; the
  Mac app is read from `ASC_PLATFORM=MAC_OS tools/asc-publish.sh status` (READY_FOR_DISTRIBUTION)
  and its public page (`?platform=mac`).
- **Play.** `tools/play.sh listing status` says `completed`, which is the rollout state and
  not the review verdict; the verdict is only in the Console's Publishing overview. The public
  page keeps serving the old version until approval, and its embedded data names the new one
  after it. The privacy-policy URL is a Console-only field the API does not expose: read it there.
- **Snap.** The Store API call above (`Snap-Device-Series: 16`) lists the channel map; after a
  promotion `stable` and `edge` both show the revision. The promote run's log shows nothing.
- **Support path, after any change to the page.** `node website/check-support-page.js`, then
  one real message sent by a person from a real mail client through the live page's button,
  landing in Dave's `MegaPDF support` folder with a `MegaPDF support MP-…` subject
  (`info@electricrv.ca` is an alias that forwards to his mailbox). Not a script's send.
- **The release-notes and README claims** are written from these reads, at the moment of
  writing (§2.7's sweep).

Then the close-out, all of it:

- the checklist issue's last comment says what went live where, with ids and hashes;
- the checklist issue is closed; the milestone is left exactly as it is — opening or closing one is Dave's call;
- a handoff memory (what shipped, what was deferred, what bit);
- **machines left as found**: the WACK task deleted, no test package left on GPD-DAVE, the
  Mac mini's and kdocker2's `~/megapdf-work/<task>/` gone, every agent worktree and branch
  pruned (`git worktree prune`; `git cherry origin/main <branch>` before deleting one),
  `D:\megapdf-qa\` emptied of this release's scratch — and **no window or dialog left open**:
  after any capture or QA rig, look at the screen and the window list
  (`Get-Process | ? MainWindowTitle`), not only for MegaPDF processes.

## 5. Traps

Terse, with the date each one bit.

- **The fix being merged is not the fix being present — check the tree before every release tool, not just before deploying** (2026-10-02). A release runs `msstore.sh`, `asc-publish.sh`, `play.sh` and `deploy.py` out of a working tree, and that tree drifts while agents merge PRs elsewhere. Two failures from the same night, same cause: a `deploy.py --linux --dry-run` from a tree 63 commits behind reported the Linux page offering 2.1.1 — against a repository that also held 2.1.1, so it looked like agreement and would have rolled the live site **back** from 2.2.0 on a real deploy (§3 now opens the Linux step with the check below). Separately, `#655`'s `asc_publish.py` screenshot fix was merged, then `tools/asc-publish.sh screenshots` was run from a shared checkout still eleven commits behind: it ran the **old** script, uploaded **0 screenshots** for iOS across all three locales, and — because it was the old script — the loud short-set failure #655 added never fired either; the symptom was a cheerful "0 screenshots" six times over. Before running *any* of them: `git log --oneline -1 HEAD` against `git log --oneline -1 origin/main`, every time, not only before a deploy.
- **glibc / GLIBCXX ceiling** (2026-09-26, #397). The Ubuntu 24.04 runner builds against glibc 2.39 / GCC 13; the .deb promises `libc6 >= 2.35` and Debian 12 has `GLIBCXX_3.4.30`. `megapdf-cli` quietly imported `GLIBC_2.38` and died on install. `build-linux-app.sh` now refuses any ELF above 2.35 / 3.4.30 and `check-apt-repo.sh` runs the CLI in every container.
- **ASC 500 on inherited screenshots, 409 on the review detail** (2026-09-26). A version created from the last inherits both; deleting the screenshots one by one got 500 for minutes, creating the detail got 409. `asc_publish.py` now drops and recreates the set, and updates the detail on 409.
- **ITMS-90788** (2026-09-26). A `CFBundleDocumentTypes` entry without `LSHandlerRank` is a warning on upload; `Alternate` is in `ios/project.yml` now. It cost a second build (33 for 32).
- **`Add-AppxPackage` on the same version is a no-op** (2026-09-17, #146). It reports success and leaves the old build installed; `-ForceUpdateFromAnyVersion` does not help. Remove first, through `Install-QaTestPackage` (`tools/windows-qa/qa-safety.ps1`), which removes only a test build it installed and stops at a Store install (#717).
- **`Shoot-Set.ps1` refuses unless the installed package is its `-Version`** (2026-09-20). Right, but it means the Store set cannot be shot until the release package is installed, which is after §2.3.
- **The picker over OneDrive** (2026-09-26). On GPD-DAVE the Open dialog opens on Dave's OneDrive. Fine in test evidence, never in a public capture; drive the picker off-frame or at the demo folder, and read every image.
- **PowerShell heredocs truncate** (2026-09-26). A long script fed to `powershell.exe -Command -` lost its tail and ran half. Write a `.ps1` under `D:\megapdf-qa\` and dot-source it from a short heredoc (`-File` is blocked by the secrets hook).
- **Agents on one host need unique directories** (2026-09-26). Two agents briefed with the same clone on kdocker2: one's clean-up deleted the other's tree mid-job. `~/megapdf-<issue>-<role>`, and "never touch a sibling" in every brief.
- **The secrets hook, and why the wrappers exist** (2026-09-19, 2026-09-26). It denies any command naming a credentials file, its folder or a token word, even an existence check. `tools/msstore.sh`, `tools/asc-publish.sh`, `tools/play.sh` keep those off the command line. Don't work around it; use them.
- **Both Windows packages from one CI run** (2026-09-19, #306). 2.0's x64 was built locally and its ARM64 in CI, from different commits, with different .NET runtimes. `MegaPDF-store-packages` of one run, both files, recorded in `SHA256SUMS`.
- **The metainfo's newest `<release>` must be dated within 30 days** (2026-09-26). Or `make-release-tarball.sh` refuses at the tag. Re-date if the tag slips.
- **CI green ≠ works** (2026-09-26). The lost click (#401), the disabled Open button (#412), the Android picker that made `.md.pdf` (#409), the CLI that would not load (#397): all on green runs. Real-machine verification is the gate, and every image is read.
- **WACK can silently do nothing, and it looks exactly like not having started** (2026-10-01). `wack-launch.ps1` wrote its log to `D:\megapdf-qa\` as its first statement; an agent's routine scratch cleanup had deleted that directory, so the script threw before it ever raised the UAC prompt. Symptom: "no WACK on screen". Diagnose with `Get-ScheduledTaskInfo -TaskName 'MegaPDF WACK'` — `LastTaskResult 267011` with a 1999 `LastRunTime` means it has never run. The launcher now creates the directory. Also move the **done-marker** aside with the report, not just the report — a stale marker can make the gate look finished when it is not.
- **Windows PowerShell reads a BOM-less `.ps1` as ANSI** (2026-10-01). A non-ASCII character in a *comment* broke string parsing with a misleading `Missing closing '}'` pointing at an unrelated line. Keep the `tools/windows-qa` scripts ASCII-only.
- **Renaming capture slots silently breaks `tools/asc_publish.py`** (2026-10-01, #655). It hardcodes slot names. After the 2.2 capture work renamed them, `screenshots` uploaded **1 of 7** on Mac — only `02-text` survived the rename — and would have uploaded **0** on iOS, whose captures sit in a `listing/` subdirectory the tool did not read. It printed "1 screenshots" and exited 0. Fixed in #655 to fail loudly on a short set. Since #656 the slots live in `tools/store_slots.py` alone; change them there, update the capture rig the test names, and run `python3 tools/test_store_slots.py`. iPhone and iPad have **different** orders by decision.
- **`deploy.py` from a worktree (#668).** Fixed: it finds the login file beside the main checkout (2.4.0's four go-live deploys all ran from fresh worktrees at `origin/main`). If its dry run says `Login file ... - NOT FOUND`, set `MEGAPDF_DEPLOY_ENV_DIR` to the folder that holds it; never symlink a folder into `.claude/worktrees/` (2.2.1's workaround). The rehearsal is run before the release is published; only the real deploy follows publishing.
- **Publish the release before deploying the website** (2026-10-02). Deploying first left the live Linux page offering 2.2.0 `.deb` and tarball links that **404'd**, because the GitHub release was still a draft. `gh release edit linux-v<x.y.z> --draft=false` first, then deploy — §3's Linux sequence already has this order; this is the concrete failure that order prevents.
- **Deploying from a stale checkout silently regresses the live site** (2026-10-02). A `--dry-run` from a tree 63 commits behind reported "`linux/index.html` offers 2.1.1" against a repository holding 2.1.1 — passed, because both sides were stale the same way — but a real deploy from that tree would have rolled the live Linux section **back** from 2.2.0. §3's Linux step now opens with the `git log` check this would have caught.
- **`deploy.py --linux`'s refusal is correct; a direct download into `website/megapdf/apt` is not** (2026-10-02). "`linux/index.html` offers X, but the repository holds Y" means run the tag build and download its `MegaPDF-apt-repository` artifact — but downloading straight into `website/megapdf/apt` fails "file exists" and can abort part-way, because the artifact also carries the **tracked** `megapdf.asc`/`.gpg`/`.sources`. §3's Linux step now extracts to a scratch directory and copies in only `dists/` and `pool/`.
- **This WSL session runs elevated, so everything it launches does too — and an elevated WinUI app gets no file picker** (2026-10-19, #696). Open, Save As and Print appeared to do nothing on the 2.3.0 package: `OpenAsync` threw `COMException 0x80004005`, logged only to `%TEMP%\MegaPDF-crash.log`. Launched at normal rights all three appear. Drive §2.3 on Windows through `explorer.exe shell:AppsFolder\ElectricRV.MegaPDF_fba94j4nmgb9y!App`, or a scheduled task at `-RunLevel Limited` for Open with, never by starting `MegaPDF.exe` from the shell.
- **Push at most three tags at once** (2026-10-19). GitHub creates no events when more than three tags arrive in one push: four release tags went up together and not one workflow started, with nothing to say so. Delete the remote tags and push them one at a time; then check `gh run list` shows a run per tag before moving on.
- **A corpus battery's time is a gate too** (2026-10-19, #704). The 2.3.0 markdown run's scores matched 2.2.0's. What was wrong was its time, and it was Dave who noticed the run going long: two large JPEG 2000 scans took 382 s and 462 s, against 14 s and 15 s. Cause: `FPDFImageObj_GetImageMetadata` decodes a whole JPX or JBIG2 image to report bit depth and colour space. Compare per-file times against the last release's run, not only the scores; and read an image's filter with `FPDFImageObj_GetImageFilter`, which costs nothing, before asking PDFium for anything that needs its pixels.
- **The App Review notes grow past 4000 between releases** (2026-10-20, #707). Feature work adds to them mid-cycle (#676 did), and nothing counts them until submission: 2.3.0's iOS notes stood at 4,454 characters before the release added a word. Count them in §2.5 the way `asc_publish.py`'s `review_notes()` does (blockquotes dropped, emphasis stripped), before `asc-publish.sh review` refuses.
- **Ready the simulator before every later `xcodebuild` pass** (2026-10-24, #809). `test-without-building` shuts the simulator down at the end of a pass, and the next pass installs the app over the old copy while still booting the device; SpringBoard refuses the launch ("Application failed preflight checks"). On the mini: 6 of 6 passes refused with the device shut down, 0 of 5 once `simctl bootstatus -b` had finished first. `ios-ci.yml` now runs `scripts/ci-simulator.sh ready` before the French and Spanish passes; any script that runs `xcodebuild` twice against one simulator needs the same (`tools/mac-mini.md`).
- **Never reboot the Mac mini without Dave, and re-dispatch after any reboot before calling anything verified** (2026-10-24, #614). #614 was closed on "the runner comes back online after the restart"; every iOS job after that reboot failed until #809. A runner that is online is not a build that passes: run a self-hosted iOS dispatch to green first.
- **A library can add permissions you never wrote** (2026-10-24, #821). §2.3's Android paragraph: the guard is `:app:checkNoPermissions`, and the store texts are re-read against the merged manifest.
- **The metainfo points at images the website must already serve** (2026-10-24, #724). §3, Linux; the read-back is the last line of §4's block.
