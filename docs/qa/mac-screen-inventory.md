# Mac app — screen inventory and QA checklist (#146 §1b)

Every window, sheet, dialog, menu, mode, notice, busy state and error the
Avalonia macOS app can show. Tick a row when it has been *seen* — in the
language, theme and window width the pass is covering — not merely when the
code that would show it exists.

Run the pass once per **language** (en, fr-CA, fr-FR) × **theme** (light, dark),
and the layout-sensitive rows again at each **width** (1280, 1000, 800, 480 = the
minimum, `MainWindow.axaml:10`).

**Look for:** clipping, overlap, untranslated text, the wrong theme, broken
layout, and text that runs into or under a neighbour.

## How to pose each screen

Most screens can be photographed without touching the mouse — the app renders
its own window, so no Screen Recording grant is involved:

```
APP=~/app-146/MegaPDF.app/Contents/MacOS/MegaPDF
C="$HOME/Library/Containers/com.megapdf.ios/Data"      # the sandbox, where fixtures must live
$APP "$C/tmp/fixtures/demo.pdf" --language fr-CA --theme dark \
     --window 1280x800 --screenshot-state more --screenshot out.png
```

`--screenshot-state` accepts `sign find focus more textbox busy busy-page
busy-line unsaved mode` (`App.axaml.cs:37`). Everything else in the table below
has no pose and must be driven in a **real window** (`tools/macos-record-demo.sh`
shows the cliclick pattern; remember the mode banner pushes the page ~30 px down,
so re-measure with `tools/macos-measure-page.py` after arming a mode).

---

## 1. Windows

| # | Screen | How to reach it | Posed by |
|---|---|---|---|
| 1.1 | Empty state — title, tagline, "Open a PDF…", Recent list | launch with no file | `--screenshot` with no pdf argument |
| 1.2 | Document open, Select mode | open any PDF | `--screenshot` |
| 1.3 | Document open at the minimum width (480×360) | drag the window in | `--window 480x360` |
| 1.4 | Restricted banner + **Unlock…** | open an owner-restricted document (`encrypted.pdf`) | real window |
| 1.5 | Window title dirty bullet (`•`) | make any edit | `--screenshot-state unsaved` |

## 2. Dialogs (separate windows, `CanResize=False`, centred on the owner)

| # | Dialog | Buttons | How to reach it | Posed by |
|---|---|---|---|---|
| 2.1 | **Unsaved changes** — "Do you want to save the changes made to the document "{0}"?" | Don't Save / Cancel / **Save** | edit, then Close, Cmd+Q, or open another | `--screenshot-state unsaved` → `<out>-dialog.png` |
| 2.2 | **Restore unsaved work** (recovery) | Discard them / Decide later / **Restore** | kill the app mid-edit, relaunch | real window |
| 2.3 | **Key needed** to open a protected file | Cancel / **Open** | open a user-protected PDF | real window |
| 2.4 | **Unlock document** (owner credential) — same window, relabelled | Cancel / **Unlock** | click **Unlock…** in the restricted banner | real window |
| 2.5 | Retry text after a wrong entry | Cancel / **Open** | type the wrong thing | real window |
| 2.6 | **Document protection — manage panel** | Cancel / Remove / Change… | File ▸ Protection… on a protected file | real window |
| 2.7 | **Document protection — entry panel** (New / Confirm) | Cancel / Set \| Change | File ▸ Protection… on an unprotected file | real window |
| 2.8 | Entry-panel inline problems — the empty and the mismatch messages | — | commit an empty or mismatched pair | real window |
| 2.9 | **Add a signature** (draw pad, 560×380 fixed) | Clear / Cancel / **Save** | Sign ▸ Draw | real window |
| 2.10 | **Type your name** — live script preview | Cancel / **Add** | Sign ▸ Type | real window |
| 2.11 | **Rename signature** | Cancel / **Rename** | signature card ⋯ ▸ Rename… | real window |
| 2.12 | **Delete signature** — Cancel is *also* the default button, by design | Cancel / Delete (danger) | signature card ⋯ ▸ Delete… | real window |
| 2.13 | **Change this page?** (#139 rewrite warning) | Cancel / **Continue** | edit body text on a page that needs a rewrite | real window |
| 2.14 | Open file picker — "Open a PDF" | — | Cmd+O | real window |
| 2.15 | Save As picker — "Save a copy", suggests "{name} copy.pdf" | — | Shift+Cmd+S | real window |
| 2.16 | Shrink picker — "Save a smaller copy", suggests "{name} - smaller.pdf" | — | More ▸ Shrink | real window |
| 2.17 | Import signature picker — "Choose a photo of your signature" | — | Sign ▸ From photo | real window |
| 2.18 | **macOS system print panel** | — | Cmd+P | real window |

## 3. Menus

| # | Menu | Contents to check | Posed by |
|---|---|---|---|
| 3.1 | Menu bar **File** | Open a PDF… · Save · Save As · — · Protection… · Save a smaller copy for email · — · Print | real window |
| 3.2 | Menu bar **Edit** | Undo · Redo · — · Find in document | real window |
| 3.3 | Menu bar **View** | Zoom in · Zoom out · — · Actual size · Fit width · Fit page · Zoom level ▸ (8 presets, radio) | real window |
| 3.4 | Menu bar **Tools** | Sign · Add text ☑ · Cover ☑ · — · Text font ▸ · Text size ▸ · — · Options | real window |
| 3.5 | Tools ▸ Text font / Text size **disabled outside Add text** (stand-in item, no submenu) | greyed, no arrow | real window |
| 3.6 | **More (⋯) menu**, nothing overflowed (1280) | the fixed tail only | `--screenshot-state more --window 1280x800` |
| 3.7 | **More (⋯) menu**, overflowed (800 and 480) | the overflowed commands first, then the fixed tail | `--screenshot-state more --window 800x600` / `480x360` |
| 3.8 | **Zoom-level dropdown** | Actual size · Fit width · Fit page · — · 50/75/100/125/150/200/300/400 % | real window |
| 3.9 | **Options flyout** | "Tick marks look like" (Cross / Check / Filled square) · "Flatten when saving" + caption | real window (More ▸ Options) |
| 3.10 | **Signature library flyout** — cards, per-card ⋯, Draw / Type / From photo | | `--screenshot-state sign` |
| 3.11 | Signature library **empty state** — "No signatures yet…" | | delete every signature, then `--screenshot-state sign` |
| 3.12 | Per-signature **⋯ menu** / right-click context menu | Rename… · Delete… | real window |
| 3.13 | **Text font** toolbar dropdown (Helvetica / Times / Courier — untranslated by design) | | real window, inside Add text |
| 3.14 | **Text size** toolbar dropdown (8–24) | | real window, inside Add text |

## 4. Toolbar layout steps

| # | Step | Width where it appears | Posed by |
|---|---|---|---|
| 4.1 | `Full` — icon over label | 1280 | `--window 1280x800` |
| 4.2 | `IconsOnly` | between | `--window 1000x800` / `800x600` |
| 4.3 | `Overflow` — commands move into More; **Open never leaves the row** | 480 | `--window 480x360` |
| 4.4 | Font + Size pickers join the row while a text box is selected | any | `--screenshot-state textbox` |

The step at a given width is language-dependent — French labels are longer, so
check 4.1–4.3 in all three languages, not just English.

## 5. Modes and their banners

| # | Mode | Banner | Posed by |
|---|---|---|---|
| 5.1 | Select (default) | none | `--screenshot` |
| 5.2 | **Add text** | "Click where the new text should go — Esc cancels" | `--screenshot-state mode` |
| 5.3 | **Cover** | "Drag over what you want to cover — Esc cancels" | real window (Tools ▸ Cover) |
| 5.4 | Cover — rubber-band rectangle mid-drag | | real window |
| 5.5 | **Place signature** | "Click where the signature should go — Esc cancels" | real window (pick a card) |
| 5.6 | **Inline editor** — TextBox on the page, watermark "Type, then press Enter" | no banner | real window (click a line of text) |
| 5.7 | Inline editor on an **AcroForm field** | no banner | real window (`forms.pdf`) |
| 5.8 | Inline editor re-opened on an added text box (double-click) | no banner | real window |
| 5.9 | **Find bar** — field, Previous, Next, "{n} of {m}", Done | own bar, not a banner | `--screenshot-state find` |
| 5.10 | Find bar — "Not found" | | real window, search for nonsense |
| 5.11 | Tick / checkmark (a plain click, not a mode) | status line only | real window |
| 5.12 | **Restricted banner** — "The owner of this document restricted it…" + Unlock… | | real window, `encrypted.pdf` |

The mode banner pushes the page down about 30 px. Re-measure the page with
`tools/macos-measure-page.py` after arming a mode before mapping any click.

## 6. Busy states, notices and selection chrome

| # | State | Posed by |
|---|---|---|
| 6.1 | **Document busy strip** under the toolbar (label + 3 px indeterminate bar) | `--screenshot-state busy` |
| 6.2 | **Page busy badge** — rounded card at the top of a page | `--screenshot-state busy-page` |
| 6.3 | **Line busy bar** — 3 px bar under the line being checked | `--screenshot-state busy-line` |
| 6.4 | Busy labels: Opening… / Saving… / Checking the saved file… / Checking this page… / Applying… / Searching… / Making a smaller copy… / Preparing to print… / Restoring your edits… | real window, per operation |
| 6.5 | Commands disabled while busy (Save, Print, Add text, Cover, Undo, Redo); a page click is **ignored** | `--screenshot-state busy` |
| 6.6 | **Keyboard focus ring** — dashed accent stroke over accent-subtle fill | `--screenshot-state focus` |
| 6.7 | **Find highlights** — every hit, and the current one in the stronger colour | `--screenshot-state find` |
| 6.8 | **Selection chrome** — accent border + four white corner handles (signature) | real window |
| 6.9 | Quarter-size preview raster before a page renders | real window, scroll fast in a long file |
| 6.10 | Page render failure — "This page couldn't be displayed." | real window, a damaged page |
| 6.11 | Status bar left (`Status`, ellipsised) and right (`Page 3 of 12`) | every shot |

## 7. Errors (all via the status line unless noted)

Tick a row when the message has been *provoked and read* in that language.
The second sentence of a `WithDetail` message is untranslated engine/OS text —
that is by design; note it, don't file it.

| # | Group | Messages |
|---|---|---|
| 7.1 | Open | protected · wrong credential · unreadable · too large · not a valid PDF · error {0} · cancelled · not on the local disk · moved or deleted · cannot be opened for editing from here · unsupported protection |
| 7.2 | Save | Could not save. · read back failed, original untouched · nowhere to save back to · saved but cannot be shrunk from here |
| 7.3 | Editing | change could not be made · scanned image · layout guard ×3 · font cannot write those characters · not one of the three faces |
| 7.4 | Permissions | owner does not allow that · crash recovery is off for protected documents |
| 7.5 | Signatures | could not read that signature · could not read that image · could not save that signature |
| 7.6 | Shrink | save your changes first · reopen before shrinking · pictures already small · could not shrink |
| 7.7 | Print | macOS only · could not print · could not load components · components unavailable · path could not be prepared · document could not be prepared · operation could not be created · Sent to the printer. · Printing cancelled. |
| 7.8 | Page | This page couldn't be displayed. |
| 7.9 | Protection dialog, inline | the empty-entry problem · the mismatch problem |

## 8. Flows to walk with real files (#146 §1b)

Use the review test form (`tools/gen_review_form.py`), the fixtures
(`tools/gen_test_fixtures.py`) and the large files (`~/megapdf-large`).

- [ ] open (Open button, Cmd+O, argv, **Finder double-click**, drop on the Dock icon)
- [ ] scroll (mouse, scrollbar, a 10,000-page file)
- [ ] zoom (−/+, the menu presets, Fit width, Fit page, Actual size)
- [ ] find (a hit, no hit, wrap-around, a phrase across two lines)
- [ ] tick a checkbox (real AcroForm and a drawn square)
- [ ] sign (draw, type, import; place, move, resize, delete)
- [ ] add text
- [ ] cover
- [ ] edit body text
- [ ] undo and redo
- [ ] save, and save as
- [ ] set protection, then remove it
- [ ] shrink
- [ ] print (the dialog opens)
- [ ] close with unsaved changes
- [ ] recovery after the app is killed mid-edit

## 9. Things that look like defects but are not

- Dialogs are **separate centred windows**, not macOS sheets sliding from the title bar.
- On **Delete signature**, Cancel is both the cancel *and* the default button, deliberately — Enter cancels the delete.
- The zoom −/+ stops include 67 %, the zoom **menu** presets do not, so the button can read "67%" with nothing ticked.
- `--screenshot-state unsaved` opens the dialog **non-modally**, unlike the real flow.
- Helvetica / Times / Courier are untranslated on purpose — they are brand names.
- There is no About window and no Help menu on the Mac app.
- Retina is out of scope for 2.0 (Dave, 2026-09-16): the Mac mini has a 1× display.
