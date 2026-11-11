#!/usr/bin/env python3
"""Builds website/megapdf/social-card.png, the 1200 x 630 picture a chat app or social
site shows when someone shares a link to the site (og:image, #845).

Run: python3 website/make_social_card.py

Everything comes from this repository: the site's own screenshots, the app icon and
the self-hosted Space Grotesk (#846), so the card can be rebuilt after a release
re-shoots the screenshots, with nothing fetched from anywhere. Needs Pillow, and
fontTools with brotli to read the woff2 font.
"""

import io
import os

from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFilter, ImageFont

SITE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "megapdf")
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


def main():
    card = Image.new("RGBA", (W, H))
    draw = ImageDraw.Draw(card)
    for y in range(H):
        t = y / (H - 1)
        draw.line([(0, y), (W, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)))

    # Right: the Mac window, with the phone in front of its corner.
    laptop = Image.open(os.path.join(SITE, "shot-desktop.png")).convert("RGB")
    laptop = laptop.resize((560, round(560 * laptop.size[1] / laptop.size[0])), Image.LANCZOS)
    shadowed(card, rounded(laptop, 12), (590, 120))
    phone = Image.open(os.path.join(SITE, "screenshot-viewer.png")).convert("RGB")
    phone = phone.resize((190, round(190 * phone.size[1] / phone.size[0])), Image.LANCZOS)
    shadowed(card, rounded(phone, 22), (1000, 210))

    # Left: the name, the promise, the platforms.
    icon = Image.open(os.path.join(SITE, "icon.png")).convert("RGBA").resize((64, 64), Image.LANCZOS)
    card.alpha_composite(rounded(icon, 14), (64, 64))
    draw = ImageDraw.Draw(card)
    draw.text((144, 74), "MegaPDF", font=font(40, 700), fill=TEXT)
    head = font(54, 700)
    draw.text((64, 190), "Someone sent", font=head, fill=TEXT)
    draw.text((64, 252), "you a PDF.", font=head, fill=TEXT)
    draw.text((64, 330), "Send it back", font=head, fill=ACCENT)
    draw.text((64, 392), "done.", font=head, fill=ACCENT)
    small = font(23, 500)
    draw.text((64, 492), "Free · No account · Never leaves your device", font=small, fill=MUTED)
    draw.text((64, 530), "Windows · Mac · iPhone · iPad · Android · Linux", font=small, fill=MUTED)

    out = os.path.join(SITE, "social-card.png")
    card.convert("RGB").save(out, optimize=True)
    print(out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
