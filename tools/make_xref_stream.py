#!/usr/bin/env python3
"""Give a PDF that has a classic cross-reference table a cross-reference *stream* instead,
by appending one that indexes every object the table already pointed at (#267).

Usage: python3 tools/make_xref_stream.py <in.pdf> <out.pdf>

PDFium's writer only ever produces a cross-reference stream when it saves *incrementally*
over a document that already had one (`is_incremental_ && parser_->IsXRefStream()`), so
testing that branch needs a source in that shape. The large fixtures
(tools/gen_large_fixtures.py) all carry a classic table, and converting a multi-GB one with
qpdf means rewriting every byte of it. This copies the body untouched and appends a new
revision whose trailer is a cross-reference stream: seconds and one extra copy, rather than
a full rewrite, and the object offsets are the ones the source already had.

The stream is written uncompressed with /W [1 8 2] -- eight offset bytes, so a reader that
cannot represent a file this size fails visibly rather than quietly.
"""
import os
import re
import shutil
import sys

CHUNK = 1 << 22


def classic_table(path):
    """(offsets by object number, trailer dictionary text) of the file's classic table."""
    size = os.path.getsize(path)
    with open(path, "rb") as f:
        f.seek(max(0, size - 2048))
        tail = f.read()
        at = tail.rfind(b"startxref")
        if at < 0:
            sys.exit("%s: no startxref in the last 2 KB" % path)
        start = int(re.match(rb"\s*(\d+)", tail[at + 9:]).group(1))

        f.seek(start)
        section = f.read(min(size - start, 1 << 26))
    if not section.startswith(b"xref"):
        sys.exit("%s: startxref does not name a classic table (it may already be a stream)" % path)

    offsets = {}
    pos = 4
    while True:
        m = re.compile(rb"\s*(\d+)\s+(\d+)\s*").match(section, pos)
        if m is None:
            break
        first, count = int(m.group(1)), int(m.group(2))
        pos = m.end()
        for k in range(count):
            entry = re.compile(rb"\s*(\d+)\s+(\d+)\s+([nf])").match(section, pos)
            if entry is None:
                sys.exit("%s: the table ends inside a subsection" % path)
            pos = entry.end()
            if entry.group(3) == b"n":
                offsets[first + k] = int(entry.group(1))
    t = section.find(b"trailer", pos)
    if t < 0:
        sys.exit("%s: no trailer after the table" % path)
    return offsets, section[t + 7:section.find(b"startxref", t)].strip()


def entry(kind, offset, gen):
    return bytes([kind]) + offset.to_bytes(8, "big") + gen.to_bytes(2, "big")


def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2
    src, dst = argv[1], argv[2]
    offsets, trailer = classic_table(src)

    shutil.copyfile(src, dst)
    at = os.path.getsize(dst)
    num = max(offsets) + 1

    runs, data = [], bytearray()
    data += entry(0, 0, 65535)
    runs.append((0, 1))
    for objnum in sorted(offsets):
        if runs and runs[-1][0] + runs[-1][1] == objnum:
            runs[-1] = (runs[-1][0], runs[-1][1] + 1)
        else:
            runs.append((objnum, 1))
        data += entry(1, offsets[objnum], 0)
    runs.append((num, 1))
    data += entry(1, at, 0)

    keep = b""
    for key in (b"/Root", b"/Info", b"/ID"):
        m = re.search(re.escape(key) + rb"\s*(\[[^\]]*\]|\d+\s+\d+\s+R)", trailer)
        if m:
            keep += b" " + key + b" " + m.group(1)
    index = b"[" + b" ".join(b"%d %d" % r for r in runs) + b"]"
    head = (b"%d 0 obj\n<< /Type /XRef /Size %d /W [1 8 2] /Index %s%s /Length %d >>\nstream\n"
            % (num, num + 1, index, keep, len(data)))
    with open(dst, "ab") as f:
        f.write(head)
        f.write(data)
        f.write(b"\nendstream\nendobj\nstartxref\n%d\n%%%%EOF\n" % at)

    print("%s: %d objects, cross-reference stream at %d, %.2f GiB"
          % (dst, len(offsets), at, os.path.getsize(dst) / 2 ** 30))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
