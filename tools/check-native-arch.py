#!/usr/bin/env python3
"""Fails if any binary in a Windows package is built for a different architecture (#288).

    tools/check-native-arch.py arm64 MegaPDF.App_2.0.0.0_arm64.msix
    tools/check-native-arch.py x64   src/MegaPDF.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64

The Store's ARM64 package shipped from 1.7 to 2.0 with an x64 pdfium.dll (and, from 2.0,
an x64 megapdf_core.dll) that an ARM64 process can't load, so every document failed to
open on an ARM64 PC. Nothing noticed, because nothing looked. This reads every .dll
and .exe in an .msix/.msixbundle/.appx (a zip) or in a directory, and checks the PE
header's machine field:

- native code must be the package's architecture (ARM64X counts as arm64);
- a managed assembly may be AnyCPU IL (the i386 header with ILONLY and without
  32BITREQUIRED); anything else is architecture-specific and must match too;
- `runtimes/win-<rid>/` folders for another architecture are skipped: an unpackaged
  build carries them from NuGet, and .NET only probes the one for its own RID;
- the one exception is the Windows App SDK's own `*_ec.dll` in an x64 package: the
  ARM64EC companion it ships so an x64 app runs well on an ARM64 PC. Its header says
  arm64 on purpose.

Prints one line per binary and exits 1 on any mismatch, 2 if it found nothing to check.
"""
import io
import os
import struct
import sys
import zipfile

MACHINES = {0x014C: "x86", 0x8664: "x64", 0xAA64: "arm64", 0x01C4: "arm", 0xA641: "arm64ec"}
ACCEPT = {"x64": {"x64"}, "arm64": {"arm64"}}
COMIMAGE_ILONLY = 0x1
COMIMAGE_32BITREQUIRED = 0x2


def classify(data):
    """(machine name, managed kind or None). managed kind is 'anycpu' or 'specific'."""
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None, None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return None, None
    machine = struct.unpack_from("<H", data, pe + 4)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    # Data directory 14 is the CLR runtime header.
    dirs = opt + (96 if magic == 0x10B else 112)
    clr_rva, clr_size = struct.unpack_from("<II", data, dirs + 14 * 8)
    name = MACHINES.get(machine, hex(machine))
    if clr_size == 0:
        return name, None
    flags = clr_flags(data, pe, clr_rva)
    if machine == 0x014C and flags is not None and flags & COMIMAGE_ILONLY and not flags & COMIMAGE_32BITREQUIRED:
        return name, "anycpu"
    return name, "specific"


def clr_flags(data, pe, rva):
    sections = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    table = pe + 24 + opt_size
    for i in range(sections):
        s = table + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, s + 8)
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            off = rawptr + (rva - vaddr)
            return struct.unpack_from("<I", data, off + 16)[0]
    return None


def binaries(path):
    if os.path.isdir(path):
        for root, _, files in os.walk(path):
            for f in files:
                if f.lower().endswith((".dll", ".exe")):
                    full = os.path.join(root, f)
                    with open(full, "rb") as fh:
                        yield os.path.relpath(full, path), fh.read()
        return
    with zipfile.ZipFile(path) as z:
        for info in z.infolist():
            low = info.filename.lower()
            if low.endswith((".msix", ".appx")):
                # A bundle: check each package inside it against its own architecture's
                # rule would need its manifest; the caller passes one package at a time.
                inner = zipfile.ZipFile(io.BytesIO(z.read(info)))
                for sub in inner.infolist():
                    if sub.filename.lower().endswith((".dll", ".exe")):
                        yield f"{info.filename}/{sub.filename}", inner.read(sub)
            elif low.endswith((".dll", ".exe")):
                yield info.filename, z.read(info)


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ACCEPT:
        print(__doc__.strip().split("\n\n")[1], file=sys.stderr)
        return 2
    arch, path = sys.argv[1], sys.argv[2]
    bad = checked = 0
    for name, data in binaries(path):
        parts = name.replace("\\", "/").lower().split("/")
        if "runtimes" in parts[:-1]:
            rid = parts[parts.index("runtimes") + 1]
            if rid.startswith("win-") and rid != f"win-{arch}":
                continue
        machine, managed = classify(data)
        if machine is None:
            continue
        checked += 1
        ok = managed == "anycpu" or machine in ACCEPT[arch]
        kind = {"anycpu": "managed AnyCPU", "specific": "managed"}.get(managed, "native")
        if not ok and arch == "x64" and machine == "arm64" and managed is None \
                and os.path.basename(name).lower().endswith("_ec.dll"):
            ok, kind = True, "ARM64EC companion"
        print(f"{'ok ' if ok else 'BAD'}  {machine:8} {kind:15} {name}")
        bad += not ok
    if checked == 0:
        print(f"no PE binaries found in {path}", file=sys.stderr)
        return 2
    print(f"{checked} binaries, {bad} not {arch}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
