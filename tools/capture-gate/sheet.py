"""The contact sheet: one page, every image, the checks printed beside it.

Self-contained on purpose. The thumbnails are inlined, so the page can be
mailed, dropped in a chat or opened on a phone on the train with no server and
no network; the full-size image is a relative link, which works when the page
sits beside the set and simply does not open when it does not — the sheet
itself still reads.

No framework, no build step, and the only script is the filter that hides the
images the tool has cleared, because the whole point is to leave a short list.
"""
from __future__ import annotations

import datetime
import html
import os

CSS = """
:root { color-scheme: light dark; --pass:#137a3d; --flag:#b3261e; --skip:#6b6b6b; }
* { box-sizing: border-box; }
body { margin:0; padding:16px 14px 64px; font:15px/1.45 -apple-system,
       "Segoe UI", Roboto, system-ui, sans-serif; }
h1 { font-size:20px; margin:0 0 4px; }
h2 { font-size:17px; margin:28px 0 8px; padding-top:10px;
     border-top:1px solid #8884; }
.meta { color:#6b6b6b; font-size:13px; margin:0 0 10px; }
.verdict { border:1px solid #8884; border-radius:10px; padding:10px 12px;
           margin:10px 0 4px; }
.verdict b { font-size:15px; }
.grid { display:grid; gap:14px;
        grid-template-columns:repeat(auto-fill, minmax(280px, 1fr)); }
.card { border:1px solid #8884; border-radius:10px; overflow:hidden;
        background:#fff1; }
.card.flag { border-color:var(--flag); }
.card img { width:100%; display:block; background:#7773; }
.cap { padding:8px 10px 10px; }
.cap .t { font-weight:600; }
.cap .s { color:#6b6b6b; font-size:13px; }
ul.f { list-style:none; margin:8px 0 0; padding:0; font-size:13px; }
ul.f li { margin:3px 0; padding-left:16px; text-indent:-16px; }
li.pass::before { content:"✓ "; color:var(--pass); }
li.flag::before { content:"! "; color:var(--flag); font-weight:700; }
li.skip::before { content:"– "; color:var(--skip); }
li.flag, span.flag { color:var(--flag); }
li.skip { color:var(--skip); }
span.pass { color:var(--pass); }
button { font:inherit; padding:7px 12px; border-radius:8px;
         border:1px solid #8886; background:#8881; cursor:pointer; }
footer { margin-top:32px; color:#6b6b6b; font-size:13px; }
"""

SCRIPT = """
function onlyFlagged(btn) {
  const on = document.body.classList.toggle('filtered');
  document.querySelectorAll('.card').forEach(c => {
    c.style.display = (on && !c.classList.contains('flag')) ? 'none' : '';
  });
  btn.textContent = on ? 'Show every image' : 'Show only what needs an eye';
}
"""


def _findings(shot) -> str:
    out = []
    for finding in shot.findings:
        out.append(f'<li class="{finding.status}">'
                   f'<b>{html.escape(finding.check)}</b> — '
                   f'{html.escape(finding.note)}</li>')
    return "\n".join(out)


def write(path: str, result: dict, shots: list, thumbs: dict,
          profile: dict) -> None:
    out_dir = os.path.dirname(os.path.abspath(path))
    parts = [
        "<!doctype html><html><head><meta charset='utf-8'>",
        "<meta name='viewport' content='width=device-width, initial-scale=1'>",
        f"<title>{html.escape(result['title'])} — capture gate</title>",
        f"<style>{CSS}</style></head><body>",
        f"<h1>{html.escape(result['title'])} — capture gate</h1>",
        f"<p class='meta'>{html.escape(result['root'])}<br>"
        f"{datetime.date.today().isoformat()} · "
        f"{sum(s['images'] for s in result['sets'])} images · "
        f"{'text checks ran (tesseract)' if result['ocr'] else 'text checks stood down — no tesseract'}"
        "</p>",
        "<button onclick='onlyFlagged(this)'>Show only what needs an eye</button>",
    ]

    for entry in result["sets"]:
        flagged = entry["questionable"]
        line = (f"<b>{html.escape(entry['language'])}</b> — {entry['images']} "
                f"images, {entry['passed']} checks passed, "
                f"{entry['skipped']} not run. ")
        if flagged:
            line += (f"<span class='flag'>{len(flagged)} to look at: "
                     + ", ".join(html.escape(i["name"]) for i in flagged)
                     + "</span>")
        else:
            line += "<span class='pass'>nothing questionable.</span>"
        parts.append(f"<div class='verdict'>{line}</div>")

    if result.get("pairs"):
        rows = "; ".join(
            f"<b>{html.escape(p['a'])}</b> and <b>{html.escape(p['b'])}</b> are "
            f"the same image in {p['same']} of {p['of']} poses"
            for p in result["pairs"] if p["same"])
        if rows:
            parts.append(f"<div class='verdict'>{rows}. Correct when the demo "
                         "person is the only string that differs between two "
                         "catalogues on a listing screen — and worth knowing "
                         "before three listings go up looking alike.</div>")

    if result.get("constants"):
        names = ", ".join(html.escape(c["name"]) for c in result["constants"])
        parts.append(
            "<div class='verdict'><b>Look at these by eye, whatever the checks "
            f"say: {names}.</b> Every check here compares an image with "
            "something — a slot size, the same pose in another language, a set "
            "you already signed off. Anything that is the same in <i>every</i> "
            "image of a set has nothing to be compared against: device chrome, "
            "a launcher taskbar, a wallpaper edge, a watermark. One image per "
            "device is where those live.</div>")

    current = None
    for shot in shots:
        if shot.lang != current:
            if current is not None:
                parts.append("</div>")
            current = shot.lang
            parts.append(f"<h2>{html.escape(str(current))}</h2><div class='grid'>")
        try:
            href = os.path.relpath(shot.path, out_dir)
        except ValueError:                       # different drive, on Windows
            href = shot.path
        slot = shot.device or "desktop"
        parts.append(
            f"<div class='card {shot.verdict}'>"
            f"<a href='{html.escape(href)}'>"
            f"<img loading='lazy' src='{thumbs[shot.path]}' "
            f"alt='{html.escape(shot.name)}'></a>"
            f"<div class='cap'><div class='t'>{html.escape(shot.pose)}</div>"
            f"<div class='s'>{html.escape(slot)} · "
            f"{shot.size[0]}×{shot.size[1]} · {html.escape(shot.name)}</div>"
            f"<ul class='f'>{_findings(shot)}</ul></div></div>")
    if current is not None:
        parts.append("</div>")

    parts.append(
        "<footer><p><b>What this sheet cannot tell you.</b> Whether the set "
        "looks inviting; whether the pose tells the story the listing copy "
        "promises; whether the French reads like French. Those are the reasons "
        "a person still opens the images — but now only the ones above.</p>"
        f"<p>{html.escape(profile.get('notes', ''))}</p></footer>"
        f"<script>{SCRIPT}</script></body></html>")

    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(parts))
