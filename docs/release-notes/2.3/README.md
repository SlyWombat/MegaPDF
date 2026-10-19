# MegaPDF 2.3 — what's new, for Dave to approve

**Draft. Nothing here has been pasted into a console and no publishing tool was run.**
Dave set the shape on 2026-10-19: keep it short, name Find on scanned pages (#676), and
call everything else "various other fixes". No Windows caveat about the text-recognition
language.

| File | Channel | Field | Limit | Says |
|---|---|---|---|---|
| [`microsoft-store.md`](microsoft-store.md) | Microsoft Store | What's new in this version | 1500 | scanned pages + fixes |
| [`app-store.md`](app-store.md) | App Store (iPhone, iPad) | What's New in This Version | 4000 | scanned pages + fixes |
| [`mac-app-store.md`](mac-app-store.md) | Mac App Store | What's New in This Version | 4000 | scanned pages + fixes |
| [`google-play.md`](google-play.md) | Google Play | Release notes | 500 | scanned pages + fixes |
| [`linux.md`](linux.md) | Linux (AppStream) | `<release><description>` | none | fixes only: Linux has no recogniser |
| [`check_copy.py`](check_copy.py) | — | derives the France blocks, recounts, checks the limits | — | — |

```
python3 docs/release-notes/2.3/check_copy.py          # derive fr-FR, recount, check limits
python3 docs/release-notes/2.3/check_copy.py --check  # fail on drift
```

## English, the same on all four stores

> Find now searches scanned pages. MegaPDF reads them on your device, and nothing leaves it. Plus various other fixes.

## Still to do before release

- **French review.** The Canadian French is new; Fable reviews it before release, and the
  France block is derived from it.
- **The claim must still be true at release.** Find on scans is on `main` for Windows,
  macOS, iOS and Android (#676, read at 200 dpi). Linux is excluded on purpose.
- **Version strings** in `tools/gen_listing_copy.py` (`NOTES_VERSION`),
  `tools/msstore_submit.py` and `tools/asc_publish.py` are bumped at release time, per
  `docs/RELEASING.md`, not here.
