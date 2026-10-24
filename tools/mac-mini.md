# The in-house Mac — build, test and capture runbook

A Mac mini (M4, macOS 26.7.1, Xcode 26.6) on the house network is the build and capture box
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
and only when, **both** of these are true: the switch below is on, and the job
is not a fork's. It is 36-55 minutes there and it queues behind every other job
in the account; here it starts in seconds.

**The two commands.** Run them from anywhere — they need no access to the Mac:

```
gh variable set MAC_SELF_HOSTED --body on     # use the Mac mini
gh variable set MAC_SELF_HOSTED --body off    # back to GitHub's runners
gh variable list                              # which is it right now
```

That is the whole switch. The variable is read in `runs-on`, so it takes effect
on the next job to start; jobs already running are unaffected. **Set it to `off`
whenever the Mac is going to be unavailable** — shut down, being upgraded, on a
different network, or being used for a capture session you do not want a CI job
walking into. A job that asks for a runner which never appears does not fail
fast: it sits in the queue for 24 hours and *then* fails.

**A job already queued for the Mac keeps its labels after the variable goes
`off`**, and goes on waiting for the Mac. The flip only steers jobs that start
after it. Cancel such jobs with `gh run cancel <id>` (and re-run them, which now
lands on GitHub), or bring the Mac back. The order is always: variable `off`
first, then stop the service, so nothing new queues for a machine that is going
away.

**The fork guard has two layers, and the one that matters is on the machine.**
This repository is public, and a self-hosted runner executes whatever a workflow
file tells it to, on this machine, as `claude`.

1. **The `runs-on` expression in `ios-ci.yml`** sends a pull request from a fork
   to GitHub's disposable runner whatever the variable says. It was proved by a
   probe workflow (run 36888016328, recorded on #614), including the deleted-fork
   case where `head.repo` is null and a `fork == false` test alone fails open.
   But for a `pull_request` event GitHub runs **the pull request's own copy** of
   the workflow files, so a fork's pull request can delete that expression, or add
   a new workflow asking for `[self-hosted, megapdf-ios]`. This layer keeps
   *honest* fork PRs on GitHub, where they pass instead of being refused. It is
   not what keeps strangers off the machine.
2. **The runner's job-started hook**, `tools/mac-runner/job-started.sh`, installed
   as a copy in `~/actions-runner/megapdf/` and named by
   `ACTIONS_RUNNER_HOOK_JOB_STARTED` in the LaunchAgent. It runs before the first
   step of **every** job this runner takes, and no pull request can edit it. It
   fails the job unless the repository is this one, the workflow is `ios-ci.yml`,
   and the event is a `push`, a `workflow_dispatch`, or a `pull_request` whose head
   repository is this repository and not a fork. It fails closed on anything
   missing, including a null `head.repo`. `tools/mac-runner/test-job-started.sh`
   tests it, in CI (`mac-runner-guard.yml`) and by hand against the installed copy.
   Each job it saw is a line in `~/Library/Logs/megapdf-runner/job-started.log`:
   `allowed` or `REFUSED(<reason>)`, with the run id, event and workflow.

On top of both, the repository's fork-approval setting is "require approval for
all external contributors" (Dave, 2026-10-21): no fork's workflow runs anywhere
until it is approved. That is a third layer; neither of the first two depends on
it.

**The service on the machine** is separate from the variable. It is a
LaunchAgent, so it starts itself:

```
# is it registered and waiting?
gh api repos/SlyWombat/MegaPDF/actions/runners --jq '.runners[] | {name,status,busy}'

# on the machine
ssh mac-mini 'bash ~/Projects/MegaPDF/tools/mac-runner/service.sh status'     # launchd state + log tails
ssh mac-mini 'bash ~/Projects/MegaPDF/tools/mac-runner/service.sh restart'    # kickstart -k
ssh mac-mini 'bash ~/Projects/MegaPDF/tools/mac-runner/service.sh install'    # (re)write plists, copy the hook, bootstrap
ssh mac-mini 'bash ~/Projects/MegaPDF/tools/mac-runner/service.sh uninstall'  # bootout, delete plists and hook copies
```

`install` is also how a changed hook reaches the machine: the installed copy does
not follow `git pull`, on purpose. Logs are in `~/Library/Logs/megapdf-runner/`
(`runner.log`, `job-started.log`, `prune.log`); a weekly agent
(`ca.electricrv.megapdf.actions-runner-logs`, Sundays 04:30) prunes the runner's
`_diag` and rotates `runner.log` past 10 MB, because launchd rotates nothing.

Notes on how it is set up, and why each part is the way it is:

- **It runs as `claude`** (Dave's decision, 2026-10-01), which is also the
  account that holds the read-write deploy key for this repository, the six
  working clones — two with unpushed commits — and the PDF corpus. **A job that
  reaches this runner can read all of it.** That is why the guard lives on the
  machine and not in a workflow file or a repository setting. The runner's own
  files and work directory sit under `/Users/claude/actions-runner`, nowhere near
  the clones, but that is tidiness and not isolation — same account, same reach.
  Do not add further secrets to this account while the runner is registered.
  A second account is not an option: simulators need a GUI session, and the
  auto-login is `claude`.
- **Registered once, standing** (Dave, 2026-10-21: "we have a standing order to
  use local resources"). The registration persists in the runner directory, so
  the runner takes job after job. Registering again is needed only after
  `config.sh remove`; it is done from a machine that has `gh` (there is
  deliberately no `gh` and no GitHub token on the Mac) with
  `tools/mac-runner-configure.sh`, which takes the registration token on
  **stdin** and never puts it in a command-line argument (argv is visible to
  anything that runs later on this shared machine, which is how a Home Assistant
  token leaked on 2026-09-08). That script's header carries the one-line
  invocation, and it verifies the runner tarball's checksum before unpacking it.
  **The runner updates itself** after registration, and that stays on: GitHub
  stops sending jobs to a runner that is too old, which would be the 24-hour
  silent queue again. What stops state carrying between runs is the per-job
  cleanup in the workflow (below), not the registration: a job that wanted to
  leave something behind could do it with push access anyway. The runner
  directory holds the registration and its keys; never print anything in it,
  check files with `test -f`.
- **One runner slot; jobs queue.** The Mac is shared with agents' builds, and a
  CI job boots two simulators. A queue here is minutes; the hosted queue measured
  26. `ios-ci.yml`'s `concurrency` group cancels a pull request's older run when
  it gets a new push, so the queue never works through stale commits, and
  `timeout-minutes: 90` stops one hung job blocking everyone behind it.
- **Label `megapdf-ios`.** The job asks for `[self-hosted, macOS, ARM64,
  megapdf-ios]`, so nothing else in the repository can drift onto this machine
  by accident (and the hook refuses any other workflow that tries).
- **Every job starts from nothing**: the workspace is emptied before checkout,
  derived data goes under the runner's own temp directory rather than the shared
  `~/Library/Developer/Xcode/DerivedData`, and the two simulators are created
  for the job and deleted by UDID when it ends, cancelled or not. That last one is
  deliberate and not optional — #569 was a flake caused entirely by state
  surviving between runs.
- **A LaunchAgent, `ca.electricrv.megapdf.actions-runner`, in `gui/502`.**
  `LimitLoadToSessionType Aqua`, so it runs only in the GUI login session, where
  CoreSimulator works; a LaunchDaemon has no such session. It runs the runner's
  own service host, `runsvc.sh`, with `KeepAlive` on an unsuccessful exit and a
  30-second throttle, so it comes back after a crash and after a self-update.
  **It deliberately does not set `SessionCreate`**, which the runner's own
  template (`bin/actions.runner.plist.template`) does: that starts the job in a
  new security session, outside the login session that the simulators and the
  login keychain belong to. So never use the runner's `svc.sh install`; it
  brings `SessionCreate` back and loads with `launchctl load`, which from SSH
  lands in the SSH session's domain. `service.sh` uses `launchctl bootstrap
  gui/502` only. After a reboot the agent starts at the `claude` auto-login
  with nobody involved. The house restart window waits for `xcodebuild` and
  `xctest` to finish before it restarts (house-network-ops#426).
- **This machine is shared, and that is the main running cost.** The 2026-10-01
  measurement ran while another agent was building on the Mac, at load average
  11. A CI job here competes with whoever is using the Mac, in both directions.
  Agents are not paused for CI and CI is not paused for agents; contention costs
  speed, not correctness. An agent quoting a timing from the Mac says whether
  `pgrep -x Runner.Worker` found a CI job running.
- **Boot the simulator yourself before an `xcodebuild test-without-building` that follows
  another pass on the same device (#614, 2026-10-24).** xcodebuild shuts the device down at
  the end of a pass and, next time, installs the app over the old copy *while it boots the
  device*. After the 2026-10-24 reboot SpringBoard refused that launch for the full 20 s
  ("Cannot launch application scene ... while it's application is being updated", surfacing
  as `SBMainWorkspace ... Busy ("Application failed preflight checks")`): 6 of 6 passes on a
  device that already had the app, 0 of 5 when the device was booted and `bootstatus` had
  finished first. `ios-ci.yml` runs `scripts/ci-simulator.sh ready` before the French and
  Spanish passes for this reason; do the same in any script that runs xcodebuild twice
  against one simulator. Before the reboot the same sequence passed (every job to
  2026-10-24 02:27Z), so the reboot changed the timing of the boot against the install; what
  in the boot changed was not found.
- **Simulator names: `megapdf-agent-<n>`, never `megapdf-ci-<n>`.** A CI job
  deletes **every** `megapdf-ci-*` simulator when it starts, as leftovers of a
  cancelled job of its own. With an ephemeral runner a CI job rarely overlapped
  an agent's run; with a standing one it will, and an agent's `megapdf-ci-*`
  device would vanish mid-run. Name yours `megapdf-agent-<issue>`.
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

**The standing runner against hosted, measured 2026-10-22 (#614 proof step 16).** Three
dispatched hosted runs of the same workflow (`gh workflow run ios-ci.yml -f runner=hosted
--ref main`), spread over one day because hosted timing moves hour to hour, beside every
`build` job the mini ran in the last forty runs. Not same-SHA pairs: a merged head cannot be
re-run on the mini after the fact, so the comparison is of the workflow, not of one commit.

| runner | run | dispatched (EDT) | SHA | wait | job | Build and test | iPhone UI lane | iPad UI lane |
|---|---|---|---|---|---|---|---|---|
| hosted | [39818409951](https://github.com/SlyWombat/MegaPDF/actions/runs/39818409951) | 12:57 | a3111b4 | 3 s | 55 min 51 s | 364 s | 1090 s | 1000 s |
| hosted | [39845022875](https://github.com/SlyWombat/MegaPDF/actions/runs/39845022875) | 16:00 | e709ac7 | 3 s | 53 min 33 s, **red** | 423 s | 961 s | 982 s, red |
| hosted | [39868868578](https://github.com/SlyWombat/MegaPDF/actions/runs/39868868578) | 19:00 | e99d317 | 2 s | 47 min 59 s | 367 s | 925 s | 924 s |
| mini, median of 29 | since 2026-10-21 15:50 UTC | | | **15 min** (1 s to 76 min) | **15 min 46 s** | 68 s | 421 s | 308 s |

- **The compute is about 3.4 times faster on the mini** (job median 946 s against 3,213 s for
  the three hosted runs), and the whole job is shorter than the hosted iPhone UI lane alone.
- **The queue moved rather than vanished.** A dispatched hosted run starts at once; a hosted
  run for a pull request waited 11 to 18 minutes on 2026-10-21 (two runs). The mini has one
  slot, so its median wait is 15 minutes. Typical pull request: about 31 minutes on the mini
  (15 queued, 16 running) against about 60 to 75 hosted.
- **Hosted was red once in three.** Run 39845022875 failed
  `ReadingModeUITests.testTheEdgeSwipeClosesTheFindBarFirstAndThenLeavesReadingMode` on the
  iPad lane ("Find is unreachable from inside reading mode (#506)"); the same tests passed on
  the mini for the same code. One occurrence, not diagnosed; the mini was green on 28 of its
  29 jobs.
- **Not recorded:** the mini's load average at the start of each job (the design asked for
  it). The mini runs the agents' own builds too, so its numbers include that contention.

**When macOS or Xcode moves (27).** The Mac is held on macOS 26.7.1 and Xcode
26.6 until 27.2 at the earliest (house-network-ops#426, not before December).
When that upgrade is scheduled:

1. `gh variable set MAC_SELF_HOSTED --body off` before the upgrade.
2. After it, `service.sh status`. The agent survives an OS upgrade, because it
   lives in `~/Library/LaunchAgents`.
3. Run the § Recipes build-and-test recipe by hand.
4. `gh workflow run ios-ci.yml -f runner=self-hosted --ref main`, and compare it
   with a hosted run of the same commit. The hosted image may still be on Xcode
   26 while the Mac is on 27: that is a divergence to record, not to hide.
5. Turn the variable back `on`.

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
| GitHub Actions runner | `/Users/claude/actions-runner` | 2026-10-01, #614; standing (non-ephemeral) since 2026-10-21, LaunchAgent `ca.electricrv.megapdf.actions-runner`, logs in `~/Library/Logs/megapdf-runner/`, job-started hook in `~/actions-runner/megapdf/`. Runs as `claude`, so a job that reaches it can read the deploy key and the clones; the hook is what keeps strangers off it. Label `megapdf-ios`. See "CI on this machine" above. |

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
