#!/usr/bin/env python3
"""Extract the About window's app icon from the committed MegaPDF.icns (#176).

A Mac About box shows the app's icon, and the bundle's .icns is the one place
that icon already lives at the right metrics — Apple's margin and corner radius
are baked into it by tools/gen_macos_icon.py. Rendering assets/branding/icon.svg
again here would mean a second implementation of that geometry, and PIL, which
the macOS build box deliberately does not need.

So this pulls the 256x256 PNG (the `ic13` entry, 128pt at 2x) straight out of
the .icns with nothing but the standard library. The result is committed, like
the .icns itself; re-run it when the branding changes.

Usage: tools/gen_about_icon.py [out.png]
"""

import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ICNS = ROOT / "assets" / "branding" / "MegaPDF.icns"
DEFAULT_OUT = ROOT / "src" / "MegaPDF.Avalonia" / "Assets" / "AppIcon.png"

# 128pt @2x. Big enough that the 64pt the About window draws it at stays crisp
# on every Mac display, small enough not to carry 1024px into the assembly.
WANTED = b"ic13"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


def main(argv: list[str]) -> int:
    out = Path(argv[0]) if argv else DEFAULT_OUT
    data = ICNS.read_bytes()
    if data[:4] != b"icns":
        print(f"::error::{ICNS} is not an .icns file")
        return 1

    offset = 8
    while offset < len(data):
        kind = data[offset:offset + 4]
        length = struct.unpack(">I", data[offset + 4:offset + 8])[0]
        if length < 8:
            print(f"::error::malformed icns entry at byte {offset}")
            return 1
        if kind == WANTED:
            blob = data[offset + 8:offset + length]
            if not blob.startswith(PNG_MAGIC):
                print(f"::error::the {WANTED.decode()} entry is not a PNG")
                return 1
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(blob)
            print(f"{out.relative_to(ROOT)}: {len(blob):,} bytes from {WANTED.decode()}")
            return 0
        offset += length

    print(f"::error::{ICNS} has no {WANTED.decode()} entry — regenerate it with tools/gen_macos_icon.py")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
