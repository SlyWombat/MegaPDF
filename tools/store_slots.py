"""Each store's listing screenshots: which poses, in what order, under what file name.

The one copy (#656). tools/capture-gate/stores.py, tools/asc_publish.py,
tools/play_listing.py and tools/gen_listing_copy.py import it; the capture rigs
(shell and workflow YAML) are checked against it by tools/test_store_slots.py.
Change an order here and nowhere else, then run that test.

Stdlib only, so every importer (and CI) can reach it with no setup.

`listing` is what goes to the store, in upload order, per device. `extra` is what
the QA set still shoots after the listing poses: a pose a profile does not name
sorts to the end of the capture sheet rather than beside its siblings, and the
QA matrix still wants these. `file` is the name each submission tool writes and
reads, with `{n}` the 1-based slot number, `{pose}` and `{device}`. `max` is
how many images that store's submission tool can carry.
"""
from __future__ import annotations

# Android's eight listing slots since #613, on phone and tablet alike.
# `viewer` and `draw` are behind them because they are no longer listing slots -
# reading replaced the first and the second came out at Play's limit of eight -
# but the QA matrix still shoots both.
_PLAY = ["reading", "text-edit", "sign", "pages", "text", "search", "redact", "home"]

SLOTS: dict[str, dict] = {
    "microsoft": {
        # Listing order is the file numbers (docs/microsoft-store-listing.md); the
        # poses are the file names with the number taken off. Re-cut for 2.2
        # (#613, Dave 2026-10-01): reading and the page tools are what the listing
        # copy now opens with, redaction moves back, and reading replaces the old
        # front slot rather than joining it. Dave settled it the same day at
        # **eight** slots - checkbox back, between fixing the document and signing
        # it, shrink and add-text retired, and the ninth slot the Store allows
        # deliberately left empty. See tools/screenshots-windows/Shoot-Set.ps1,
        # which is not covered by the drift test (its slot names are scattered
        # across eight Shot-*.ps1 calls); msstore_submit.py compares the files on
        # disk with the captions at submit time and refuses a mismatch.
        "listing": {"desktop": ["reading", "text", "checkbox", "sign", "pages",
                                "search", "redact", "home"]},
        "extra": [],
        "file": "{n:02d}-{pose}.png",
        "max": 9,          # msstore_submit.py reads 0[1-9]-*.png; not Partner Center's own limit
    },
    "mac": {
        # The seven listing slots since #613. `viewer` is behind them because it
        # is no longer one - reading replaced it at the front - but the Mac screen
        # inventory still shoots it.
        "listing": {"desktop": ["reading", "text", "sign", "pages", "search",
                                "redact", "home"]},
        "extra": ["viewer"],
        "file": "light-{n:02d}-{pose}.png",
        "max": 10,
    },
    "ios": {
        # Re-cut for 2.2 (#613, PR #651) and - unlike every other store - **two
        # sequences, not one**, because Dave's call on the iPad was to lead on what
        # only that device has rather than repeat the iPhone's case.
        #
        # `text-edit` and `text` both keep their own slot on both devices, as
        # Android's 8-slot set did - correcting existing body text (#113) and
        # adding a new line (#43) are different stories - and `draw` keeps its slot
        # on both too (Dave, PR #651): the App Store takes up to ten images, so the
        # Play-only squeeze that cut `draw` there (#613 section draw) never applied
        # here.
        #
        # iPhone reads as the #613 argument in sequence: read it, fix it, sign it
        # (twice), organise it, fill it, find it, take something out, nothing
        # leaves the device.
        #
        # iPad leads on the sidebar and the 2.2 toolbar (#172) instead (Dave, PR
        # #651): `pages` and `reading` swap to the front - the workspace, then the
        # same workspace taken away, which is the strongest argument that the
        # toolbar in slot 1 is real - and the other seven keep the iPhone's order
        # and the iPhone's captions (tools/gen_listing_copy.py `captions_ipad`),
        # because nothing about *them* is an iPad story.
        "listing": {
            "iphone-6_9": ["reading", "text-edit", "sign", "draw", "pages", "text",
                           "search", "redact", "home"],
            "ipad-13": ["pages", "reading", "text-edit", "sign", "draw", "text",
                        "search", "redact", "home"],
        },
        "extra": ["viewer"],   # .github/workflows/ios-screenshots.yml appends it for the QA inventory
        "file": "{device}-{pose}.png",
        "max": 10,
    },
    "play": {
        "listing": {"phone": _PLAY, "tablet": _PLAY},
        "extra": ["viewer", "draw"],   # still in the QA matrix, not listing slots
        "file": "android-{pose}.png",
        "max": 8,
    },
}


def _store(store: str) -> dict:
    try:
        return SLOTS[store]
    except KeyError:
        raise KeyError(f"unknown store {store!r}; known: {sorted(SLOTS)}") from None


def devices(store: str) -> list[str]:
    """The device labels a store's listing is cut for, in order."""
    return list(_store(store)["listing"])


def listing_poses(store: str, device: str) -> list[str]:
    """A copy of the poses that go to the store for this device, in upload order."""
    listing = _store(store)["listing"]
    try:
        return list(listing[device])
    except KeyError:
        raise KeyError(f"unknown device {device!r} for {store!r}; known: {sorted(listing)}") from None


def listing_files(store: str, device: str) -> list[str]:
    """The file name each listing pose is written and read under, in upload order."""
    template = _store(store)["file"]
    return [template.format(n=i, pose=p, device=device)
            for i, p in enumerate(listing_poses(store, device), 1)]


def gate_order(store: str):
    """The capture gate's `order`: listing poses, then `extra`.

    One flat list when every device's sequence is identical, else a
    {device: [...]} dict (tools/capture-gate/gate.py `order_key` reads either).
    """
    extra = _store(store)["extra"]
    seqs = {d: listing_poses(store, d) + list(extra) for d in devices(store)}
    first = next(iter(seqs.values()))
    if all(s == first for s in seqs.values()):
        return first
    return seqs
