#!/usr/bin/env python3
"""Fills in tools/linux/snap/snapcraft.yaml.in (#158).

    make-snapcraft-yaml.py <template> <metainfo.xml> <version> <out.yaml>

The listing text comes from the AppStream metainfo, the one place the Linux listing is
written, so the snap and the Flatpak cannot drift apart: the English <summary> and
<description>, flattened to the plain text the Snap Store shows (paragraphs, and "- "
for list items).

Except for one paragraph. The metainfo is the Flatpak's, and its last-but-one paragraph
says the package asks for no access to your files. The Flatpak doesn't, but the snap
plugs `home` (see snapcraft.yaml.in for why), so that sentence would be false here.
It is replaced by SNAP_PERMISSIONS, and the script refuses to run if it cannot find
the paragraph it expects to replace: a rewritten metainfo must be looked at, not
quietly half-copied.
"""
import re
import sys
import xml.etree.ElementTree as ET

FLATPAK_PARAGRAPH_START = "MegaPDF makes no network connection"

SNAP_PERMISSIONS = (
    "MegaPDF makes no network connection, and this snap asks for no network "
    "permission, so that is a fact about the package rather than a claim in a "
    "listing. It can open and save documents in your home folder, which is what lets "
    "a PDF you double-click in your file manager open in it; the file dialogs go "
    "through your desktop's file portal. Documents on a USB drive are yours to allow "
    "with `snap connect megapdf:removable-media`. Your documents and your signature "
    "never leave your computer, because there is no server for them to go to."
)

XML_LANG = "{http://www.w3.org/XML/1998/namespace}lang"


def text(el):
    return re.sub(r"\s+", " ", "".join(el.itertext())).strip()


def main():
    template, metainfo, version, out = sys.argv[1:5]
    root = ET.parse(metainfo).getroot()

    summaries = [s for s in root.findall("summary") if s.get(XML_LANG) is None]
    descriptions = [d for d in root.findall("description") if d.get(XML_LANG) is None]
    if len(summaries) != 1 or len(descriptions) != 1:
        sys.exit("expected one untranslated <summary> and <description> in the metainfo")
    summary = text(summaries[0])
    if len(summary) > 78:
        sys.exit(f"the summary is {len(summary)} characters; the Snap Store takes 78")

    blocks, replaced = [], 0
    for child in descriptions[0]:
        if child.get(XML_LANG) is not None:
            continue  # a translation of the paragraph before it
        if child.tag == "p":
            para = text(child)
            if para.startswith(FLATPAK_PARAGRAPH_START):
                para, replaced = SNAP_PERMISSIONS, replaced + 1
            blocks.append(para)
        elif child.tag in ("ul", "ol"):
            blocks.append("\n".join(f"- {text(li)}" for li in child
                                    if li.tag == "li" and li.get(XML_LANG) is None))
    if replaced != 1:
        sys.exit(f"found the Flatpak-only paragraph {replaced} times, expected once; "
                 "the metainfo changed, so check SNAP_PERMISSIONS against it")

    description = "\n\n".join(blocks)
    if len(description) > 4096:
        sys.exit(f"the description is {len(description)} characters; the Snap Store takes 4096")
    # Indented two spaces under `description: |`. Every line, including the blank ones,
    # so the block scalar is unambiguous.
    indented = "\n".join(("  " + line) if line else "" for line in description.split("\n"))

    yaml = open(template, encoding="utf-8").read()
    for key, value in (("@VERSION@", version),
                       ("@SUMMARY@", summary.replace("'", "''")),
                       ("@DESCRIPTION@", indented)):
        if yaml.count(key) != 1:
            sys.exit(f"{key} appears {yaml.count(key)} times in the template, expected once")
        yaml = yaml.replace(key, value)
    open(out, "w", encoding="utf-8").write(yaml)
    print(f"  snapcraft.yaml: version {version}, summary {len(summary)} chars, "
          f"description {len(description)} chars")


if __name__ == "__main__":
    main()
