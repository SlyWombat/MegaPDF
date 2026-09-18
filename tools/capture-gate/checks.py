"""The checks, one function each, all of them returning findings rather than
raising or printing.

A finding is (check, status, note). Three statuses and they mean different
things on purpose:

* **pass** — measured, and it is right.
* **flag** — measured, and it is questionable. The note says what was measured
  so the reader can disagree with the tool rather than re-do its work.
* **skip** — *not* measured, and why. A gate that silently drops a check it
  could not run is worse than no gate, because the sheet then reads clean.

Two kinds of check live here. Per-image ones look at one capture against the
store profile. Set-wide ones look at a capture against its siblings, and they
are the stronger half: the poses of a set are shot by the same script on the
same frame, so *any* difference between them that is not the translation is a
difference somebody introduced — a dialog that did not dismiss, a notification
that arrived, a selection that was never cleared.
"""
from __future__ import annotations

import collections
import re
import unicodedata

import im
import stores

Finding = collections.namedtuple("Finding", "check status note")


def _ok(check, note):
    return Finding(check, "pass", note)


def _flag(check, note):
    return Finding(check, "flag", note)


def _skip(check, note):
    return Finding(check, "skip", note)


# --------------------------------------------------------------------- size

def _ratio(w: int, h: int) -> str:
    from math import gcd
    g = gcd(w, h)
    return f"{w // g}:{h // g} ({max(w, h) / min(w, h):.3f})"


def size(shot, profile) -> list[Finding]:
    w, h = shot.size
    accepted = profile["slots"].get(shot.device or "desktop") or []
    if not accepted:
        return [_skip("size", f"no slot named {shot.device or 'desktop'!r} in this "
                              f"profile — {w}x{h}, {_ratio(w, h)}")]
    if (w, h) in accepted:
        return [_ok("size", f"{w}x{h}, {_ratio(w, h)}")]
    if profile.get("crops_expected") and not _is_frame(shot, profile):
        return [_skip("size", f"{w}x{h} — not a window size, so this is a crop "
                              f"of one; the frame checks stand down")]
    return [_flag("size", f"{w}x{h} is not a {shot.device or 'desktop'} slot "
                          f"({', '.join(f'{a}x{b}' for a, b in accepted)}); "
                          f"ratio {_ratio(w, h)}")]


# ---------------------------------------------------------------- clipping

def _ink_line(line: bytes, background: set[int]) -> bytes:
    table = bytes(0 if v in background else 1 for v in range(256))
    return line.translate(table)


def _tightest(runs: list[tuple[int, int]], within: int) -> list[tuple[int, int]]:
    """The longest group of runs whose neighbours are `within` px apart."""
    best: list[tuple[int, int]] = []
    group: list[tuple[int, int]] = []
    for run in runs:
        if group and run[0] - (group[-1][0] + group[-1][1]) > within:
            best = max(best, group, key=len)
            group = []
        group.append(run)
    return max(best, group, key=len)


def edges(shot, profile, band: int = 3, glyph_max: int = 18,
          glyph_min_count: int = 3) -> list[Finding]:
    """Text touching the frame.

    Anything drawn hard against the outside of the image was cut off by it. A
    window border, a scrollbar track and the shadow under a title bar all touch
    the frame too, and they touch it *along its whole length*, which is how
    they are told apart from the three or four short strokes a clipped word
    leaves behind.
    """
    w, h = shot.size
    findings = []
    worst = []
    for name, box, outer in (
            ("top", (0, 0, w, band), "row0"),
            ("bottom", (0, h - band, w, band), "rowN"),
            ("left", (0, 0, band, h), "col0"),
            ("right", (w - band, 0, band, h), "colN")):
        crop = im.gray_box(shot.path, box)
        if crop.h == 0 or crop.w == 0:
            continue
        background = im.background_values(crop, min_share=0.02)
        line = {"row0": crop.row(0), "rowN": crop.row(crop.h - 1),
                "col0": crop.col(0), "colN": crop.col(crop.w - 1)}[outer]
        mask = _ink_line(line, background)
        found = im.runs(mask, {1})
        length = len(line)
        glyphs = [r for r in found if 1 <= r[1] <= glyph_max]
        structural = [r for r in found if r[1] > length * 0.4]
        if structural:
            continue                      # a border or a track, not a word
        # Strokes have to be *near each other* to be a word. Three marks spread
        # over 800 px of the right-hand edge are a scrollbar's arrows and its
        # thumb, which is what the first version of this check reported on
        # every search pose.
        cluster = _tightest(glyphs, within=40)
        if len(cluster) >= glyph_min_count:
            span = cluster[-1][0] + cluster[-1][1] - cluster[0][0]
            worst.append(f"{name}: {len(cluster)} strokes in {span} px "
                         f"at {cluster[0][0]}")
    if worst:
        findings.append(_flag("clipping", "ink touching the frame — " +
                              "; ".join(worst)))
    else:
        findings.append(_ok("clipping", "nothing but chrome touches the frame"))
    return findings


def _is_frame(shot, profile) -> bool:
    """Is this capture a whole window, or a crop of one?

    A QA set holds both: a dialog is often shot cropped to itself. The slot
    sizes are the list of things that are whole windows, so anything that is
    not one of them is a crop, and the checks that measure window furniture
    stand down rather than measure the crop's own edges.
    """
    for sizes in profile["slots"].values():
        if tuple(shot.size) in sizes:
            return True
    return False


# ---------------------------------------------------------------- toolbar

def toolbar(shot, profile) -> list[Finding]:
    """The 2.0 toolbar is one row (#144). The pre-#144 bar was two."""
    spec = profile.get("toolbar")
    if not spec:
        return [_skip("toolbar", "this platform has no desktop toolbar band")]
    if not _is_frame(shot, profile):
        return [_skip("toolbar", "not a full window — a crop has no toolbar to "
                                 "measure")]
    raster = im.gray(shot.path)
    end = shot.toolbar_end
    # A button is an icon with a word under it, and on some platforms the two
    # are far enough apart to read as separate rows of ink. Gaps smaller than
    # a sixth of the band are closed before counting — within the band only,
    # so that closing one cannot join the bar to the page under it.
    inside = im.blocks(im.ink_profile(raster)[:end], 0.004,
                       gap=max(2, end // 6))
    rows = len(inside)
    lo, hi = spec["height"]
    # Some poses put a second bar under the toolbar on purpose — Find opens
    # one, and armed modes show their banner. The profile names them, so that
    # "two rows" stays a defect everywhere else.
    expected = spec.get("rows_by_pose", {}).get(shot.pose, spec["rows"])
    if rows > expected:
        return [_flag("toolbar", f"{rows} rows of controls in a {end} px band — "
                                 f"the {shot.pose} pose should have "
                                 f"{expected} (#144)")]
    if not rows:
        return [_flag("toolbar", f"no controls found in the top {end} px")]
    if not lo <= end <= hi * expected:
        return [_flag("toolbar", f"the toolbar band is {end} px; expected "
                                 f"{lo}–{hi * expected} for {expected} row(s) "
                                 f"on this platform (#144)")]
    return [_ok("toolbar", f"{rows} row(s), {end} px")]


# ------------------------------------------------------------ stray chrome

def accent(shot, profile) -> list[Finding]:
    """Brand-accent pixels, and what shape they are in.

    The accent is legitimate in two shapes: the armed-mode banner, which is a
    full-width band, and a redaction mark, which sits on the page. A selection
    border left on a text box is neither — it is a thin rectangle around a word
    — and it is how "Hélène Bélanger" reached a Mac set with a blue box drawn
    through its accents.
    """
    mask = im.colour_mask(shot.path, stores.ACCENT)
    total = im.matched(mask)
    if total == 0:
        return [_ok("accent", "no brand accent drawn")]
    allowed = profile.get("accent_poses", {}).get(shot.pose, ())
    band_rows, stray = 0, 0
    for y in range(mask.h):
        row = mask.row(y)
        hits = row.count(0)
        if hits > mask.w * 0.5:
            band_rows += 1
        else:
            stray += hits
    shapes = []
    if band_rows:
        shapes.append(f"a {band_rows}-row band")
    if stray:
        shapes.append(f"{stray} px elsewhere")
    note = f"{total} accent px: " + " and ".join(shapes)
    if not profile.get("accent_strict"):
        # On the phones every control is tinted with the accent, so its
        # presence says nothing; `accent_consistent` still compares the pose
        # across languages, which is where a stray selection shows up there.
        return [_ok("accent", note + " — measured, not judged on this platform")]
    if allowed:
        return [_ok("accent", f"{note} — this pose draws "
                              f"{', '.join(allowed)} in the accent")]
    return [_flag("accent", note + " — no accent is expected in this pose; "
                                   "a selection or a toggled tool left on?")]


# Spell-check underlines are red and wavy on every platform the app runs on.
# The exact red is the OS's, not ours, so a few are tried with a wide fuzz and
# the *shape* does the discriminating: a run of red 20 px or wider that is only
# two to five pixels tall, which is a squiggle and not an icon.
SQUIGGLE_REDS = ("#ff3b30", "#e81123", "#d70015", "#ff0000")


def squiggle(shot, profile) -> list[Finding]:
    """A spelling underline left on camera.

    Red and thin is not enough: a Material text field draws a straight red rule
    under itself when it rejects a password, and that is a screen doing its
    job. What tells them apart is what a row of the mark looks like. A rule is
    one unbroken run of red; a squiggle dips in and out of every row it
    touches, so each row holds several short runs with gaps between them.

    The row test came out of the Android QA matrix, where "red, thin, and
    spread over four rows" described the error underline on six different
    password screens.
    """
    for red in SQUIGGLE_REDS:
        mask = im.colour_mask(shot.path, red, fuzz=18)
        if im.matched(mask) < 20:
            continue
        counts = [mask.row(y).count(0) for y in range(mask.h)]
        inked = bytes(1 if n >= 12 else 0 for n in counts)
        for start_y, length in im.runs(inked, {1}):
            if not 2 <= length <= 6:
                continue
            rows = counts[start_y:start_y + length]
            total = sum(rows)
            width = max(rows)
            if width < 20:
                continue
            if max(rows) / total > 0.6:
                continue                      # nearly all on one row: a rule
            busiest = start_y + rows.index(max(rows))
            segments = im.runs(mask.row(busiest), {0})
            if len(segments) < 4:
                continue                      # one unbroken run: a rule
            return [_flag("squiggle",
                          f"a red mark {width} px wide in {len(segments)} "
                          f"segments over {length} rows at y={start_y} — a "
                          f"spelling underline left on camera? ({red})")]
    return [_ok("squiggle", "no red underline")]


# ------------------------------------------------------- text, through OCR

def _fold(text: str) -> str:
    return unicodedata.normalize("NFC", text).replace(" ", " ").lower()


def _accents(text: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFD", text)
                   if unicodedata.combining(c))


def _unaccented(text: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFD", text)
                   if not unicodedata.combining(c))


def person(shot, profile) -> list[Finding]:
    """The demo person matches the listing language, accents intact.

    Read with a tolerance OCR forces: tesseract distinguishes é from è about as
    well as a tired proofreader, so the *identity* is compared unaccented and
    the accents are checked for presence rather than for being the right ones.
    A name that comes back with no accents at all is worth a look — that is
    what a missing glyph or the wrong fixture looks like; a name that comes
    back with the wrong one of two accents is the reader, not the image.
    """
    expected = stores.PEOPLE.get(shot.lang)
    if not expected:
        return [_skip("person", f"no demo person recorded for {shot.lang}")]
    text = im.ocr(shot.path)
    if text is None:
        return [_skip("person", "needs tesseract; the language sets are still "
                                "compared against each other (see 'siblings')")]
    haystack = _fold(text)
    plain = _unaccented(haystack)
    surname = _fold(expected.split()[-1])
    others = [(lang, p) for lang, p in stores.PEOPLE.items()
              if lang != shot.lang and p != expected]
    for lang, name in others:
        if _unaccented(_fold(name.split()[-1])) in plain:
            return [_flag("person", f"reads {name!r}, but this is the "
                                    f"{shot.lang} set — expected {expected!r}")]
    if _unaccented(surname) not in plain:
        return [_skip("person", f"{expected!r} is not on this screen (no name "
                                f"is posed in every shot)")]
    if surname in haystack:
        return [_ok("person", f"{expected} — accents intact")]
    accents = _accents(expected)
    where = plain.index(_unaccented(surname))
    read = haystack[where:where + len(surname)]
    if accents and not _accents(read):
        return [_flag("person", f"{expected!r} is on screen without any of its "
                                f"accents — a missing glyph, or the wrong "
                                f"fixture")]
    return [_ok("person", f"{expected} — accented, though OCR read it as "
                          f"{read!r} (é and è are one letter to tesseract)")]


def zoom(shot, profile) -> list[Finding]:
    """The zoom reads the pinned value.

    Found rather than looked up: the chip moves with the language (French
    labels are longer) and with the pose (the text pose adds the font and size
    pickers), so a box measured on one shot is wrong on the next one. The
    toolbar is cut into its controls and each is read as a single line until
    one of them is a percentage.
    """
    pinned = profile.get("zoom")
    if not pinned:
        return [_skip("zoom", "this platform has no zoom control in the posed "
                              "screens — Android opens at 1f, which is both the "
                              "container width and MIN_ZOOM")]
    if not im.have("tesseract"):
        return [_skip("zoom", "needs tesseract to read the chip")]
    w, h = shot.size
    depth = shot.toolbar_end or min(160, h)
    # Only the first row of the band: the search pose opens the find bar under
    # the toolbar, and a crop holding two rows is not a single line to read.
    rows = im.blocks(im.ink_profile(im.gray(shot.path)), 0.004)
    first = next((b for b in rows if b[1] <= depth), (0, depth))
    top, bottom = max(0, first[0] - 3), min(h, first[1] + 3)
    for x, width in im.column_clusters(shot.path, bottom - top, top=top):
        text = im.ocr(shot.path,
                      box=(max(0, x - 4), top, width + 8, bottom - top), psm=7)
        # French writes "100 %", with a non-breaking space the reader renders
        # as an ordinary one, and tesseract sometimes puts a space inside the
        # number itself.
        found = [m.replace(" ", "").replace("\u00a0", "")
                 for m in re.findall(r"(\d[\d ]{0,4})\s*%", text or "")]
        found = [m for m in found if m]
        if not found:
            continue
        if all(v == pinned for v in found):
            return [_ok("zoom", f"{found[0]} %")]
        return [_flag("zoom", f"reads {', '.join(found)} % — the set is shot "
                              f"at {pinned} %")]
    return [_skip("zoom", "no percentage found in the toolbar")]


# Paths that should never be on a listing image. A capture is taken on
# somebody's machine, and the recents list, the title bar and the status line
# all quote where the file came from; #162 and #165 exist because full paths
# reached a review set. The match is reported by name only — the gate never
# prints the path it found, which would copy it into a log and defeat the
# point of noticing.
PRIVATE_PATHS = (
    ("a macOS home folder", r"/Users/[A-Za-z0-9._-]+"),
    ("a Windows home folder", r"[Cc]:\\Users\\[A-Za-z0-9._-]+"),
    ("a Linux home folder", r"/home/[A-Za-z0-9._-]+"),
    ("an iOS container path", r"/var/mobile/Containers"),
    ("an Android storage path", r"/storage/emulated/[0-9]+"),
)


def privacy(shot, profile) -> list[Finding]:
    """Nothing on a listing image should say whose machine it was shot on."""
    text = im.ocr(shot.path)
    if text is None:
        return [_skip("privacy", "needs tesseract to read the screen")]
    hits = [name for name, pattern in PRIVATE_PATHS if re.search(pattern, text)]
    if hits:
        return [_flag("privacy", f"{hits[0]} is legible in this image — a "
                                 f"capture should not say whose machine it "
                                 f"came from (#162, #165)")]
    return [_ok("privacy", "no home-folder path on screen")]


# Words that belong to the English build and have a French counterpart in
# every screen of a French set. One of them on a French capture means a string
# came from the wrong place — which is how "scanned-agreement - smaller.pdf"
# reached both French Windows sets, from an English `-Out` typed on the command
# line rather than from the app's own SmallerFileName resource.
# Each has a French counterpart that shares no letters with it, which matters:
# OCR drops accents (it reads "Récents" as "Recents"), so a marker whose French
# form differs only by an accent finds nothing but the reader's own limits.
# That cost one false positive on every French iOS home screen before "recent"
# came out of this list.
ENGLISH_MARKERS = (
    "smaller", "save as", "open a pdf", "cancel", "done", "add text",
    "sign above", "whiteout", "redact", "page 1 of",
)


def language_purity(shot, profile) -> list[Finding]:
    """No English left in a French set."""
    if not shot.lang or not shot.lang.startswith("fr"):
        return [_skip("language", "only the French sets are checked for this")]
    text = im.ocr(shot.path)
    if text is None:
        return [_skip("language", "needs tesseract to read the screen")]
    haystack = _fold(text)
    # Whole words only: "recents" is not "recent", and a French word that
    # happens to contain an English one is not an English string.
    hits = [w for w in ENGLISH_MARKERS
            if re.search(rf"\b{re.escape(w)}\b", haystack)]
    if hits:
        return [_flag("language", f"the English word {hits[0]!r} is on a "
                                  f"{shot.lang} capture — a string that did not "
                                  f"come from the catalogue?")]
    return [_ok("language", "no English UI words on a French capture")]


def against_reference(shot, reference: str | None) -> list[Finding]:
    """This shot against the one that was signed off."""
    if reference is None:
        return [_skip("certified", "no counterpart in the reference set")]
    fraction = im.difference(shot.path, reference)
    if fraction is None:
        return [_flag("certified", "a different size from its counterpart in "
                                   "the reference set")]
    if fraction <= 0.001:
        return [_ok("certified", f"matches the reference set "
                                 f"({100 * fraction:.3f} % of pixels differ)")]
    return [_flag("certified", f"{100 * fraction:.2f} % of pixels differ from "
                               f"the reference set — intended, or is this "
                               f"shot posed differently?")]


PER_IMAGE = (size, edges, toolbar, accent, squiggle, person, zoom, privacy,
             language_purity)


# ------------------------------------------------------------ set-wide work

def poses_match(group: list, profile) -> list[tuple]:
    """The same pose in three languages should be the same screen.

    Compared as *which rows have ink on them* rather than as pixels. A longer
    French label changes how much ink a row holds and sometimes wraps a
    sentence onto one more line; neither moves the screen about. Something that
    disagrees over a long unbroken stretch of rows — a dialog in one language,
    a control that did not appear, a list that scrolled — did move it, and that
    is what gets reported, with the rows to look at.
    """
    out = []
    if len(group) < 2:
        return out
    reference = group[0]
    for shot in group[1:]:
        if shot.size != reference.size and profile.get("crops_expected") \
                and not _is_frame(shot, profile):
            out.append((shot, _ok(
                "siblings",
                f"cropped to its own bounds: {shot.size[0]}x{shot.size[1]} "
                f"against {reference.lang}'s {reference.size[0]}x"
                f"{reference.size[1]} — a French dialog is a different size")))
            continue
        if shot.size != reference.size:
            out.append((shot, _flag("siblings",
                                    f"{shot.size[0]}x{shot.size[1]} against "
                                    f"{reference.lang}'s "
                                    f"{reference.size[0]}x{reference.size[1]}")))
            continue
        height = shot.size[1]
        disagree = bytes(a ^ b for a, b in zip(reference.ink_rows, shot.ink_rows))
        total = sum(disagree) / height
        stretches = im.runs(disagree, {1})
        longest = max(stretches, key=lambda r: r[1], default=(0, 0))
        # A wrapped line of text is about one line tall. Anything disagreeing
        # over more than 2.5 % of the screen's height in one unbroken stretch
        # is not a translation being longer.
        if longest[1] > max(18, height * 0.025) or total > 0.25:
            out.append((shot, _flag(
                "siblings",
                f"rows {longest[0]}–{longest[0] + longest[1]} have ink in one "
                f"of {reference.lang}/{shot.lang} and not the other "
                f"({100 * total:.1f} % of rows disagree in all) — the layouts "
                f"differ by more than the words")))
        else:
            out.append((shot, _ok(
                "siblings",
                f"same layout as {reference.lang} "
                f"({100 * total:.1f} % of rows disagree, longest run "
                f"{longest[1]} px — translation, not layout)")))
        # A modal dims the window behind it, and the same pose in another
        # language is not dimmed unless the dialog is part of the pose. One
        # shot several per cent darker than its own translation is a sheet
        # that did not dismiss.
        if shot.top_tone < reference.top_tone - 8:
            out.append((shot, _flag(
                "chrome", f"the top of this window is painted "
                          f"{reference.top_tone - shot.top_tone} shades darker "
                          f"than {reference.lang}'s — something modal over "
                          f"the window?")))

        if shot.status_right and reference.status_right:
            # The posed battery and Wi-Fi do not translate: the same pose in
            # another language is the one sibling guaranteed to draw them the
            # same way, down to the pixel. A notification, a carrier name or
            # SystemUI's "no internet" badge over the Wi-Fi icon has nowhere
            # to hide in that comparison. The *left* of the bar is not
            # compared: an iPad puts the date there, and a date translates.
            ink = max(sum(shot.status_right), 1)
            differ = im.mask_difference(shot.status_right,
                                        reference.status_right)
            if differ / ink > 0.02:
                out.append((shot, _flag(
                    "chrome", f"the right of the status bar differs from "
                              f"{reference.lang}'s by {100 * differ / ink:.1f} % "
                              f"of its ink — the battery and the Wi-Fi do not "
                              f"translate, so something is in there that "
                              f"should not be")))
            else:
                out.append((shot, _ok(
                    "chrome", f"signal, Wi-Fi and battery identical to "
                              f"{reference.lang}'s")))

        # Identical pixels across two languages means one of them did not
        # translate — the check `compare.py` makes on the Android matrix.
        if shot.digest == reference.digest:
            if shot.pose in profile.get("untranslated_poses", ()):
                out.append((shot, _ok(
                    "siblings", f"identical to the {reference.lang} shot, as "
                                f"this screen is meant to be")))
            else:
                out.append((shot, _flag(
                    "siblings", f"pixel-identical to the {reference.lang} shot "
                                f"— this screen did not translate")))
    return out


def chrome_consistent(group: list, profile, live_clock: bool = False) -> list[tuple]:
    """Within one language set, the frame furniture must not vary.

    Both measurements are made against the set's own middle rather than a
    stored reference, so a re-shoot on a new frame needs nothing here updated:

    * the **status band**, among the shots that share a background. The clock
      and battery are the same ink over a white list and over a grey toolbar,
      but their edges are not, so comparing across backgrounds measures the
      antialiasing rather than the contents. The stronger comparison is the
      cross-language one in `poses_match`; this one is what is left when a set
      has only one language.
    * the **top band's brightness** — a modal dims the window behind it, so a
      sheet that did not dismiss shows up as one shot several per cent darker
      than its siblings before anyone looks at what the sheet says.
    """
    out = []
    if len(group) < 3:
        return out
    if (profile.get("status_band") or {}).get("height_frac"):
        buckets = collections.defaultdict(list)
        for shot in group:
            if shot.status_ink:
                buckets[shot.status_bg].append(shot)
        for bucket in buckets.values():
            if len(bucket) < 3:
                continue
            # With a live clock only the right of the bar is comparable: the
            # digits change between shots. A store set poses the clock (9:41,
            # SystemUI demo mode), which is what makes the whole bar worth
            # comparing and a stray notification findable on the left of it.
            def bar(shot):
                return shot.status_right if live_clock else shot.status_ink

            scores = {id(s): sum(im.mask_difference(bar(s), bar(o))
                                 for o in bucket if o is not s) for s in bucket}
            centre = min(bucket, key=lambda s: scores[id(s)])
            for shot in bucket:
                if shot is centre:
                    continue
                ink = max(sum(bar(shot)), 1)
                differ = im.mask_difference(bar(shot), bar(centre))
                if differ / ink > 0.05:
                    out.append((shot, _flag(
                        "chrome", f"status bar differs from the rest of the "
                                  f"{shot.lang} set by {100 * differ / ink:.1f} % "
                                  f"of its ink — a badge, a notification, or "
                                  f"the wrong demo-mode state")))
    return out


def accent_consistent(group: list, profile) -> list[tuple]:
    """The same pose in three languages should carry the same accent.

    This is the check that finds a selection border without being told what a
    selection border looks like: the English shot has none, the French one has
    520 px of it.
    """
    out = []
    if len(group) < 2:
        return out
    if len({s.size for s in group}) > 1:
        # Dialogs are shot cropped to themselves and a French dialog is wider,
        # so its accent-filled default button is bigger. Comparing the two is
        # comparing the translation.
        return out
    counts = sorted(s.accent_px for s in group)
    median = counts[len(counts) // 2]
    # Loose on purpose. Where the accent fills a button, the button is as wide
    # as its label and a French label is longer, so the same screen carries
    # half as much accent again in French. Only a difference bigger than that
    # is worth a look.
    for shot in group:
        if abs(shot.accent_px - median) > max(300, median * 0.6):
            out.append((shot, _flag(
                "siblings", f"{shot.accent_px} accent px against the pose's "
                            f"median of {median} — an element is armed, "
                            f"selected or highlighted in one language only")))
    return out
