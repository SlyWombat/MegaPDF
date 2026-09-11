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

## What is installed (2026-09-11)

| Piece | Where | Note |
|---|---|---|
| Xcode 26.6 | `/Applications/Xcode.app` | licence accepted; iOS 26.5 simulators, iPhone 17 line and the current iPads |
| Homebrew | `/opt/homebrew` | `xcodegen`, `ffmpeg` |
| .NET 8 SDK | `~/.dotnet` | per-user, from `dotnet-install.sh`; builds the Avalonia app |
| Repo | `~/Projects/MegaPDF` | clone over the read-write deploy key; `git pull` before a session |
| iOS PDFium | `~/Projects/MegaPDF/ios/Vendor` | `ios/scripts/fetch-pdfium.sh`, gitignored |
| Simulator build | `~/dd-ios` | derived data shared by the capture scripts |
| Mac app | `~/app-macos/MegaPDF.app` | `tools/build-macos-app.sh osx-arm64 ~/app-macos` |
| Captures | `~/captures/` | never committed; copy what is wanted into `artifacts/` locally |

## Recipes

All from `~/Projects/MegaPDF` on the Mac.

**Prove the builds** (what CI does, on real Apple silicon):

```
cd ios && xcodegen generate && xcodebuild test -project MegaPDF.xcodeproj -scheme MegaPDF \
  -destination 'platform=iOS Simulator,name=iPhone 17 Pro' -derivedDataPath ~/dd-ios CODE_SIGNING_ALLOWED=NO
tools/build-macos-app.sh osx-arm64 ~/app-macos
python3 tools/gen_test_fixtures.py ~/fixtures
~/app-macos/MegaPDF.app/Contents/MacOS/MegaPDF --render-check ~/fixtures/stamped.pdf
~/app-macos/MegaPDF.app/Contents/MacOS/MegaPDF --self-test ~/fixtures
dotnet test tests/MegaPDF.Core.Tests/MegaPDF.Core.Tests.csproj -c Release
```

**iOS preview videos**: `tools/ios-demo-video.sh <en|fr-CA|fr> [device] [label] [out]`
— see `docs/app-store-listing.md` § App preview videos for what the three
output files are for. Takes about three minutes per device.

**iOS listing screenshots**: `tools/ios-screenshots.sh <lang> [out]` — the CI
recipe, locally.

**macOS preview video**: `tools/macos-demo-video.sh ~/app-macos/MegaPDF.app ~/captures/macos/video 1440x900 [light|dark]`
— the app's `--story` mode renders a frame after each step (tick, tick,
sign, print the name, find ×3, done) and ffmpeg holds each for a couple of
seconds. Real window states, no pointer.

**macOS screenshots**: the app renders its own window, so no Screen Recording
permission is involved. `--window WxH` sets the size (1440x900 is a Mac App
Store size and the smallest at which the whole toolbar fits); `--theme dark`
forces dark; `--screenshot-state find|focus|mode` poses it. Fixtures must sit
inside the sandbox container:

```
C="$HOME/Library/Containers/com.megapdf.mac/Data"; mkdir -p "$C/tmp/fixtures"; cp ~/fixtures/*.pdf "$C/tmp/fixtures/"
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

**macOS preview video, recorded for real**: `tools/macos-record-demo.sh
[app] [out] [light|dark]` — cliclick drives the window while screencapture
records 1920x1080. It measures where the page is off a screenshot and maps
every click from PDF points, and seeds the signature library if empty. For
dark, switch Appearance first (`tell appearance preferences to set dark mode
to true`) and back after. Finding that clicks missed at every zoom but
fit-width was how the page-click bug in the Mac app was found and fixed.
