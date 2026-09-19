#!/usr/bin/env python3
"""Every in-use entry of a classic xref table: non-negative, and pointing at `N G obj`.

    xrefcheck.py <file.pdf>

Reads only the table and a few bytes at each offset, so a 2.5 GB file costs seconds.
Follows /Prev for incremental updates.
"""
import re
import sys


def main(path):
    with open(path, "rb") as f:
        f.seek(0, 2)
        size = f.tell()
        f.seek(max(0, size - 2048))
        tail = f.read()
        m = re.search(rb"startxref\s+(-?\d+)\s+%%EOF", tail)
        start = int(m.group(1))
        print(f"file {size:,} bytes; startxref {start:,}")
        total = bad = negative = 0
        seen = set()
        offset = start
        while offset is not None and offset not in seen:
            seen.add(offset)
            f.seek(offset)
            if f.read(4) != b"xref":
                print(f"  startxref {offset} does not point at 'xref'")
                return 1
            f.readline()
            while True:
                header = f.readline()
                if header.startswith(b"trailer"):
                    break
                first, count = map(int, header.split())
                for i in range(count):
                    entry = f.read(20)
                    num = first + i
                    off, gen, kind = int(entry[:10]), int(entry[11:16]), entry[17:18]
                    if kind != b"n":
                        continue
                    total += 1
                    if off < 0:
                        negative += 1
                        bad += 1
                        continue
                    pos = f.tell()
                    f.seek(off)
                    head = f.read(40)
                    f.seek(pos)
                    if not re.match(rb"\s*%d\s+%d\s+obj" % (num, gen), head):
                        bad += 1
                        if bad <= 5:
                            print(f"  object {num} {gen}: offset {off} holds {head[:24]!r}")
            trailer = f.read(512)
            p = re.search(rb"/Prev\s+(\d+)", trailer)
            offset = int(p.group(1)) if p else None
        print(f"in-use entries {total:,}; negative {negative}; not at 'N G obj' {bad}")
        return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
