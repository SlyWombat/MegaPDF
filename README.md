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
src/MegaPDF.Core/          Engine adapter, services, edit operations (UI-independent)
src/MegaPDF.App/           WinUI 3 application
tests/MegaPDF.Core.Tests/  Unit tests (engine mocked)
```

## Building

Requires the .NET 8 SDK on Windows 11 (or Windows 10 1809+). No Visual Studio needed.

```
dotnet build MegaPDF.sln
dotnet test MegaPDF.sln
```

## Test installer (MSIX)

```
powershell -File tools\Build-Installer.ps1
```

This produces a signed, self-contained package under `artifacts\MegaPDF.App_<version>_x64_Test\`
along with the generated `Install.ps1`. To install: right-click `Install.ps1` → *Run with
PowerShell* (it trusts the dev certificate — one elevation prompt — then installs). The
installer registers MegaPDF as an available `.pdf` handler; uninstall cleanly via
Settings → Apps. Store releases will be signed with a real certificate instead (SDD §5).

## License

[Apache-2.0](LICENSE)
