#!/usr/bin/env python3
"""Builds the landing pages' pictures (#845):

- the French and Spanish pages' screenshots, fr/img/ and es/img/, cut from a
  release's own localized captures (docs/qa/captures-<x.y>/), so each page shows
  the app, and the sample document, in its own language;
- each page's social card, the 1200 x 630 picture a chat app or social site shows
  when someone shares the link (og:image): social-card.png, fr/social-card.png,
  es/social-card.png.

Run: python3 website/make_page_images.py [--captures docs/qa/captures-2.4]

Everything comes from this repository: the captures, the site's English
screenshots, the app icon and the self-hosted Space Grotesk (#846), so the set can
be rebuilt after a release re-shoots its captures, with nothing fetched from
anywhere. Needs Pillow, and fontTools with brotli to read the woff2 font.
"""

import argparse
import io
import os

from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = os.path.join(HERE, "megapdf")
REPO = os.path.dirname(HERE)
W, H = 1200, 630
BG_TOP, BG_BOTTOM = (12, 18, 32), (16, 35, 59)
TEXT, ACCENT, MUTED = (226, 232, 240), (53, 182, 224), (169, 180, 204)


def font(size, weight):
    """Space Grotesk at a given weight, from the site's own woff2 (a variable font)."""
    tt = TTFont(os.path.join(SITE, "fonts", "space-grotesk-latin.woff2"))
    tt.flavor = None
    buf = io.BytesIO()
    tt.save(buf)
    buf.seek(0)
    f = ImageFont.truetype(buf, size)
    try:
        f.set_variation_by_axes([weight])
    except (OSError, AttributeError):
        pass
    return f


def rounded(img, radius):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, img.size[0] - 1, img.size[1] - 1), radius, fill=255)
    out = img.convert("RGBA")
    out.putalpha(mask)
    return out


def shadowed(canvas, img, xy, radius=28, offset=18):
    shadow = Image.new("RGBA", (img.size[0] + radius * 4, img.size[1] + radius * 4), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (radius * 2, radius * 2, radius * 2 + img.size[0], radius * 2 + img.size[1]), 18, fill=(0, 0, 0, 150))
    shadow = shadow.filter(ImageFilter.GaussianBlur(radius))
    canvas.alpha_composite(shadow, (xy[0] - radius * 2, xy[1] - radius * 2 + offset))
    canvas.alpha_composite(img, xy)


CARD = {
    "en": ("Someone sent", "you a PDF.", "Send it back", "done.",
           "Free · No account · Never leaves your device",
           "Windows · Mac · iPhone · iPad · Android · Linux"),
    # FR-REVIEW #845
    "fr": ("Quelqu'un vous a", "envoyé un PDF.", "Renvoyez-le,", "c'est réglé.",
           "Gratuit · Sans compte · Ne quitte jamais votre appareil",
           "Windows · Mac · iPhone · iPad · Android · Linux"),
    # ES-REVIEW #845
    "es": ("Alguien te mandó", "un PDF.", "Devuélvelo", "listo.",
           "Gratis · Sin cuenta · Nunca sale de tu dispositivo",
           "Windows · Mac · iPhone · iPad · Android · Linux"),
}


def card(lang):
    """The link preview for one language's page, from that page's own screenshots."""
    base = SITE if lang == "en" else os.path.join(SITE, lang)
    shots = {"en": ("shot-desktop.png", "screenshot-viewer.png")}.get(
        lang, ("img/shot-desktop.png", "img/screenshot-viewer.png"))
    lines = CARD[lang]
    card = Image.new("RGBA", (W, H))
    draw = ImageDraw.Draw(card)
    for y in range(H):
        t = y / (H - 1)
        draw.line([(0, y), (W, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)))

    # Right: the Mac window, with the phone in front of its corner.
    laptop = Image.open(os.path.join(base, shots[0])).convert("RGB")
    laptop = laptop.resize((560, round(560 * laptop.size[1] / laptop.size[0])), Image.LANCZOS)
    shadowed(card, rounded(laptop, 12), (590, 120))
    phone = Image.open(os.path.join(base, shots[1])).convert("RGB")
    phone = phone.resize((190, round(190 * phone.size[1] / phone.size[0])), Image.LANCZOS)
    shadowed(card, rounded(phone, 22), (1000, 210))

    # Left: the name, the promise, the platforms.
    icon = Image.open(os.path.join(SITE, "icon.png")).convert("RGBA").resize((64, 64), Image.LANCZOS)
    card.alpha_composite(rounded(icon, 14), (64, 64))
    draw = ImageDraw.Draw(card)
    draw.text((144, 74), "MegaPDF", font=font(40, 700), fill=TEXT)
    head = font(54 if lang == "en" else 50, 700)
    draw.text((64, 190), lines[0], font=head, fill=TEXT)
    draw.text((64, 252), lines[1], font=head, fill=TEXT)
    draw.text((64, 330), lines[2], font=head, fill=ACCENT)
    draw.text((64, 392), lines[3], font=head, fill=ACCENT)
    small = font(23 if lang == "en" else 21, 500)
    draw.text((64, 492), lines[4], font=small, fill=MUTED)
    draw.text((64, 530), lines[5], font=small, fill=MUTED)

    out = os.path.join(base, "social-card.png")
    card.convert("RGB").save(out, optimize=True)
    print(os.path.relpath(out, REPO), os.path.getsize(out), "bytes")


# Which capture each localized page picture comes from, and its size. The iPhone
# captures are 1320 x 2868 and the Mac's 1440 x 900, so these are exactly a third,
# a half and two-thirds, as for the English page (website/README.md).
SHOTS = [
    ("ios-screenshots/{cap}/listing/iphone-6_9-search.png", "screenshot-viewer.png", (660, 1434)),
    ("ios-screenshots/{cap}/listing/iphone-6_9-home.png", "shot-home.png", (440, 956)),
    ("ios-screenshots/{cap}/listing/iphone-6_9-text.png", "shot-text.png", (440, 956)),
    ("ios-screenshots/{cap}/listing/iphone-6_9-reading.png", "shot-signed.png", (440, 956)),
    ("ios-screenshots/{cap}/listing/iphone-6_9-sign.png", "shot-sign.png", (440, 956)),
    ("ios-screenshots/{cap}/listing/iphone-6_9-draw.png", "shot-draw.png", (440, 956)),
    ("ios-screenshots/{cap}/listing/iphone-6_9-pages.png", "shot-pages.png", (440, 956)),
    ("macos-screenshots/{cap}/light-06-redact.png", "shot-desktop.png", (960, 600)),
]
CAPTURE_LOCALE = {"fr": "fr-CA", "es": "es-419"}


def shots(lang, captures):
    out_dir = os.path.join(SITE, lang, "img")
    os.makedirs(out_dir, exist_ok=True)
    for src, name, size in SHOTS:
        path = os.path.join(captures, src.format(cap=CAPTURE_LOCALE[lang]))
        im = Image.open(path).convert("RGB")
        if im.size[0] * size[1] != im.size[1] * size[0]:
            raise SystemExit(f"{path} is {im.size}, not the shape of {size}")
        im.resize(size, Image.LANCZOS).save(os.path.join(out_dir, name), optimize=True)
        print(os.path.relpath(os.path.join(out_dir, name), REPO), "from", os.path.relpath(path, REPO))


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--captures", default=os.path.join(REPO, "docs", "qa", "captures-2.4"),
                    help="the release capture folder the French and Spanish pictures come from")
    args = ap.parse_args()
    for lang in ("fr", "es"):
        shots(lang, args.captures)
    for lang in ("en", "fr", "es"):
        card(lang)


if __name__ == "__main__":
    main()
