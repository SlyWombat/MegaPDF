# Android screen inventory — the 2.0 QA runbook (#146 §1b)

Every screen, sheet, dialog, menu, mode, notice, busy state and error the Android
app can show, as a checklist to walk. Read with `android/README.md` and `TESTING.md`.

**How to use it.** Walk the list once per capture matrix cell. A cell is
*device × language × theme*, plus one pass per device at the largest text size:

| Axis | Values |
|---|---|
| Device | small phone (360 × 720 dp), large phone (480 × 1067 dp), tablet (800 × 1280 dp) |
| Language | `en`, `fr-CA`, `fr-FR` (set per app with `cmd locale set-app-locales`) |
| Theme | light, dark — **the app is light-only by design** (see note L1) |
| Text size | default, and `font_scale` 2.0 (the largest the system offers) |

Look at every image for: clipping, overlap, untranslated text, wrong theme,
broken layout, a stray system dialog, the real clock instead of the demo one.

`tools/android-qa/` holds the container and the driver used for the 2.0 pass;
[`android-qa-2.0-results.md`](android-qa-2.0-results.md) records what it found.

---

## Where each screen lives

| Area | Source |
|---|---|
| Home, loading, the open prompt | `android/app/src/main/java/com/megapdf/android/HomeScreen.kt` |
| About, third-party notices | `AboutDialog.kt` |
| Viewer, search bar, toolbars, overlays, notices | `ViewerScreen.kt` |
| Signature library sheet and its dialogs | `SignaturesSheet.kt`, `DrawSignatureDialog.kt`, `TypeSignatureDialog.kt` |
| Protection dialogs | `SecurityDialogs.kt`, `DocumentCapabilities.kt` |
| Busy strip and page spinner | `BusyState.kt`, `ViewerScreen.kt` |
| Page-rewrite warning | `PageRewriteWarnings.kt` |
| Status toasts, errors | `ViewerViewModel.kt`, `MainActivity.kt` |

---

## A. Home

- [ ] **A1 Home, first run** — wordmark, tagline "Open. Fix. Save. Done.", **Open PDF**
      button, ⓘ About button top-right. No Recent section.
- [ ] **A2 Home with recents** — the Recent heading and one card per file (name, and the
      date/time in the device's format). Check a long file name and a French month name.
- [ ] **A3 Home with an error** — red text under the button. Five wordings, each its own shot:
  - [ ] A3a `open_failed` — "Couldn't open this file."
  - [ ] A3b `open_failed_code` — "Couldn't open this file (error N)." (corrupt PDF)
  - [ ] A3c `open_unsupported_security` — a protection scheme PDFium can't open
  - [ ] A3d `open_too_large` — out of memory measuring the pages
  - [ ] A3e `open_access_revoked` — a recent whose SAF grant is gone (reboot, or the
        provider revoked it); the entry is dropped from Recent at the same time
- [ ] **A4 About dialog** — version, copyright, "Special thanks to Mega Woman.",
      the GitHub link, **Third-party notices**, Close.
- [ ] **A5 Third-party notices** — full-screen, its own top bar and back arrow,
      monospace paragraphs, scrolls. **A5a** its loading spinner (the file is ~150 KB).
- [ ] **A6 System document picker** (`ACTION_OPEN_DOCUMENT`, `application/pdf`) —
      system UI, and the one screen that *does* follow the system dark theme.

## B. Opening

- [ ] **B1 Under 500 ms** — blank screen, no spinner at all (`BusyState.SHOW_AFTER_MS`).
- [ ] **B2 Over 500 ms** — centred spinner with **Opening…** beneath it.

## C. The prompt for a protected document

- [ ] **C1 Password required** — the dialog, masked field, Open / Cancel.
- [ ] **C2 Rejected** — "That password didn't work. Try again." in the error colour,
      above the field.
- [ ] **C3 Cancel** returns to Home, document closed.

## D. The viewer

- [ ] **D1 Clean** — top bar (back arrow = close, title, **Save** disabled, ⋮), the page on
      the `Brand.Backdrop` grey, bottom bar (Sign, Add text, Search · Undo, Redo).
- [ ] **D2 Dirty** — the title gains a "• " prefix and Save turns on.
- [ ] **D3 Saving** — Save reads **Saving…** and is disabled; every editing tool is
      disabled; back and the file commands are refused.
- [ ] **D4 Zoomed** — pinch to 1×–4×, and double-tap (1× ⇄ 2×). Horizontal scrolling
      appears above 1×.
- [ ] **D5 A page not rendered yet** — plain white page box of the right aspect ratio.
- [ ] **D6 A one-page document** — the page is centred vertically, not top-packed (#48).
- [ ] **D7 Odd page geometry** — a 200-inch poster, a 3-inch × 200-inch receipt, a
      `/UserUnit` banner, mixed sizes and `/Rotate` in one file (the `mixed-sizes`
      fixture), an off-origin MediaBox and a CropBox smaller than it.
- [ ] **D8 Overflow menu (⋮)** — Save a copy · Password… · *(Unlock with owner
      password…, restricted documents only)* · divider · About MegaPDF.
  - [ ] **D8a** every item disabled during a save.
  - [ ] **D8b** Password… disabled when the document has no file behind it.
- [ ] **D9 Tool tooltip** — long-press a bottom-bar icon.
- [ ] **D10 Restricted document** — the tools the owner disallowed are greyed out.

### D11 Busy states (#145)

Each appears only after 500 ms and stays at least 300 ms.

- [ ] **D11a** strip: **Opening…**
- [ ] **D11b** strip: **Saving…**
- [ ] **D11c** strip: **Checking the saved file…**
- [ ] **D11d** strip: **Searching…**
- [ ] **D11e** strip: **Applying…**
- [ ] **D11f** page spinner: **Checking this page…**, beside the line being checked
- [ ] **D11g** page spinner mid-page, when the work has no line of its own

### D12 Notices (the dark pill above the bottom bar, goes away on its own)

- [ ] **D12a** `body_text_substituted` — a similar standard font was used
- [ ] **D12b** `body_text_scanned` — this page is a scanned image
- [ ] **D12c** `body_text_layout` — can't be changed without disturbing the layout
- [ ] **D12d** `body_text_layout_render` — other parts of the page would look different
- [ ] **D12e** `body_text_layout_text_moves` — text elsewhere would move
- [ ] **D12f** `security_restricted_notice` — shown once when a restricted document opens
- [ ] **D12g** `security_restricted_edit` — an edit the owner doesn't allow

### D13 Toasts (system-styled, so these follow the system dark theme)

- [ ] **D13a** `saved` · **D13b** `save_failed` · **D13c** `save_no_permission`
- [ ] **D13d** `tap_to_place_signature` · **D13e** `tap_to_place_text`
- [ ] **D13f** `signature_added` · **D13g** `signature_import_failed` ·
      **D13h** `signature_save_failed` · **D13i** `signature_image_missing` ·
      **D13j** `signature_image_unreadable` · **D13k** `signature_remove_failed` ·
      **D13l** `signature_move_failed`
- [ ] **D13m** `text_untagged` — added by an older version, not editable here
- [ ] **D13n** `text_add_failed` · **D13o** `text_change_failed` ·
      **D13p** `text_move_failed` · **D13q** `text_remove_failed`
- [ ] **D13r** `undo_failed` · **D13s** `redo_failed` · **D13t** `edit_failed`
- [ ] **D13u** `search_failed`
- [ ] **D13v** `security_password_set` / `security_password_changed` /
      `security_password_removed`

## E. Find in document

- [ ] **E1** bar open, empty, keyboard up, placeholder "Search".
- [ ] **E2** sweeping — the counter is blank and the **Searching…** strip shows.
- [ ] **E3** hits — "3 of 17", every match in translucent cyan and the current one in
      translucent blue, ▲ ▼ enabled.
- [ ] **E4** **No results**, ▲ ▼ disabled.
- [ ] **E5** a match reached while zoomed — the view scrolls to it both down *and*
      across (#28).
- [ ] **E6** a match spanning two lines / two text objects.
- [ ] **E7** ✕ closes the bar and clears every highlight; Back does the same.

## F. Signatures

- [ ] **F1** sheet, empty — the explanatory paragraph and **Draw · Type · Photo**.
      Check the three buttons do not clip on the small phone in French.
- [ ] **F2** sheet with entries — a grid of white cards showing the real ink and the name.
- [ ] **F3** a card's ⋮ menu — Rename · Delete (Delete in the error colour).
- [ ] **F4** **Rename signature** dialog.
- [ ] **F5** **Delete "…"?** dialog — "This cannot be undone."
- [ ] **F6** **Draw your signature** — the near-white pad, Save · Clear · Cancel.
      Save is off until something is drawn.
- [ ] **F7** **Type your name** — field plus the live preview in the signature face.
      Type an accented name and check the accents render (§3, see note N1).
- [ ] **F8** the system photo picker (`PickVisualMedia`) — system UI.
- [ ] **F9** placement armed — the "Tap the page where the signature should go" toast.
- [ ] **F10** a placed signature, selected — blue border, ✕ badge, corner resize grip.
      Drag to move, drag the grip to resize, ✕ to remove.
- [ ] **F11** the same, disabled mid-save — no drag, no taps, it stays where it was dropped.

## G. Text

- [ ] **G1** Add text armed — the "Tap the page where the text should go" toast.
- [ ] **G2** **Add text** dialog — hint, field, the **Size** chip row and the **Font**
      chip row. Both rows scroll sideways; check nothing clips on the small phone.
- [ ] **G3** **Edit text** on a box the app added — the field opens on its text, Save.
- [ ] **G4** **Edit text** on the document's own line — "Change this line. The size and
      font stay as they are." Clearing the field removes the line.
- [ ] **G5** a selected text box — blue border, ✎ badge, ✕ badge, **no** resize grip.
- [ ] **G6** **Change this page?** — the once-per-page rewrite warning, Continue / Cancel.
- [ ] **G7** the confirm button disabled while another change is still going in.
- [ ] **G8** tapping a real form field, and tapping a checkbox (interactive widget
      **and** a plain drawn square).

## H. Protection

- [ ] **H1** **Unlock document** — owner-password field, Unlock / Cancel.
  - [ ] **H1a** with unsaved changes: "Unsaved changes will be lost."
  - [ ] **H1b** rejected: the field clears and the error line shows.
  - [ ] **H1c** while checking: the field and Unlock are disabled.
- [ ] **H2** **Document password**, restricted face — the explanation, **Unlock…** / Cancel.
- [ ] **H3** **Set a password** — two masked fields, Set password / Cancel.
- [ ] **H4** **Document password**, change face — two fields plus **Remove password**
      in the body, Change password / Cancel.
- [ ] **H5** "Enter a password." · **H6** "The passwords don't match."

## I. Closing and lifecycle

- [ ] **I1** **Unsaved changes** — Save · Cancel · Discard, all three on one row. Check
      the row does not clip on the small phone in French (three long labels).
- [ ] **I2** Back while the find bar is open closes the bar, not the document.
- [ ] **I3** Back during a save is swallowed; the activity does not finish.
- [ ] **I4** **Save a copy** opens the system `CreateDocument` picker, pre-filled with
      the display name.
- [ ] **I5** rotate the device mid-edit — the document, the selection and the undo
      stack survive.
- [ ] **I6** the app killed from outside (`am force-stop` / `am kill`) — see note R1.
- [ ] **I7** the app backgrounded long enough for the process to be reclaimed —
      see note R1.

---

## Notes and deliberate limits

**L1 — the app is light-only.** `ui/Brand.kt` hands `MegaPdfTheme` the light scheme
whatever the system setting is, and `MainActivity` forces both system bars to the
light style, because a dark scheme would paint white icons onto a white app (#40).
A `DarkColours` scheme exists but is deliberately not wired up. So a dark-mode
capture *should be identical to the light one* except for system-drawn surfaces —
the SAF picker, the photo picker, toasts and the soft keyboard. Anything else that
changes in dark mode is a defect.

**N1 — the demo name (§3).** `screenshot_text` is what the screenshot flow types.
English **Jane Whitfield**, fr-CA **Hélène Bélanger**, fr-FR **Céline Lefèvre**;
the accents must render in the page and in the typed-signature face.

**R1 — there is no crash recovery on Android.** `EditHistory` is single-session and
keeps no journal, unlike the desktop apps. After a kill or a process death the app
comes back to Home and unsaved edits are gone, with no prompt. Walk I6 and I7 to
confirm that is all that happens (no corrupt file, no half-written save), not to
look for a restore offer.

**Not on Android at all** — these desktop flows have no Android screen, and no
capture: whiteout / cover, Shrink for email, Print, and the Settings flyout with the
check-mark style. The Android bottom bar is Sign · Add text · Search · Undo · Redo,
and that is the whole tool set.
