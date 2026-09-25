<p align="center">
  <img src="assets/branding/logo.svg" alt="MegaPDF" width="440">
</p>
<p align="center"><em>Open. Fix. Save. Done.</em></p>

A free, open-source, lightweight PDF editor for Windows 11 — built for people who find Acrobat too bloated and complex.

MegaPDF does four things exceptionally well and deliberately nothing else:

1. **Edit text** — click any text in the document and type, like editing a Word file.
2. **Check boxes** — click an empty square and it becomes a checked box.
3. **Apply signatures** — keep a small personal library of signature images; drag one onto the page.
4. **Save** — Save overwrites, Save As creates a copy. No export wizards, no "flatten" dialogs.

Also included, because long documents need it: **Find** (the toolbar button, or
`Ctrl+F`) searches the whole document — type and every match lights up, Enter
walks through them.

Also on the command line: `megapdf-cli extract file.pdf` writes the text;
`--format md` writes Markdown.

No account. No cloud. No subscription. All processing is local.

## Design

The full Software Design Document lives in [SDD.md](SDD.md) — product principles, feature specifications, architecture, and deployment strategy. Read it before contributing; the non-goals list is enforced.

## Tech stack

- C# / .NET 8, WinUI 3 (Windows App SDK), CommunityToolkit.Mvvm
- PDFium as the PDF engine, behind an `IPdfEngine` adapter, with an in-house interactive text-editing layer
- MSIX packaging (Microsoft Store, direct download, winget)

## Repository layout

```
SDD.md                     Software Design Document (start here)
core/                      The shared engine core in C++ (ADR-003)
src/MegaPDF.Core/          Engine adapter, services, edit operations (UI-independent)
src/MegaPDF.App/           WinUI 3 application (Windows)
src/MegaPDF.Avalonia/      The cross-platform app (macOS, Linux) — ADR-002
tests/MegaPDF.Core.Tests/  Unit tests (engine mocked)
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
the tooling are ready and wait in `tools/linux/flatpak/`. The Snap Store is the
other channel waiting on an account, tracked in #314.

## Installing

Windows builds ship through the **Microsoft Store** only:
https://apps.microsoft.com/detail/9PF4TRRH4M76 (x64 and ARM64; the Store signs the
package and handles updates). The Store package is built headlessly by the recipe in
`tools/Store-Submission.md`. There is no sideload installer and no in-app updater any
more — both were retired in September 2026 once the Store listing went live; the older
`.msix` assets on the GitHub releases page are historical and will not update.

## License

[Apache-2.0](LICENSE)
