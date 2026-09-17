#!/usr/bin/env python3
"""Vendor the licence texts of everything the apps ship into libs/licenses/.

Why this exists (#176). `tools/gen_third_party_notices.py` must run on a Linux
CI box with no NuGet cache and no network, so it cannot read a licence out of
`~/.nuget/packages/...` at generation time. And a licence text must never be
typed from memory — a copyright line that is subtly wrong is worse than a
missing file. So the texts are fetched once, from the package or the source
tree that actually ships them, committed, and checked for drift afterwards.

Every entry below names exactly where its text came from:

  * `pkg` — the file is inside the `.nupkg` on nuget.org, at `path`. That is
    byte-for-byte the package the app links: the hashes here were confirmed
    against the Mac mini's own `~/.nuget/packages` copies (2026-09-17).
  * `url` — the package ships no licence file, only an SPDX expression in its
    `.nuspec`. The text then comes from the upstream repository, pinned to the
    exact commit that `.nuspec`'s `<repository commit="...">` records.

Usage:
    python3 tools/fetch_vendor_licenses.py            # fetch and write
    python3 tools/fetch_vendor_licenses.py --check    # verify what is committed

`--check` needs no network: it re-hashes the committed files against
libs/licenses/MANIFEST.md. Without `--check` the files are re-downloaded and
the manifest is rewritten, which is how a version bump is done.
"""

import argparse
import hashlib
import io
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEST = ROOT / "libs" / "licenses"
MANIFEST = DEST / "MANIFEST.md"

NUGET = "https://api.nuget.org/v3-flatcontainer/{id}/{ver}/{id}.{ver}.nupkg"

# (slug, filename, source, note)
#   source is ("pkg", package-id, version, path-in-nupkg)
#          or ("url", url)
ENTRIES = [
    ("avalonia", "licence.md",
     ("url", "https://raw.githubusercontent.com/AvaloniaUI/Avalonia/"
             "595be4757eee59286d845b78e5a33f1546c35d66/licence.md"),
     "Avalonia 11.2.8 ships no licence file, only `<license type=\"expression\">MIT</license>`."
     " Taken from the commit its .nuspec records as the build source."),

    ("microcom.runtime", "LICENSE",
     ("url", "https://raw.githubusercontent.com/kekekeks/MicroCom/"
             "76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE"),
     "MicroCom.Runtime 0.11.0 ships no licence file and its .nuspec records no commit;"
     " the repository is untagged. Pinned to master as of 2026-09-17."),

    ("tmds.dbus.protocol", "COPYING",
     ("url", "https://raw.githubusercontent.com/tmds/Tmds.DBus/"
             "db7af5a46d316dda9886663fd3a006f6199343e1/COPYING"),
     "Tmds.DBus.Protocol 0.20.0 ships no licence file. Taken from the commit its"
     " .nuspec records; the file is named COPYING, not LICENSE."),

    ("skiasharp", "LICENSE.txt", ("pkg", "skiasharp", "2.88.9", "LICENSE.txt"),
     "Identical in HarfBuzzSharp 7.3.0.3 (same repository, same release):"
     " one copy covers both."),
    ("skiasharp", "THIRD-PARTY-NOTICES.txt",
     ("pkg", "skiasharp", "2.88.9", "THIRD-PARTY-NOTICES.txt"),
     "The notices SkiaSharp ships for the third-party code compiled into"
     " libSkiaSharp/libHarfBuzzSharp. Identical in the HarfBuzzSharp package."),

    ("communitytoolkit.mvvm", "License.md",
     ("pkg", "communitytoolkit.mvvm", "8.2.2", "License.md"), ""),
    ("communitytoolkit.mvvm", "ThirdPartyNotices.txt",
     ("pkg", "communitytoolkit.mvvm", "8.2.2", "ThirdPartyNotices.txt"), ""),

    ("dotnet-runtime", "LICENSE.TXT",
     ("pkg", "microsoft.netcore.app.runtime.osx-arm64", "8.0.31", "LICENSE.TXT"),
     "The self-contained macOS publish carries 182 files from this runtime pack."
     " The same text ships in the win-x64 and osx-x64 packs."),
    ("dotnet-runtime", "THIRD-PARTY-NOTICES.TXT",
     ("pkg", "microsoft.netcore.app.runtime.osx-arm64", "8.0.31", "THIRD-PARTY-NOTICES.TXT"),
     "Covers the third-party code inside the .NET runtime itself."),

    ("windowsappsdk", "license.txt",
     ("pkg", "microsoft.windowsappsdk", "1.7.250606001", "license.txt"),
     "The Windows app only. NOT an MIT licence — the Windows App SDK ships under"
     " Microsoft Software License Terms, which the generated notices previously"
     " misstated as MIT (#176). MegaPDF.App floats on `1.7.*`; this file and the"
     " NOTICE.txt beside it are byte-identical across the released 1.7.x series"
     " (checked 1.7.250606001 against 1.7.260224002, 2026-09-17), so the pin here"
     " does not go stale when the float moves."),
    ("windowsappsdk", "NOTICE.txt",
     ("pkg", "microsoft.windowsappsdk", "1.7.250606001", "NOTICE.txt"),
     "The Windows App SDK's own third-party attributions. Its licence requires"
     " this file to travel with the redistributable, so it is reproduced too."),
]


def fetch(source) -> bytes:
    if source[0] == "url":
        with urllib.request.urlopen(source[1], timeout=120) as r:
            return r.read()
    _, pkg, ver, path = source
    url = NUGET.format(id=pkg.lower(), ver=ver.lower())
    with urllib.request.urlopen(url, timeout=300) as r:
        blob = r.read()
    with zipfile.ZipFile(io.BytesIO(blob)) as z:
        return z.read(path)


def describe(source) -> str:
    if source[0] == "url":
        return source[1]
    _, pkg, ver, path = source
    return f"`{pkg}` {ver} → `{path}` (nuget.org)"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true",
                    help="verify the committed files against MANIFEST.md, offline")
    args = ap.parse_args(argv)

    if args.check:
        wanted = {}
        for line in MANIFEST.read_text(encoding="utf-8").splitlines():
            if line.startswith("| `") and "` | `" in line:
                cells = [c.strip().strip("`") for c in line.strip("|").split("|")]
                wanted[cells[0]] = cells[1]
        bad = 0
        for rel, want in sorted(wanted.items()):
            path = DEST / rel
            if not path.exists():
                print(f"::error::missing vendored licence: libs/licenses/{rel}")
                bad += 1
                continue
            got = hashlib.sha256(path.read_bytes()).hexdigest()
            if got != want:
                print(f"::error::libs/licenses/{rel} changed: {got} != {want}")
                bad += 1
        on_disk = {p.relative_to(DEST).as_posix() for p in DEST.rglob("*") if p.is_file()}
        for extra in sorted(on_disk - set(wanted) - {"MANIFEST.md", "README.md"}):
            print(f"::error::libs/licenses/{extra} is not in MANIFEST.md")
            bad += 1
        if bad:
            return 1
        print(f"{len(wanted)} vendored licence files match MANIFEST.md")
        return 0

    rows = []
    for slug, name, source, note in ENTRIES:
        blob = fetch(source)
        # Normalise to LF so the committed text is stable across platforms; the
        # licence wording is untouched.
        blob = blob.replace(b"\r\n", b"\n")
        out = DEST / slug / name
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(blob)
        digest = hashlib.sha256(blob).hexdigest()
        rel = f"{slug}/{name}"
        rows.append((rel, digest, describe(source), note))
        print(f"{rel}: {len(blob):,} bytes")

    lines = [
        "# Vendored licence texts — provenance",
        "",
        "Generated by `tools/fetch_vendor_licenses.py`. Do not edit by hand.",
        "",
        "Each file below was copied verbatim from the package or source tree that",
        "ships it — never transcribed. CRLF was normalised to LF; nothing else was",
        "changed. `tools/gen_third_party_notices.py` reads these, and",
        "`tools/fetch_vendor_licenses.py --check` re-hashes them offline so a silent",
        "edit fails CI.",
        "",
        "| File | sha256 | Source |",
        "|---|---|---|",
    ]
    for rel, digest, src, _ in rows:
        lines.append(f"| `{rel}` | `{digest}` | {src} |")
    lines += ["", "## Notes", ""]
    for rel, _, _, note in rows:
        if note:
            lines.append(f"- **`{rel}`** — {note}")
    MANIFEST.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {MANIFEST.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
