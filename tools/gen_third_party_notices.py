#!/usr/bin/env python3
"""Generate per-platform THIRD-PARTY-NOTICES.txt files.

One canonical generator so the four platforms never drift. License texts are
sourced from:

  * the vendored PDFium license sets, libs/pdfium/<flavor>/licenses/
  * libs/licenses/, the texts every other shipped component publishes —
    copied verbatim from its .nupkg or its upstream repository by
    tools/fetch_vendor_licenses.py, never transcribed (#176)
  * the repo's own Apache-2.0 LICENSE

Run from anywhere:

    /usr/bin/python3 tools/gen_third_party_notices.py

Outputs (overwritten in place, committed to git):
    src/MegaPDF.App/Assets/THIRD-PARTY-NOTICES.txt        (windows)
    android/app/src/main/assets/THIRD-PARTY-NOTICES.txt   (android)
    ios/MegaPDF/Resources/THIRD-PARTY-NOTICES.txt         (ios)
    src/MegaPDF.Avalonia/Assets/THIRD-PARTY-NOTICES.txt   (macos)

The macOS list is not a guess: every file in a self-contained osx-arm64 publish
was mapped back to its owning package through MegaPDF.deps.json, and
tools/audit_macos_components.py re-runs that mapping at build time so a new
dependency cannot ship without a notice. PACKAGES_MACOS below is what that
audit checks against.
"""

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

RULE = "=" * 72

VENDORED = ROOT / "libs" / "licenses"


def vendored(rel: str) -> str:
    """A licence text exactly as its publisher ships it (libs/licenses/MANIFEST.md)."""
    return (VENDORED / rel).read_text(encoding="utf-8").lstrip("\ufeff")

HEADER = f"""{RULE}
 Mega PDF — Third-Party Notices
{RULE}

Mega PDF
Copyright (c) 2026 ElectricRV.ca Corporation. All rights reserved.

Mega PDF is open source software, released under the Apache License,
Version 2.0. Source code: https://github.com/SlyWombat/MegaPDF

Special thanks to Mega Woman.

This application includes the third-party components listed below.
Each component's license text is reproduced in full in the sections
that follow, as required by the respective licenses.
"""

# Every component shipped in the macOS bundle, keyed by the NuGet package id
# that owns its files in MegaPDF.deps.json. Derived from a self-contained
# osx-arm64 publish, not from the .csproj: a transitive package that lands a DLL
# in the output ships just as surely as a direct one. tools/audit_macos_components.py
# fails the build if the publish output contains a package that is not here.
#
# Avalonia.Angle.Windows.Natives is deliberately absent: it is in deps.json but
# places no file in the macOS output, so nothing of it ships.
PACKAGES_MACOS = {
    "Avalonia": "Avalonia",
    "Avalonia.Desktop": "Avalonia",
    "Avalonia.FreeDesktop": "Avalonia",
    "Avalonia.Headless": "Avalonia",
    "Avalonia.Native": "Avalonia",
    "Avalonia.Remote.Protocol": "Avalonia",
    "Avalonia.Skia": "Avalonia",
    "Avalonia.Themes.Fluent": "Avalonia",
    "Avalonia.Win32": "Avalonia",
    "Avalonia.X11": "Avalonia",
    "MicroCom.Runtime": "MicroCom.Runtime",
    "Tmds.DBus.Protocol": "Tmds.DBus.Protocol",
    "SkiaSharp": "SkiaSharp and HarfBuzzSharp",
    "SkiaSharp.NativeAssets.macOS": "SkiaSharp and HarfBuzzSharp",
    "HarfBuzzSharp": "SkiaSharp and HarfBuzzSharp",
    "HarfBuzzSharp.NativeAssets.macOS": "SkiaSharp and HarfBuzzSharp",
    "CommunityToolkit.Mvvm": "CommunityToolkit.Mvvm",
    "System.IO.Pipelines": ".NET Runtime",
    "MegaPDF": "Mega PDF",
    "MegaPDF.Core": "Mega PDF",
}

# Files in the publish output that deps.json does not attribute, and what owns
# each: the apphost is the .NET runtime's, the two .json files are not code.
UNATTRIBUTED_MACOS = {
    "MegaPDF": ".NET Runtime",
    "libmegapdf_core.dylib": "Mega PDF",
    "libpdfium.dylib": "PDFium",
    "MegaPDF.deps.json": None,
    "MegaPDF.runtimeconfig.json": None,
}


def inventory(components: list[str]) -> str:
    """The index at the top: what ships, before the wall of licence text."""
    body = "\n".join(f"  * {c}" for c in components)
    return f"""

{RULE}
 Components included
{RULE}

{body}
"""


def section(title: str, body: str) -> str:
    return f"\n\n{RULE}\n {title}\n{RULE}\n\n{body.strip()}\n"


def pdfium_sections(flavor: str) -> str:
    """All license texts vendored with the PDFium prebuilt for a platform."""
    lic_dir = ROOT / "libs" / "pdfium" / flavor / "licenses"
    out = []
    for path in sorted(lic_dir.iterdir()):
        name = path.stem.replace("_", "-")
        title = "PDFium" if name == "pdfium" else f"{name} (bundled with PDFium)"
        out.append(section(title, path.read_text(encoding="utf-8", errors="replace")))
    return "".join(out)


def apache_section(components: str) -> str:
    return section(components, (ROOT / "LICENSE").read_text(encoding="utf-8"))


# SkiaSharp and HarfBuzzSharp come out of one repository and ship byte-identical
# LICENSE.txt and THIRD-PARTY-NOTICES.txt (libs/licenses/MANIFEST.md records both
# hashes), so one pair of sections covers the two.
def skia_sections() -> str:
    return (
        section("SkiaSharp and HarfBuzzSharp — MIT License",
                vendored("skiasharp/LICENSE.txt"))
        + section("Third-party components within SkiaSharp and HarfBuzzSharp",
                  vendored("skiasharp/THIRD-PARTY-NOTICES.txt"))
    )


def dotnet_sections() -> str:
    return (
        section(".NET Runtime and .NET libraries — MIT License",
                vendored("dotnet-runtime/LICENSE.TXT"))
        + section("Third-party components within the .NET Runtime",
                  vendored("dotnet-runtime/THIRD-PARTY-NOTICES.TXT"))
    )


def mvvm_sections() -> str:
    return (
        section("CommunityToolkit.Mvvm — MIT License",
                vendored("communitytoolkit.mvvm/License.md"))
        + section("Third-party components within CommunityToolkit.Mvvm",
                  vendored("communitytoolkit.mvvm/ThirdPartyNotices.txt"))
    )


MACOS_COMPONENTS = [
    "Mega PDF (this application) — Apache License 2.0",
    "Avalonia, including Avalonia.Native — MIT License",
    "MicroCom.Runtime (a dependency of Avalonia) — MIT License",
    "Tmds.DBus.Protocol (a dependency of Avalonia) — MIT License",
    "SkiaSharp and libSkiaSharp — MIT License",
    "HarfBuzzSharp and libHarfBuzzSharp — MIT License",
    "CommunityToolkit.Mvvm — MIT License",
    "The .NET Runtime and .NET libraries — MIT License",
    "PDFium, and the components built into it — BSD, MIT and other licenses",
]

WINDOWS_COMPONENTS = [
    "Mega PDF (this application) — Apache License 2.0",
    "Windows App SDK and WinUI 3 — Microsoft Software License Terms",
    "CommunityToolkit.Mvvm — MIT License",
    "The .NET Runtime and .NET libraries — MIT License",
    "PDFium, and the components built into it — BSD, MIT and other licenses",
]

ANDROID_COMPONENTS = [
    "Mega PDF (this application) — Apache License 2.0",
    "AndroidX and Jetpack Compose — Apache License 2.0",
    "Material Components for Android — Apache License 2.0",
    "The Kotlin standard library, kotlinx.coroutines and kotlinx.serialization"
    " — Apache License 2.0",
    "PDFium, and the components built into it — BSD, MIT and other licenses",
]

IOS_COMPONENTS = [
    "Mega PDF (this application) — Apache License 2.0",
    "PDFium, and the components built into it — BSD, MIT and other licenses",
]


PLATFORMS = {
    "windows": {
        "out": ROOT / "src/MegaPDF.App/Assets/THIRD-PARTY-NOTICES.txt",
        "body": lambda: inventory(WINDOWS_COMPONENTS)
        + apache_section("Mega PDF — Apache License 2.0")
        # Not MIT. The Windows App SDK redistributable is licensed under
        # Microsoft's own terms and carries a NOTICE.txt that its licence
        # requires to travel with it; both are reproduced (#176).
        + section("Windows App SDK / WinUI 3 — Microsoft Software License Terms",
                  vendored("windowsappsdk/license.txt"))
        + section("Third-party notices for the Windows App SDK",
                  vendored("windowsappsdk/NOTICE.txt"))
        + mvvm_sections()
        + dotnet_sections()
        + pdfium_sections("win-x64"),
    },
    "android": {
        "out": ROOT / "android/app/src/main/assets/THIRD-PARTY-NOTICES.txt",
        "body": lambda: inventory(ANDROID_COMPONENTS)
        + apache_section(
            "Mega PDF, AndroidX / Jetpack Compose, Kotlin, kotlinx.coroutines,"
            " kotlinx.serialization, Material Components — Apache License 2.0"
        )
        + pdfium_sections("android"),
    },
    "ios": {
        "out": ROOT / "ios/MegaPDF/Resources/THIRD-PARTY-NOTICES.txt",
        "body": lambda: inventory(IOS_COMPONENTS)
        + apache_section("Mega PDF — Apache License 2.0")
        + pdfium_sections("android"),  # same bblanchon release/license set as iOS
    },
    "macos": {
        "out": ROOT / "src/MegaPDF.Avalonia/Assets/THIRD-PARTY-NOTICES.txt",
        "body": lambda: inventory(MACOS_COMPONENTS)
        + apache_section("Mega PDF — Apache License 2.0")
        + section("Avalonia — MIT License", vendored("avalonia/licence.md"))
        + section("MicroCom.Runtime — MIT License", vendored("microcom.runtime/LICENSE"))
        + section("Tmds.DBus.Protocol — MIT License", vendored("tmds.dbus.protocol/COPYING"))
        + skia_sections()
        + mvvm_sections()
        + dotnet_sections()
        # The macOS PDFium tarball ships the same fifteen licences under the same
        # names; its copies differ only in line endings and, for pdfium.txt, a
        # leading "// " on every line (checked 2026-09-17). The win-x64 set is
        # used because it is the one committed to this repo — libs/pdfium/mac-univ
        # is fetched and gitignored, so CI could not read it.
        + pdfium_sections("win-x64"),
    },
}


def main() -> None:
    for name, spec in PLATFORMS.items():
        text = HEADER + spec["body"]()
        spec["out"].write_text(text, encoding="utf-8", newline="\n")
        print(f"{name}: wrote {spec['out'].relative_to(ROOT)} ({len(text):,} chars)")


if __name__ == "__main__":
    main()
