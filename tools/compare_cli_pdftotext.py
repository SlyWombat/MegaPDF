#!/usr/bin/env python3
"""Informational-only comparison of megapdf-cli's plain-text output against `pdftotext`'s, over
#353's structure fixtures (#355, core-tests.yml's Linux leg only).

Design #142 §7 measure 3's own precedent applies here too: poppler is another heuristic, not
truth, so this never gates the build -- it only prints a token-overlap number per fixture so a
large divergence is visible in the CI log. The real fidelity gate (against PDFium's own text,
not against a second heuristic) is #354's corpus battery; this is a much smaller, always-on
sanity check over the seven fixtures core-tests.yml already has on hand.

    tools/compare_cli_pdftotext.py <megapdf-cli path> <repo root>
"""
import os
import re
import subprocess
import sys

NAMES = ["columns", "furniture", "lists", "headings", "xobject-text", "scan", "mixed"]


def tokens(text: str) -> set:
    return set(re.findall(r"[a-z0-9]+", text.lower()))


def main() -> int:
    if len(sys.argv) != 3:
        print(f"usage: {sys.argv[0]} <megapdf-cli path> <repo root>", file=sys.stderr)
        return 2
    cli, root = sys.argv[1], sys.argv[2]
    for name in NAMES:
        pdf = os.path.join(root, "tests", "MegaPDF.Core.Tests", "Fixtures", "structure", name + ".pdf")
        cli_out = subprocess.run([cli, "extract", pdf, "--quiet"], capture_output=True, text=True).stdout
        poppler_out = subprocess.run(["pdftotext", pdf, "-"], capture_output=True, text=True).stdout
        a, b = tokens(cli_out), tokens(poppler_out)
        union = len(a | b)
        overlap = (len(a & b) / union) if union else 1.0
        print(f"{name}: megapdf-cli {len(a)} tokens, pdftotext {len(b)} tokens, overlap {overlap:.3f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
