#!/usr/bin/env python3
"""Generate per-platform THIRD-PARTY-NOTICES.txt files.

One canonical generator so the three platforms never drift. License texts are
sourced from the vendored PDFium license sets (libs/pdfium/<flavor>/licenses/)
plus the repo's own Apache-2.0 LICENSE. Run from anywhere:

    /usr/bin/python3 tools/gen_third_party_notices.py

Outputs (overwritten in place, committed to git):
    src/MegaPDF.App/Assets/THIRD-PARTY-NOTICES.txt      (windows)
    android/app/src/main/assets/THIRD-PARTY-NOTICES.txt (android)
    ios/MegaPDF/Resources/THIRD-PARTY-NOTICES.txt       (ios)
"""

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

RULE = "=" * 72

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

MIT_MICROSOFT = """MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
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


PLATFORMS = {
    "windows": {
        "out": ROOT / "src/MegaPDF.App/Assets/THIRD-PARTY-NOTICES.txt",
        "body": lambda: apache_section("Mega PDF — Apache License 2.0")
        + section("Windows App SDK / WinUI 3 and .NET Runtime (Microsoft)", MIT_MICROSOFT)
        + pdfium_sections("win-x64"),
    },
    "android": {
        "out": ROOT / "android/app/src/main/assets/THIRD-PARTY-NOTICES.txt",
        "body": lambda: apache_section(
            "Mega PDF, AndroidX / Jetpack Compose, Kotlin, Material Components"
            " — Apache License 2.0"
        )
        + pdfium_sections("android"),
    },
    "ios": {
        "out": ROOT / "ios/MegaPDF/Resources/THIRD-PARTY-NOTICES.txt",
        "body": lambda: apache_section("Mega PDF — Apache License 2.0")
        + pdfium_sections("android"),  # same bblanchon release/license set as iOS
    },
}


def main() -> None:
    for name, spec in PLATFORMS.items():
        text = HEADER + spec["body"]()
        spec["out"].write_text(text, encoding="utf-8", newline="\n")
        print(f"{name}: wrote {spec['out'].relative_to(ROOT)} ({len(text):,} chars)")


if __name__ == "__main__":
    main()
