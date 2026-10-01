# The in-house Mac — build, test and capture runbook

A Mac mini (M4, macOS 26) on the house network is the build and capture box
for the iOS and macOS apps. It replaces GitHub's macOS runners for anything
that wants a real screen: App Store preview videos, listing screenshots, and
driving the real apps. CI still proves the builds; this machine is for the
things a runner cannot see.

## Reaching it

- `ssh mac-mini` from the WSL user on GPD-DAVE, key-only, as the automation
  account `claude` (admin, no passwordless sudo — builds never need it).
  Screen Sharing is on for the rare case a dialog must be clicked.
- Keep every bit of state under `/Users/claude`. Never touch the owner's
  account, add SSH keys, or change auto-login.
- Non-interactive SSH has a bare PATH. Start scripts with
  `export PATH="/opt/homebrew/bin:$HOME/.dotnet:$PATH"`.
- macOS bash is 3.2: `set -u` with an empty array (`"${T[@]}"`) is an error.
- **The only display is the KVM's HDMI capture.** If it is asleep the Avalonia
  app dies on launch with "not able to start the RenderTimer … -6661" (no
  active display for CVDisplayLink) and `system_profiler SPDisplaysDataType`
  lists no display. Display sleep is now set to never (`pmset displaysleep 0`,
  house-network change 2026-09-11); `caffeinate -u -t 3` wakes it if that ever
  regresses, and the capture scripts still do it. The same timer also fails
  intermittently while simulators boot; the scripts retry. Simulators are
  unaffected either way.

## CI on this machine: turning it on and off (#614)

`ios-ci.yml`'s `build` job runs here instead of on GitHub's macOS runners when,
and only when, **both** of these are true. It is 36-55 minutes there and it
queues behind every other job in the account; here it is ours and idle.

**The two commands.** Run them from anywhere — they need no access to the Mac:

```
gh variable set MAC_SELF_HOSTED --body on     # use the Mac mini
gh variable set MAC_SELF_HOSTED --body off    # back to GitHub's runners
gh variable list                              # which is it right now
```

That is the whole switch. The variable is read in `runs-on`, so it takes effect
on the next job to start; jobs already running are unaffected. **Set it to `off`
whenever the Mac is going to be unavailable** — shut down, rebooting, on a
different network, or being used for a capture session you do not want a CI job
walking into. A job that asks for a runner which never appears does not fail
fast: it sits in the queue for 24 hours and *then* fails. One `off` sends those
jobs straight back to GitHub.

**The second condition is the fork guard, and it is not a switch.** This
repository is public. A self-hosted runner executes whatever a workflow file
tells it to, on this machine, as the account it runs under — so a pull request
opened from a **fork** never comes here, no matter what the variable says. It
goes to GitHub's disposable macOS runner, which is what that is for. The guard
is the `runs-on` expression in `ios-ci.yml`; it was proved by
`.github/workflows/runner-selection-probe.yml`, not assumed, and the result is
recorded on #614. There are no forks today, and the setting GitHub offers for
this ("Require approval for fork pull request workflows") is only on
*first-time* contributors here, so the guard has to be in the workflow.

**Starting and stopping the runner on the machine itself** is separate from the
variable, and needed after a reboot, after every job (see "ephemeral" below), or
if it has been stopped:

```
# is it registered and waiting?
gh api repos/SlyWombat/MegaPDF/actions/runners --jq '.runners[] | {name,status,busy}'

# start it (it takes one job, then exits)
ssh mac-mini 'cd /Users/claude/actions-runner && nohup ./run.sh >> ~/mp-614/runner.log 2>&1 &'

# stop it
ssh mac-mini 'pkill -f Runner.Listener'
```

Notes on how it is set up, and why each part is the way it is:

- **It runs as `claude`** (Dave's decision, 2026-10-01), which is also the
  account that holds the read-write deploy key for this repository, the six
  working clones — two with unpushed commits — and the PDF corpus. **A job that
  reaches this runner can read all of it.** That is the whole reason the fork
  guard above lives in the workflow file rather than in a repository setting: the
  guard is the only thing between a stranger's pull request and that material, so
  it must not be one click from being loosened. The runner's own files and work
  directory sit under `/Users/claude/actions-runner`, nowhere near the clones,
  but that is tidiness and not isolation — same account, same reach. Do not add
  further credentials to this account while the runner is registered.
- **Registered `--ephemeral`**, so the runner takes exactly one job and then
  removes its own registration and exits. A job that compromised the runner
  cannot persist into the next one. The cost is real: **a fresh registration is
  needed before every job.** That needs a registration credential, which needs
  `gh`, and there is deliberately no `gh` and no GitHub credential on the Mac —
  so it is done from a machine that has one, with `tools/mac-runner-configure.sh`,
  which takes the credential on **stdin** and never puts it in a command-line
  argument (argv is visible in the process table to anything that runs later on
  this shared machine, which is how a Home Assistant token leaked on 2026-09-08).
  That script's own header carries the one-line invocation, and it verifies the
  runner tarball's checksum before unpacking it — the thing it unpacks becomes a
  process with full access to this account.

  If that per-job step proves too much friction, the alternative is to drop
  `--ephemeral` and let one registration serve many jobs. That is defensible
  here: what actually stops state carrying between runs is the per-job cleanup in
  the workflow, not the registration, and ephemerality buys little when every job
  shares the `claude` account anyway. A deliberate decision, not one to drift
  into.
- **Label `megapdf-ios`.** The job asks for `[self-hosted, macOS, ARM64,
  megapdf-ios]`, so nothing else in the repository can drift onto this machine
  by accident.
- **Every job starts from nothing**: the workspace is emptied before checkout,
  derived data goes under the runner's own temp directory rather than the shared
  `~/Library/Developer/Xcode/DerivedData`, and the two simulators are created
  for the job and deleted by UDID when it ends. That last one is deliberate and
  not optional — #569 was a flake caused entirely by state surviving between
  runs. It also means a CI job will never shut down or erase a simulator you
  booted yourself: it only ever touches devices named `megapdf-ci-*`.
- **Not installed as a launchd service.** On macOS the runner's own `svc.sh`
  installs a per-user LaunchAgent, and CoreSimulator wants a real user session, so
  a headless daemon may not be able to boot a simulator at all. The manual start
  above is proven; a service is a deliberate follow-up, not a guess. After a
  reboot, either start it again or set `MAC_SELF_HOSTED` to `off`.
- **This machine is shared, and that is the main running cost.** The 2026-10-01
  measurement ran while another agent was building on the Mac, at load average
  11. A CI job here competes with whoever is using the Mac, in both directions:
  the job is slower and so is their work. On GitHub's runners nobody is in
  anybody's way. Decide which matters more on the day, and use the variable.
- **What is still hosted, on purpose.** `ios-ci.yml`'s `pdfkit-spike` job, and
  every `macos-latest` job in `macos-app.yml`, `macos-appstore.yml`,
  `macos-tester-build.yml`, `macos-screenshots.yml`, `core-tests.yml` and the
  release workflows. The release and signing ones must stay hosted: they
  materialise a Developer ID certificate and an App Store Connect key from
  secrets, and those must not be on a desktop that a CI job can read. The others
  stay hosted because they want .NET, which is a per-user install here, and
  because keeping some macOS work on GitHub means that path stays exercised
  rather than rotting until the day this machine is unavailable.

**What the 2026-10-01 measurement showed**, because the headline expectation was
wrong in an instructive way. Same commit, same workflow file, one run each way:

| | GitHub `macos-latest` | Mac mini |
|---|---|---|
| Waiting for a runner | **26 min** | 12 s |
| Boot the simulator | 158 s | 11 s |
| The suite again in French | 239 s | 35 s |
| iPhone UI lane | 1443 s, and red | 917 s |
| iPad UI lane | never reached | 821 s |
| Unit build and test | 386 s | 421 s |

The compute is **not** dramatically faster — the unit build is marginally slower
on the Mac under load, and the whole job came out at 37 minutes either way. What
self-hosting removes is the **queue**, and the hosted red-then-skip: a hosted run
that fails the iPhone lane never runs the iPad lane at all, so #579's 36 minutes
was never a whole job. The Mac did both lanes in 37 minutes, having started
twelve seconds after the dispatch.

And the result that was hoped for and did not arrive: #599's three marginal
page-tools tests take **136-141 s on the Mac**, against the 139-146 s #599
recorded on a hosted runner. That duration is the tests' own cost, not runner
load, so dedicated hardware does not restore their margin and does not close
#599.

## What is installed (2026-09-11)

| Piece | Where | Note |
|---|---|---|
| Xcode 26.6 | `/Applications/Xcode.app` | licence accepted; iOS 26.5 simulators, iPhone 17 line and the current iPads |
| Homebrew | `/opt/homebrew` | `xcodegen`, `ffmpeg`, `cmake`, `ninja` (the last two build the shared engine core, `tools/build-core.sh`, 2026-09-13) |
| .NET 8 SDK | `~/.dotnet` | per-user, from `dotnet-install.sh`; builds the Avalonia app |
| Repo | `~/Projects/MegaPDF` | clone over the read-write deploy key; `git pull` before a session |
| iOS PDFium | `~/Projects/MegaPDF/ios/Vendor` | `ios/scripts/fetch-pdfium.sh`, gitignored. **It skips the fetch when the folder is already there** — which is right for CI's version-keyed cache and wrong for this machine, where the folder simply stays as old as the day it was made. A Vendor/ from a fortnight earlier failed the 2.0 iOS build on ten of the core's patched PDFium symbols, undeclared. `rm -rf ios/Vendor` after a long `git pull`. |
| Simulator build | `~/dd-ios` | derived data shared by the capture scripts |
| Mac app | `~/app-macos/MegaPDF.app` | `tools/build-macos-app.sh osx-arm64 ~/app-macos` |
| Captures | `~/captures/` | never committed; copy what is wanted into `artifacts/` locally |
| GitHub Actions runner | `/Users/claude/actions-runner` | 2026-10-01, #614. Runs as `claude`, so a job that reaches it can read the deploy key and the clones — the fork guard in `ios-ci.yml` is what keeps strangers off it. Ephemeral, label `megapdf-ios`. See "CI on this machine" above. |

## Recipes

All from `~/Projects/MegaPDF` on the Mac.

**Prove the builds** (what CI does, on real Apple silicon):

```
cd ios && xcodegen generate && xcodebuild test -project MegaPDF.xcodeproj -scheme MegaPDF \
  -destination 'platform=iOS Simulator,name=iPhone 17 Pro' -derivedDataPath ~/dd-ios CODE_SIGNING_ALLOWED=NO
tools/build-macos-app.sh osx-arm64 ~/app-macos
python3 tools/gen_test_fixtures.py ~/fixtures
python3 tools/gen_redaction_fixtures.py ~/fixtures   # the self-test's redaction half (#173); same folder, no name clashes
~/app-macos/MegaPDF.app/Contents/MacOS/MegaPDF --render-check ~/fixtures/stamped.pdf
~/app-macos/MegaPDF.app/Contents/MacOS/MegaPDF --self-test ~/fixtures
dotnet test tests/MegaPDF.Core.Tests/MegaPDF.Core.Tests.csproj -c Release
```

**iOS preview videos**: `tools/ios-demo-video.sh <en|fr-CA|fr> [device] [label] [out]`
— see `docs/app-store-listing.md` § App preview videos for what the three
output files are for. Takes about two minutes per device. The device argument
is a **regex** over the available simulators, not a name: Xcode 26.6 calls the
13" iPad "(M5)" and an exact name stopped finding it. `'iPhone .*Pro Max'` and
`'iPad Pro 13'`, as `ios-screenshots.sh` spells them.

**iOS listing screenshots**: `tools/ios-screenshots.sh <lang> [out]` — the CI
recipe, locally. The six listing slots land in `<out>/listing`, the review shots
in `<out>/review`.

**iOS file flows, end to end**: `BIG_PDF=<1 GB fixture> tools/ios-files-e2e.sh [device] [out]`
opens real files through the real Files picker, then runs redaction (checked from
outside the app by `tools/leakcheck/outside.py`, poppler and qpdf only, against a
control that must fail), save, save a copy, set, refuse and remove a password, and the
1 GB file with timings. It reads each result back out of "On My iPhone" between tests.
Everything else in `MegaPDFUITests` opens bundled bytes with no file behind them, so
this is the only place those flows run on iOS. Use a simulator of its own
(`xcrun simctl create "MegaPDF E2E" …`) so its Files state belongs to the run.

Two traps it gets past, each of which cost a pass (2026-09-18):

- **Staging into "On My iPhone".** A simulator has *three* folders named
  `File Provider Storage`: Photos, iCloud Drive and the local one. `find … | head -1`
  picks whichever comes first, and a fixture copied into the wrong one never shows
  in the picker ("… is not visible in the Files picker"). Pick the app group whose
  container metadata says `group.com.apple.FileProvider.LocalStorage` (see
  `local_storage()` in the script). That folder exists only after Files has run once
  on the simulator, so launch `com.apple.DocumentsApp` once first.
  `tools/ios-review-video.sh` had the same `head -1` and was fixed the same way.
- **The export sheet on iOS 26 has no name field.** It opens in the last folder
  used, and its Save lives in the nav bar `FullDocumentManagerViewControllerNavigationBar`.
  An unscoped `buttons["Save"]` finds the app's own greyed-out Save first. A name clash
  asks Keep Both or Replace, so the copy is found as the one new file in the folder.

**Mac listing screenshots**: `tools/macos-store-captures.sh <lang> [out] [app]` —
the six Mac App Store slots at 1440x900, one process per image, with the
fixtures, the signature library and the recents owned by the run rather than by
this machine. `docs/qa/mac-store-captures.md` is the runbook and the gate. There
is no workflow for this one: it needs a real display.

**macOS preview video**: `tools/macos-demo-video.sh ~/app-macos/MegaPDF.app ~/captures/macos/video 1440x900 [light|dark] [en|fr-CA|fr]`
— the app's `--story` mode renders a frame after each step (tick, tick,
sign, print the name, find ×3, done) and ffmpeg holds each for a couple of
seconds. Real window states, no pointer. This is the **fallback**: it needs no
privacy grants, and 1440x900 is a screenshot size rather than an app-preview
one. `macos-record-demo.sh` below is what the 2.0 clips were shot with.

**macOS screenshots**: the app renders its own window, so no Screen Recording
permission is involved. `--window WxH` sets the size (1440x900 is a Mac App
Store size and the smallest at which the whole toolbar fits); `--theme dark`
forces dark; `--screenshot-state find|focus|mode` poses it. Fixtures must sit
inside the sandbox container:

```
C="$HOME/Library/Containers/com.megapdf.ios/Data"; mkdir -p "$C/tmp/fixtures"; cp ~/fixtures/*.pdf "$C/tmp/fixtures/"
~/app-macos/MegaPDF.app/Contents/MacOS/MegaPDF "$C/tmp/fixtures/demo.pdf" --window 1440x900 --screenshot out.png
```

**Long jobs**: an SSH command from a Claude session is cut off after ten
minutes. Run anything longer with `nohup … &` on the Mac and tail its log.

## Screen recording and synthetic input over SSH

macOS privacy (TCC) grants apply per app, and an SSH session's app is
`sshd-keygen-wrapper`. Until 2026-09-11 it had none, so `screencapture` failed
with "could not create image from display" and the Mac clip was assembled from
app-rendered frames instead. Since then the owner has granted the SSH context
**Screen & System Audio Recording** and **Accessibility** in System Settings
(GUI-only on macOS 26; done through the KVM), and `screencapture -x` over a
fresh SSH session returns a real 2560x1440 PNG. An SSH session that was open
before a grant does not see it — reconnect. Automation for System Events is
granted too (window placement); use `tell application "System Events" to tell
process …` for other apps rather than `tell application X`, which would raise
a new consent prompt that only the KVM can answer. Never `tccutil reset`.

Re-proved 2026-09-18 and the 2.0 Mac preview clips were recorded through it, so
**nobody has to be at the Mac for a Mac clip.** What the Avalonia app does *not*
expose is an accessibility tree worth driving: System Events sees one window,
three traffic lights, a title and four unnamed empty groups, so every click is
still a coordinate.

**macOS preview video, recorded for real**: `tools/macos-record-demo.sh
[app] [out] [light|dark] [en|fr-CA|fr]` — cliclick drives the window while screencapture
records 1920x1080, which is the Mac App Store's app-preview frame. It measures
where the page is off a screenshot and maps every click from PDF points, and
seeds the signature library if empty. For dark, switch Appearance first (`tell
appearance preferences to set dark mode to true`) and back after. Finding that
clicks missed at every zoom but fit-width was how the page-click bug in the
Mac app was found and fixed.

Three things about it that cost a run each, 2026-09-18:

- **The toolbar buttons are measured, not written down** (`tools/macos-measure-toolbar.py`).
  The coordinates in the script were the pre-#144 ones and by 2.0 pointed at
  empty bar. They also move with the language — French puts Sign at 172 and
  Add text at 250 where English has 136 and 190.
- **The window sits at x=0.** macOS puts notification banners in the top-right
  of the screen, and a 1920-wide window at x=320 on this 2560-wide display ends
  at 2240, which is inside the banner. At x=0 there is 640 px of clearance and
  no Focus setting to change.
- **screencapture cannot be stopped.** It ignores SIGINT and runs its whole
  `-V`; SIGTERM kills it and takes the unfinalised file. So `-V` is a ceiling,
  the wall clock says how long the story took, and the cut keeps that. And the
  `-t` that does the cutting goes **after** `-i`: screencapture writes a
  variable-rate movie (253 frames in ninety seconds) and before `-i`, `-t` cuts
  three and a half seconds late.

**Checking a preview clip**: `tools/preview-gate.py` reads the container — the
slot size, App Store Connect's 15-30 s, the constant 30 fps, H.264 and the
silent audio track it is refused without — and cuts the clip into one still per
second for `tools/capture-gate/gate.py --store video`, one language at a time.
ImageMagick and tesseract are not on this Mac, so run that half on kdocker2.
