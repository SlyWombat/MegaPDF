#!/usr/bin/env python3
"""Check the macOS bundle ships nothing that has no third-party notice (#176).

The Mac shipped for months with no THIRD-PARTY-NOTICES.txt at all, and the way
that happens again is a new transitive package quietly landing a DLL in the
publish output. So the component list is not maintained by hand and hoped over:
it is re-derived here from MegaPDF.deps.json every time the app is built, and
compared against PACKAGES_MACOS in tools/gen_third_party_notices.py.

Two checks, both fatal:

  1. Every library that contributes a runtime, native or resource asset under
     the osx RID target of deps.json is a component the notices cover. A package
     present in deps.json but contributing no asset for this RID — such as
     Avalonia.Angle.Windows.Natives — ships nothing and is ignored.
  2. If a built .app is given: every file in Contents/MacOS is attributable, and
     Contents/Resources/THIRD-PARTY-NOTICES.txt exists and is byte-identical to
     the generated src/MegaPDF.Avalonia/Assets/THIRD-PARTY-NOTICES.txt.

Usage:
    python3 tools/audit_macos_components.py <deps.json> [MegaPDF.app]
"""

import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))

from gen_third_party_notices import PACKAGES_MACOS, PLATFORMS, UNATTRIBUTED_MACOS  # noqa: E402

RUNTIME_PACK = "runtimepack.Microsoft.NETCore.App.Runtime."


def shipping_libraries(deps: Path) -> dict[str, list[str]]:
    """{package id: [file names it contributes]} for the osx RID target."""
    data = json.loads(deps.read_text(encoding="utf-8"))
    rid_targets = [k for k in data["targets"] if "/osx-" in k]
    if not rid_targets:
        raise SystemExit(f"::error::{deps} has no osx RID target — was it published with -r osx-arm64?")
    target = data["targets"][rid_targets[0]]
    out = {}
    for lib, info in target.items():
        files = [
            Path(f).name
            for section in ("runtime", "native", "resources")
            for f in info.get(section, {})
        ]
        if files:
            out[lib.split("/")[0]] = files
    return out


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    deps = Path(argv[0])
    if deps.is_dir():
        found = sorted(deps.glob("*.deps.json"))
        if not found:
            print(f"::error::no .deps.json in {deps}")
            return 1
        deps = found[0]

    libraries = shipping_libraries(deps)
    problems = []
    covered_files = {}
    for lib, files in sorted(libraries.items()):
        if lib.startswith(RUNTIME_PACK):
            component = ".NET Runtime"
        elif lib in PACKAGES_MACOS:
            component = PACKAGES_MACOS[lib]
        else:
            problems.append(
                f"::error::{lib} ships {len(files)} file(s) in the macOS bundle but has no"
                " third-party notice. Add its licence text with"
                " tools/fetch_vendor_licenses.py and list it in PACKAGES_MACOS."
            )
            continue
        for name in files:
            covered_files[name] = component

    stale = sorted(set(PACKAGES_MACOS) - set(libraries))
    if stale:
        problems.append(
            "::error::PACKAGES_MACOS lists package(s) the macOS publish no longer ships: "
            + ", ".join(stale)
        )

    print(f"{len(libraries)} shipping libraries in {deps.name}, "
          f"{len(covered_files)} files, all mapped to a notice")

    if len(argv) > 1:
        app = Path(argv[1])
        macos_dir = app / "Contents" / "MacOS"
        if not macos_dir.is_dir():
            print(f"::error::{app} has no Contents/MacOS")
            return 1
        for path in sorted(macos_dir.iterdir()):
            name = path.name
            if name in covered_files or name in UNATTRIBUTED_MACOS:
                continue
            problems.append(
                f"::error::{name} is in the bundle but attributable to no component."
                " Add it to UNATTRIBUTED_MACOS with what owns it, or give its package a notice."
            )

        bundled = app / "Contents" / "Resources" / "THIRD-PARTY-NOTICES.txt"
        generated = PLATFORMS["macos"]["out"]
        if not bundled.is_file():
            problems.append(
                "::error::THIRD-PARTY-NOTICES.txt is not in Contents/Resources."
                " Avalonia, SkiaSharp, HarfBuzzSharp and PDFium all require their notices"
                " to travel with the binary (#176)."
            )
        else:
            got = hashlib.sha256(bundled.read_bytes()).hexdigest()
            want = hashlib.sha256(generated.read_bytes()).hexdigest()
            if got != want:
                problems.append(
                    f"::error::the bundled notices differ from {generated.relative_to(ROOT)}"
                    f" ({got[:16]} != {want[:16]}). Re-run tools/gen_third_party_notices.py."
                )
            else:
                print(f"bundled notices match the generated file ({bundled.stat().st_size:,} bytes)")

    for line in problems:
        print(line, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
