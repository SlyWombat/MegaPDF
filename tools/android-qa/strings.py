#!/usr/bin/env python3
"""The app's own string catalogue, read from res/values*/strings.xml.

The QA driver taps buttons by their label, and every label is translated, so the
driver needs the same catalogue the app has. Reading the XML keeps the two in
step: a renamed button breaks the run instead of silently missing a capture.
"""
from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
RES = ROOT / "android/app/src/main/res"

# Android's per-app locale tag -> the values-* folder that serves it.
FOLDERS = {
    "en": ["values"],
    "fr-CA": ["values-fr-rCA", "values-fr", "values"],
    "fr-FR": ["values-fr", "values"],
}


def _unescape(value: str) -> str:
    # Android string escaping: \' \" \\ \n, and &#160; already handled by the parser.
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), value)


def catalogue(lang: str) -> dict[str, str]:
    """Every string for `lang`, with the fallback chain Android itself applies."""
    merged: dict[str, str] = {}
    for folder in reversed(FOLDERS[lang]):
        path = RES / folder / "strings.xml"
        if not path.exists():
            continue
        for node in ET.parse(path).getroot().findall("string"):
            name = node.get("name")
            text = "".join(node.itertext())
            if name:
                merged[name] = _unescape(text)
    return merged


class Strings:
    def __init__(self, lang: str):
        self.lang = lang
        self._values = catalogue(lang)

    def __getitem__(self, name: str) -> str:
        return self._values[name]

    def get(self, name: str, default: str | None = None) -> str | None:
        return self._values.get(name, default)

    def format(self, name: str, *args) -> str:
        value = self._values[name]
        for index, arg in enumerate(args, start=1):
            value = re.sub(rf"%{index}\$[ds]", str(arg), value)
        return value


if __name__ == "__main__":
    import sys
    lang = sys.argv[1] if len(sys.argv) > 1 else "en"
    for key, value in sorted(catalogue(lang).items()):
        print(f"{key}\t{value}")
