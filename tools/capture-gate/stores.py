"""What each store's set has to be, in one place.

A profile says what the gate cannot work out by looking: the pixel sizes that
store accepts, which languages the listing is in and who the demo person is in
each, the order the images appear in on the listing, and the few facts about
the app's own chrome that turn "looks right" into a number — how tall the 2.0
toolbar is, what the zoom is pinned to, where the status bar lives.

Everything else — clipping, stray chrome, poses matching across languages — is
measured from the images themselves and needs no profile.

Adding a store is this file plus nothing else. Adding a *device* to a store is
one line in `slots`.
"""
from __future__ import annotations

import re

# The brand accent, from src/MegaPDF.Avalonia/Brand.axaml and
# src/MegaPDF.App/Themes/Brand.xaml. Selection borders, the armed-mode banner
# and toggled toolbar buttons are all painted in it, which is what makes "how
# much of it is in this image, and in what shape" worth measuring.
ACCENT = "#0e6fd8"

# The demo person, per listing language (#146 §3, docs/qa/android-store-captures.md).
# English keeps Jane Whitfield; each French listing shows a name from its own
# country, and the accented capitals are there to prove they render.
PEOPLE = {
    "en": "Jane Whitfield",
    "fr-CA": "Hélène Bélanger",
    "fr-FR": "Céline Lefèvre",
}

# Directory names a language set has been shot under. `fr` means fr-FR
# everywhere in this repo's capture folders; `fr-CA` is always spelled out.
LANG_DIRS = {
    "en": ("en", "en-US", "en-us"),
    "fr-CA": ("fr-CA", "fr-ca", "frCA"),
    "fr-FR": ("fr-FR", "fr-fr", "fr", "frFR"),
}


def _pose(*patterns: str):
    """A filename → (device, pose, language) reader.

    Unmatched files are not captures and are skipped rather than guessed at: a
    shot folder also holds probe frames, logs and a set's own thumbnails.

    The language comes from the file name when it carries one (the iOS sets
    name it there) and otherwise from the folder, which is the more common
    shape.
    """
    compiled = [re.compile(p, re.I) for p in patterns]

    def parse(name: str):
        match = next((m for m in (rx.match(name) for rx in compiled) if m), None)
        if not match:
            return None
        groups = match.groupdict()
        lang = groups.get("lang")
        pose = groups["pose"].lower()
        # A variant of a screen is a screen: `about-dialog` is not `about`, and
        # it has its own siblings in the other two languages.
        if groups.get("variant"):
            pose = f"{pose}-{groups['variant'].lower()}"
        return (groups.get("device") or "", pose,
                canonical(lang) if lang else None)
    return parse


STORES: dict[str, dict] = {
    # ---------------------------------------------------------------- Windows
    "microsoft": {
        "title": "Microsoft Store",
        "slots": {
            # The frame tools/screenshots-windows ships: the DWM bounds of a
            # 2500x1550 window on the 2560x1600 capture display at 150 %, which
            # is 1667 effective px — above the toolbar's full-label breakpoint.
            # It is the only size that says "a whole window" here.
            "desktop": [(2482, 1541)],
        },
        # Partner Center has no fixed slot for a desktop screenshot: anything at
        # least 1366x768 (docs/microsoft-store-listing.md § Screenshots). The
        # 1.7 listing went up at 2482x1541 — not 16:9, and accepted. A size
        # inside this floor passes; only the frame above is measured as a window.
        "min_size": (1366, 768),
        # Listing order is the file numbers (docs/microsoft-store-listing.md);
        # the poses are the file names with the number taken off.
        "order": ["edit-text", "checkbox", "signature", "shrink", "add-text", "redact"],
        "parse": _pose(r"(?:\d+[-_])?(?P<pose>[a-z][a-z0-9-]*)\.png$"),
        # Measured on the 2.0 set: the title bar is its own strip, rows 0–45;
        # the command bar is ink from about 57 to 104, labels beside their
        # icons; the page's top edge is at 153. Mica runs from the title bar
        # into the canvas, so the band has no edge to find and is fixed at 150.
        # A pre-#144 second row would sit under the first with a gap of a few
        # pixels, which is why only gaps of 4 rows or less are closed.
        "toolbar": {"depth": 150, "fixed": True, "title_bar": 45, "gap": 4,
                    "rows": 1, "height": (40, 130),
                    "rows_by_pose": {"search": 2}},
        "zoom": "100",
        # Two poses show the inline editor open, and its focus underline is
        # drawn in the accent: that is the edit being shown, not a stray.
        "accent_poses": {"redact": ("banner", "mark"),
                         "edit-text": ("the inline editor's underline",),
                         "add-text": ("the inline editor's underline",)},
        # The app's own icon in the title bar is blue (about 400 px of accent
        # in every shot). Measured on the 2.0 set: a 24 px square at (13, 10).
        "accent_ignore": [(0, 0, 60, 45)],
        "accent_strict": True,
        # The DWM bounds include Windows 11's 1 px border, and the capture
        # reads the screen, so the outer two pixels on every side are border
        # over whatever wallpaper is behind the window — measured on the 2.0
        # set, where Spotlight's photo put 70–700 "strokes" on those lines and
        # none from two pixels in. The clipping check starts inside them.
        "edge_inset": 2,
        "status_band": None,
        "notes": "Shot on a real desktop, not in CI. Frame and scale matter: "
                 "2500x1550 at 150 % is 1667 effective px, above the toolbar's "
                 "full-label breakpoint.",
    },
    # -------------------------------------------------------------------- Mac
    "mac": {
        "title": "Mac App Store",
        "slots": {"desktop": [(1440, 900), (2880, 1800), (2560, 1600), (1280, 800)]},
        "order": ["viewer", "text", "search", "sign", "redact", "home"],
        "parse": _pose(r"(?:light-|dark-)?(?:\d+[-_])?(?P<pose>[a-z][a-z0-9-]*)\.png$"),
        # The find bar is a legitimate second row, and only in the search pose
        # (#144 left one row of commands; Find opens its own bar under it).
        "toolbar": {"depth": 160, "rows": 1, "height": (36, 90),
                    "rows_by_pose": {"search": 2}},
        "zoom": "100",
        "accent_poses": {"redact": ("banner", "mark")},
        "accent_strict": True,
        "status_band": None,
        "notes": "tools/macos-store-captures.sh. Each image has a .log beside "
                 "it recording the toolbar mode and the menu-bar check.",
    },
    # -------------------------------------------------------------------- iOS
    "ios": {
        "title": "App Store (iOS)",
        "slots": {
            "iphone-6_9": [(1320, 2868), (1290, 2796)],
            "iphone-6_5": [(1242, 2688), (1284, 2778)],
            "ipad-13": [(2064, 2752), (2048, 2732)],
        },
        "order": ["home", "viewer", "search", "text", "sign", "draw", "redact"],
        "parse": _pose(r"(?:[a-z]-)?(?:(?P<lang>[a-z]{2}(?:-[A-Za-z]{2})?)-)?"
                       r"(?P<device>iphone-[0-9_]+|ipad-[0-9]+)-"
                       r"(?P<pose>[a-z][a-z0-9-]*)\.png$"),
        # iOS draws no toolbar band of the desktop kind, and no zoom chip:
        # the viewer is laid out to the container's width. Both checks stand
        # down by profile rather than by accident.
        "toolbar": None,
        "zoom": None,
        "accent_poses": {"redact": ("banner", "mark")},
        # Not strict: on iOS the accent is the tint of every control, so its
        # presence means nothing and only a difference between the languages of
        # one pose does. On the desktop the accent is reserved for armed modes
        # and selection, which is why it is strict there.
        "accent_strict": False,
        # The status bar is posed (9:41, full battery, no carrier badge) and is
        # the same pixels in every shot of a device, which is what makes a
        # stray notification or a Wi-Fi badge findable without a reference.
        # Per device, because the band has to stop at the status bar: the
        # iPad's navigation bar starts about fifty pixels down and carries the
        # document's title and a Save button, both of which translate.
        "status_band": {"height_frac": 0.045,
                        "by_device": {"ipad-13": 0.019}},
        "notes": "tools/ios-screenshots.sh, listing/ and review/ sets.",
    },
    # ---------------------------------------------------------------- Android
    "play": {
        "title": "Google Play",
        "slots": {
            "phone": [(1080, 2400), (1080, 1920), (1080, 2160)],
            "tablet": [(1600, 2560), (2560, 1600)],
            # The QA matrix's own cells. Not listing slots — the gate says so
            # when it meets them, which is the right answer for a capture that
            # was never meant for a store.
            "small": [(720, 1440)], "large": [(1440, 3200)],
        },
        "order": ["home", "viewer", "search", "sign", "draw", "text",
                  "text-edit", "redact"],
        "parse": _pose(
            r"(?P<device>small|tablet|large)__(?P<lang>en|fr-CA|fr-FR)__"
            r"(?:light|dark)__t\d+__(?P<pose>[A-Za-z0-9-]+)\.png$",
            r"android-(?P<pose>[a-z][a-z0-9-]*)\.png$",
            r"(?:\d+[-_])?(?P<pose>[a-z][a-z0-9-]*)\.png$"),
        "toolbar": None,
        # Android has no window to land on and no "100 %" to choose: the page
        # is laid out at the container's width and the viewer opens at 1f,
        # which is also MIN_ZOOM (docs/qa/android-store-captures.md).
        "zoom": None,
        "accent_poses": {"redact": ("banner", "mark")},
        "accent_strict": False,
        "status_band": {"height_frac": 0.032},
        "notes": "android/scripts/capture-screenshots.sh under SystemUI demo "
                 "mode. The aspect ratio and the tablet slot are Play Console "
                 "questions, written up in docs/qa/android-store-captures.md.",
    },
    # ------------------------------------------------- App previews (video)
    # Not a sixth store: the same three listings, one still per second pulled
    # out of a preview clip (tools/preview-gate.py). A clip is checked one
    # language at a time, so the cross-language checks stand down by design
    # and the one that earns its keep is `chrome` — within a clip the posed
    # status bar is the same pixels in every frame, so a notification or a
    # banner arriving halfway through has 20-odd siblings to disagree with.
    "video": {
        "title": "App previews (App Store, Mac App Store)",
        # The sizes the repo's own tooling produces, and nothing else: iOS
        # records the simulator at the device's native size, which is the
        # listing slot (docs/app-store-listing.md § App preview videos), and
        # tools/macos-record-demo.sh records a 1920x1080 window.
        "slots": {
            "iphone-6_9": [(1320, 2868), (1290, 2796)],
            "ipad-13": [(2064, 2752), (2048, 2732)],
            "mac": [(1920, 1080), (3840, 2160)],
        },
        "order": [],
        "parse": _pose(r"(?P<device>iphone-[0-9_]+|ipad-[0-9]+|mac)-"
                       r"(?P<pose>t[0-9]+)\.png$"),
        # A frame caught mid-flow legitimately shows a second row — the find
        # bar, the placing banner, a sheet over the toolbar — so a row count
        # would flag most of a clip and mean nothing. The one-row 2.0 toolbar
        # is proved by the stills sets; here it is read by eye.
        "toolbar": None,
        "zoom": None,
        "accent_poses": {},
        # Every armed mode in the story paints in the accent on purpose.
        "accent_strict": False,
        # iOS poses the status bar (9:41, full battery, no badge); the Mac
        # frames are the window's content, which has none.
        "status_band": {"height_frac": 0.045,
                        "by_device": {"ipad-13": 0.019, "mac": 0}},
        # A still out of an H.264 clip has no flat areas: the quantiser smears
        # the bar's grey over a dozen values and the ringing along the clock
        # reads as ink. Measured on the 2.0 English iPhone preview, that moved
        # up to 14.2 % of the band between two frames with nothing in the bar,
        # while a planted badge moved 12.6 % — the check could not tell them
        # apart at all. Widening each background tone by 16 puts the same clean
        # frames at 3.9-4.6 % and the badge at 24.6 %. Zero everywhere else, so
        # no shot set already signed off is measured differently.
        "ink_tolerance": 16,
        "notes": "tools/preview-gate.py, one still per second of the clip "
                 "that goes to App Store Connect.",
    },
    # ------------------------------------------------------------------ Linux
    # Not a store: the GitHub release page and, later, Flathub. Here because
    # the Linux set is the one that can be re-shot on this server, which makes
    # it the gate's own regression fixture.
    "linux": {
        "title": "Linux (release page / Flathub)",
        # One slot per window width the rig shoots, because on Linux "the
        # slot" is a window size rather than a store's fixed frame.
        "slots": {"1280": [(1280, 800), (1280, 1000)], "1000": [(1000, 800)],
                  "800": [(800, 700)], "480": [(480, 360)],
                  "desktop": [(1280, 800)]},
        "order": ["doc", "more", "textbox", "redact", "sign", "busy", "empty",
                  "about", "notices", "unsaved", "mode"],
        "parse": _pose(
            r"(?P<pose>[a-z][a-z0-9-]*)_(?P<lang>en|fr-CA|fr-FR)_"
            r"(?:light|dark)_(?P<device>\d+)(?:-(?P<variant>[a-z0-9-]+))?\.png$",
            r"(?:\d+[-_])?(?P<pose>[a-z][a-z0-9-]*)\.png$"),
        "toolbar": {"depth": 200, "rows": 1, "height": (32, 90),
                    # Find opens its own bar under the toolbar; the armed modes
                    # put a banner there.
                    "rows_by_pose": {"find": 2, "search": 2, "mode": 2,
                                     "redact": 2, "busy": 2, "busy-line": 2,
                                     "busy-page": 2, "more": 2}},
        "zoom": "100",
        # Where the brand accent belongs on this platform, per pose: the armed
        # banner and its marks, the busy strip (a progress bar is accent by
        # definition) and the licence links in About.
        "accent_poses": {"redact": ("banner", "mark"), "mode": ("banner",),
                         "busy": ("progress",), "busy-line": ("progress",),
                         "busy-page": ("progress",), "about": ("links",),
                         "about-dialog": ("links",), "notices": ("links",),
                         "notices-dialog": ("links",),
                         # The textbox pose *is* a selected text box, the focus
                         # pose is a focus ring, and a dialog's default button
                         # is filled with the accent.
                         "textbox": ("a selected text box",),
                         "focus": ("the focus ring",),
                         "unsaved": ("the default button",),
                         "unsaved-dialog": ("the default button",)},
        "accent_strict": True,
        "status_band": None,
        # A QA matrix, not a listing: dialogs are shot cropped to themselves,
        # so a size that is not a window is a crop rather than a defect.
        "crops_expected": True,
        # Third-party licence texts are not translated and must not be: the
        # notices dialog is the same words in every language by design (#176).
        "untranslated_poses": ("notices", "notices-dialog"),
        "notes": "docs/qa/linux-screen-inventory.md.",
    },
}


def profile(store: str) -> dict:
    try:
        return STORES[store]
    except KeyError:
        raise SystemExit(
            f"unknown store {store!r}. Known: {', '.join(sorted(STORES))}")


def language_of(dirname: str) -> str | None:
    for lang, names in LANG_DIRS.items():
        if dirname in names:
            return lang
    return None


def canonical(tag: str) -> str | None:
    """`fr`, `frCA`, `en-US` → the listing language, or None if it is not one."""
    return language_of(tag) or language_of(tag.replace("_", "-"))
