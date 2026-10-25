<p align="center">
  <img src="assets/branding/logo.svg" alt="MegaPDF" width="440">
</p>
<p align="center"><em>Open. Fix. Save. Done.</em></p>

A free, open-source, lightweight PDF editor for Windows, Mac, Linux, iPhone/iPad
and Android — built for people who find Acrobat too bloated and complex.

MegaPDF does the one job most people actually have with a PDF: someone sent you
a form, and you need to send it back filled in, checked off, and signed. No
account. No cloud. No subscription. Everything happens on your device.

MegaPDF does eight things exceptionally well and deliberately nothing else:

1. **Read** — a reading mode that takes the toolbars off the screen and leaves
   the page, in Normal, Sepia or Night colours, on every platform.
2. **Edit text** — click any text in the document and type, like editing a Word file.
3. **Add text** — click a blank line and type on it, in the size and face that
   matches the form you're filling in.
4. **Redact** — mark what has to come out and MegaPDF takes it out of the file
   rather than covering it over.
5. **Check boxes** — click an empty square and it becomes a checked box.
6. **Apply signatures** — keep a small personal library of signature images; drag one onto the page.
7. **Save** — Save overwrites, Save As creates a copy. No export wizards, no "flatten" dialogs.
   Save As can also write the document's text as a **Markdown** file — a one-way export
   that leaves the PDF untouched.
8. **Pages** — rotate, delete, reorder, insert and extract pages — each a single undo step.

Also included, because long documents need it: **Find** (the toolbar button, or
`Ctrl+F`) searches the whole document — type and every match lights up, Enter
walks through them. Documents open as **tabs** in one window on Windows, the Mac
and Linux, and a PDF opened from the file manager joins the window you already have.

Also on the command line: `megapdf-cli extract file.pdf` writes the text;
`--format md` writes Markdown. It ships in every Linux package (on `PATH`), and
for Windows and macOS as a zip on the releases page (`windows-cli-v*`,
`macos-cli-v*`) — not inside any store package.

No account. No cloud. No subscription. All processing is local.

## Get it

**[electricrv.ca/megapdf](https://electricrv.ca/megapdf/)** has the store badges
for every platform plus the Linux install page. Store status as of 2.4.0 on
2026-10-25: **Linux** (APT repository, `.deb`, tarball, or `sudo snap install
megapdf`), **Google Play**, **Windows** (Microsoft Store), **Mac** (Mac App
Store) and **iOS** are all live on 2.4.0.

## Design

The full Software Design Document lives in [SDD.md](SDD.md) — product principles, feature specifications, architecture, and deployment strategy. Read it before contributing; the non-goals list is enforced.

## Tech stack

Five native apps over one engine. What MegaPDF does to a PDF (opening, rendering,
text editing, redaction, forms, page tools, search, extraction) is decided once, in a
shared C++ core, so a fix there reaches every platform. Each app is written in its own
platform's language and UI toolkit on top of it.

- **Engine: `core/`.** C++17 behind a C ABI (`core/megapdf_core.h`), over **our own
  build of PDFium, not upstream's.** We carry a patch series
  (`tools/pdfium/patches/`, each patch explained in `tools/pdfium/README.md`) that
  fixes how PDFium rewrites a page when it is edited, writes AES-256 with new
  passwords, and more. `libs/pdfium/RELEASE` pins the build every platform links. The
  number of patches in that build is compiled into the core as
  `MEGAPDF_PDFIUM_PATCHES`, and behaviour that needs a patch is gated on it. Do not
  assume upstream PDFium behaviour.
- **Windows:** C# / .NET 8, WinUI 3 (Windows App SDK), CommunityToolkit.Mvvm. x64 and
  ARM64, shipped as an MSIX through the Microsoft Store only.
- **Mac and Linux:** one app, `src/MegaPDF.Avalonia`. C# / .NET 8 and Avalonia,
  **one binary for both platforms**, with what differs between them under its
  `Platform/` folder. A change there lands on two platforms. The Mac build is for Apple
  silicon and goes through the Mac App Store. The Linux build (x64) ships as a `.deb`
  with a signed APT repository, a Snap, and a tarball.
- **Both desktop apps** reach the core through `src/MegaPDF.Core`: P/Invoke bindings,
  plus the services and edit operations the two apps share. There the core is a
  native library: `megapdf_core.dll`, `libmegapdf_core.dylib` or `libmegapdf_core.so`.
- **iPhone and iPad:** Swift and SwiftUI, iOS 16 and later. The core's sources compile
  into the app, and Swift calls the C ABI through a bridging header. The Xcode project
  is generated from `ios/project.yml` by XcodeGen.
- **Android:** Kotlin and Jetpack Compose (Material 3), Android 8.0 (API 26) and
  later. The core compiles into a JNI library in `android/engine`.
- **Command line:** `megapdf-cli` (`core/cli/`) extracts a PDF's text or Markdown
  with the same core.
- **Tests:** xUnit for `MegaPDF.Core`, a native test binary for the core
  (`core/tests/`), XCTest on iOS, JUnit and instrumented tests on Android, and each
  desktop app's own self-test (`--self-test` on Mac and Linux, `--screenshot-state` on
  Windows). Corpus batteries in `tools/stress/` run the core over thousands of real
  PDFs. `tests/matrix/coverage.toml` maps every feature to its proof on every
  platform, and CI fails a test file that no feature cites.

## Repository layout

```
core/                       The shared engine: C++17 over our patched PDFium (ADR-003)
  megapdf_core.h            Its C ABI, the one surface every platform calls
  cli/                      megapdf-cli, text and Markdown extraction
  tests/                    The core's native tests and their expected outputs
src/MegaPDF.Core/           .NET bindings to the core and services shared by both desktop apps
src/MegaPDF.App/            Windows app (WinUI 3)
src/MegaPDF.Avalonia/       Mac and Linux app (Avalonia), one binary for both (ADR-002)
ios/                        iPhone and iPad app (SwiftUI); project.yml generates the Xcode project
android/                    Android app (Compose): app/ is the UI, engine/ wraps the core over JNI
tests/MegaPDF.Core.Tests/   xUnit tests for MegaPDF.Core, against the real engine
tests/matrix/coverage.toml  Which test proves each feature on each platform; enforced in CI
libs/pdfium/                The pinned PDFium build (RELEASE) and its Windows and Android binaries
libs/licenses/              Third-party licence texts the apps ship
tools/                      Build, packaging, store submission, test fixtures and QA
  pdfium/                   The PDFium patch series and the script that builds it
  linux/                    .deb, APT repository, Snap and Flatpak packaging
  stress/                   Corpus batteries over real-world PDFs
  gen_strings.py            Localisation: fr-CA is written by hand, fr-FR is derived from it
docs/                       ADRs, the release runbook (RELEASING.md), the localisation glossary
website/                    electricrv.ca/megapdf: the site's source and its deploy script
assets/branding/            Logo, icon and splash artwork
.github/workflows/          CI for every platform, and the release pipelines
SDD.md                      Software Design Document (start here)
TESTING.md                  How to test MegaPDF, and the record of corpus battery runs
```

## Building

### Windows

Requires the .NET 8 SDK on Windows 11 (or Windows 10 1809+). No Visual Studio needed.

```
dotnet build MegaPDF.sln
dotnet test MegaPDF.sln
```

### Linux (#158)

The cross-platform app in `src/MegaPDF.Avalonia` builds and runs on Linux x64.
Tested on **Ubuntu 24.04 LTS**; **Fedora current** is the other target
distribution. `linux-arm64` is not buildable yet — the patched PDFium series has
no arm64 build.

Needs the .NET 8 SDK, CMake and a C++ toolchain. Ninja is used when present:

```
sudo apt install -y dotnet-sdk-8.0 cmake ninja-build g++ python3
tools/fetch-pdfium-linux.sh          # the pinned PDFium prebuilt
tools/build-linux-app.sh             # -> artifacts/linux/MegaPDF
artifacts/linux/MegaPDF/install.sh   # into ~/.local, no root
```

`install.sh` puts MegaPDF in the applications menu, offers it in a PDF's Open
With menu, installs the hicolor icons, and adds a `megapdf` command.
`uninstall.sh` takes it all back out and keeps your signatures and settings.

Two packages are worth having beside it, and neither is required:
`cups-client` for printing (`lp`), and `fonts-urw-base35` for a script face to
type signatures in — without it they are drawn in the body font.

The `.deb` and its signed APT repository are how Linux ships (see "Installing"
below); AppImage was decided against in favor of the `.deb` (`tools/Linux-Packaging.md`
says why). Flathub is on hold pending a Flathub account (#254 did the prep; no
open issue tracks the submission itself yet) — the manifest, its CI checks and
the tooling are ready and wait in `tools/linux/flatpak/`. The Snap Store is live on
its **stable** channel — `sudo snap install megapdf` needs no channel flag — since
2026-10-02 (#314).

## Installing

Windows builds ship through the **Microsoft Store** only:
https://apps.microsoft.com/detail/9PF4TRRH4M76 (x64 and ARM64; the Store signs the
package and handles updates). The Store package is built headlessly by the recipe in
`tools/Store-Submission.md`. There is no sideload installer and no in-app updater any
more — both were retired in September 2026 once the Store listing went live; the older
`.msix` assets on the GitHub releases page are historical and will not update.

## Releasing

A release goes to every channel at once, in a fixed order with a gate at each step.
[`docs/RELEASING.md`](docs/RELEASING.md) is the runbook — the version rule, the gates
and the command that proves each, the submission commands per store, and the traps —
and `.github/ISSUE_TEMPLATE/release-checklist.md` is the issue to open and work down.

## License

[Apache-2.0](LICENSE)
