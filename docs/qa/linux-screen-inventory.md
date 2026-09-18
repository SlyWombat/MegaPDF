# Linux app — screen inventory and QA checklist (#158)

Every window, dialog, menu, mode, notice, busy state and error the Avalonia Linux app
can show. Tick a row when it has been *seen* — in the language, theme and window width
the pass is covering — not merely when the code that would show it exists.

Run the pass once per **language** (en, fr-CA, fr-FR) × **theme** (light, dark), and the
layout-sensitive rows again at each **width** (1280, 1000, 800, 480 = the minimum,
`MainWindow.axaml:10`).

**Look for:** clipping, overlap, untranslated text, the wrong theme, broken layout, and
text that runs into or under a neighbour.

The 2026-09-18 pass is recorded in the **Seen** column: ✅ captured and looked at,
**hands** for the rows that need a pointer and so were not reachable, and a note where
something else stood in.

## How to pose each screen

The app renders its own window to a bitmap rather than letting anything photograph the
screen, so no compositor grant is involved and the capture is the window alone.

```
. tools/../<session with X, a window manager, dbus and the portal>
APP=artifacts/linux/MegaPDF/bin/MegaPDF          # or, from the package:
FP="flatpak run --user --filesystem=$FIX:ro --filesystem=$OUT \
      --command=/app/lib/megapdf/MegaPDF ca.electricrv.MegaPDF"

$APP --window 1280x800 --language fr-CA --theme dark \
     --screenshot-state more --screenshot out.png "$FIX/demo-fr.pdf"
```

`--screenshot-state` accepts `sign find focus more textbox redact busy busy-page
busy-line unsaved mode about notices` (`App.axaml.cs:37`). `unsaved`, `about` and
`notices` render their dialog beside the window to `<out>-dialog.png`. `sign` needs
`--signature <png|jpg>` when the library is empty and the app is not running from a
checkout. Everything else in the tables below has no pose and needs a real pointer.

Because the app renders its own window, **a capture cannot depend on which window
manager is running**: the GNOME and KDE sets would be identical by construction. What
the second desktop is for is everything around the window — see §10.

---

## 1. Windows

| # | Screen | How to reach it | Posed by | Seen |
|---|---|---|---|---|
| 1.1 | Empty state — title, tagline, "Open a PDF…", Recent list with each file's location | launch with no file | `--screenshot` with no pdf | ✅ |
| 1.2 | Document open, Select mode | open any PDF | `--screenshot` | ✅ |
| 1.3 | Document open at the minimum width (480×360) | drag the window in | `--window 480x360` | ✅ |
| 1.4 | Restricted banner + **Unlock…** | open an owner-restricted document (`encrypted.pdf`) | real window | hands |
| 1.5 | Window title dirty bullet (`•`) | make any edit | `--screenshot-state unsaved` | ✅ |
| 1.6 | Window title and `WM_CLASS` | any window | `wmctrl -l -x` | ✅ §10 |

## 2. Dialogs (separate windows, centred on the owner)

| # | Dialog | Buttons | How to reach it | Posed by | Seen |
|---|---|---|---|---|---|
| 2.1 | **Unsaved changes** — "Do you want to save the changes made to the document "{0}"?" | Don't Save / Cancel / **Save** | edit, then Close or Ctrl+Q | `--screenshot-state unsaved` → `<out>-dialog.png` | ✅ |
| 2.2 | **Restore unsaved work** (recovery) | Discard them / Decide later / **Restore** | kill the app mid-edit, relaunch | real window | journal verified, §9 |
| 2.3 | **Key needed** to open a protected file | Cancel / **Open** | open a user-protected PDF | real window | hands |
| 2.4 | **Unlock document** (owner credential) — same window, relabelled | Cancel / **Unlock** | **Unlock…** in the restricted banner | real window | hands |
| 2.5 | Retry text after a wrong entry | Cancel / **Open** | type the wrong thing | real window | hands |
| 2.6 | **Document protection — manage panel** | Cancel / Remove / Change… | File ▸ Protection… on a protected file | real window | hands |
| 2.7 | **Document protection — entry panel** (New / Confirm) | Cancel / Set \| Change | File ▸ Protection… on an unprotected file | real window | hands |
| 2.8 | Entry-panel inline problems — empty and mismatch | — | commit an empty or mismatched pair | real window | hands |
| 2.9 | **Add a signature** (draw pad) | Clear / Cancel / **Save** | Sign ▸ Draw | real window | hands |
| 2.10 | **Type your name** — live script preview | Cancel / **Add** | Sign ▸ Type | real window | hands |
| 2.11 | **Rename signature** | Cancel / **Rename** | signature card ⋯ ▸ Rename… | real window | hands |
| 2.12 | **Delete signature** | Cancel / Delete (danger) | signature card ⋯ ▸ Delete… | real window | hands |
| 2.13 | **Print** — which printer, how many copies | Cancel / **Print** | Print, with a CUPS queue present | real window | hands; the CUPS route is §8 |
| 2.14 | **About MegaPDF** — icon, version, copyright, GitHub link, Third-party notices… | — | Help ▸ About | `--screenshot-state about` | ✅ |
| 2.15 | **Third-party notices** — the licence text | — | About ▸ Third-party notices… | `--screenshot-state notices` | ✅ |

## 3. Menus

| # | Menu | Contents | Seen |
|---|---|---|---|
| 3.1 | The menu bar, in the window | File / Edit / View / Tools / Window / Help (`MainWindow.MenuBar.cs`) | ✅ asserted by `--self-test` |
| 3.2 | Every toolbar, More and zoom command reachable from it | 22 commands | ✅ asserted by `--self-test` |
| 3.3 | **More** (•••) — Save As, Password…, Print, Shrink, Options, plus anything overflowed | | ✅ at 1280/1000/800/480 |
| 3.4 | Zoom menu — the presets, Fit width, Fit page, Actual size | | hands (the flyout is its own window) |
| 3.5 | Signature card ⋯ — Rename…, Delete… | | hands |
| 3.6 | Recent item context menu — Show in file manager | | hands |

Command key is **Ctrl** (`Shortcut(...)`, `MainWindow.MenuBar.cs:42`); Redo is **Ctrl+Y**
off macOS, not Ctrl+Shift+Z.

## 4. Toolbar layout steps

| # | Width | What the toolbar does | Seen |
|---|---|---|---|
| 4.1 | 1280 | every command with its label; zoom −, level, + | ✅ |
| 4.2 | 1000 | labels still on in English and French | ✅ |
| 4.3 | 800 | labels gone, icons only; zoom fits to 94 % | ✅ |
| 4.4 | 480 (minimum) | icons only and commands moved into More; Open never leaves | ✅ |
| 4.5 | the font and size pickers, while a text box is selected | they take a place in the row down to 800 | ✅ |

## 5. Modes and their banners

| # | Mode | Banner | Seen |
|---|---|---|---|
| 5.1 | Add text | "Click where the new text goes — Esc cancels" | ✅ `--screenshot-state mode` |
| 5.2 | Sign — placing | "Click where the signature goes — Esc cancels" | ✅ via `sign` then place |
| 5.3 | Cover | "Drag across what you want to hide. Esc cancels. Cover removes nothing: Redact does" | hands |
| 5.4 | **Redact** (#173) | "Drag across what you want removed, or select text — Esc cancels", with a mark placed | ✅ `--screenshot-state redact` |
| 5.5 | A selected text box — move, edit, delete hint on the status line | | ✅ `--screenshot-state textbox` |
| 5.6 | The keyboard focus ring on a page region | | ✅ `--screenshot-state focus` |

## 6. Busy states, notices and selection chrome

| # | State | Seen |
|---|---|---|
| 6.1 | Busy strip under the toolbar ("Checking the saved file…"), toolbar disabled | ✅ `busy` |
| 6.2 | Page-level spinner ("Checking this page…") | ✅ `busy-page` |
| 6.3 | Line-level spinner | ✅ `busy-line` — the toolbar disables; the indicator itself is too brief to catch in a still |
| 6.4 | Search hits — cyan for every match, brand blue for the current one, "1 of 3" | ✅ `find`, en and fr |
| 6.5 | Restricted-document banner | hands |

## 7. Errors (all via the status line unless noted)

Not reachable without a pointer or a broken file to hand; the strings are in
`Strings.resx` and the shared wording is asserted by the string-catalogue tests. The
Linux-specific ones worth naming:

| # | Where | What it says |
|---|---|---|
| 7.1 | Print, in a Flatpak | printing needs the portal route, which is not built — the app detects `/.flatpak-info` and says so rather than failing obscurely |
| 7.2 | Save | "Could not save. Writing the PDF failed." — the #193 case; a large save no longer reaches it |
| 7.3 | Startup, no writable data directory | "MegaPDF could not create the folder it keeps your settings in: …" (#195) |
| 7.4 | Page | "This page couldn't be displayed." |

## 8. Flows walked with real files (#158)

The review form (`tools/gen_review_form.py`), the fixtures (`tools/gen_test_fixtures.py`,
`tools/gen_redaction_fixtures.py`) and the large set (`tools/gen_large_fixtures.py`).

Rows marked **self-test** are driven through the real `MainViewModel` by
`MegaPDF --self-test <fixtures>`, which is the same code path a click takes and asserts
the outcome rather than photographing it.

- [x] open — argv, the installed launcher on PATH, and from inside the Flatpak sandbox
- [x] scroll — every page of a 1,000-page and a 10,000-page document (battery, §11)
- [x] zoom — a preset applies and the button reads it (**self-test**); fit at 94 % seen at 800 px
- [x] find — a hit, no hit, wrap-around, case-insensitivity, the counter (**self-test**), and posed in three languages
- [x] tick a checkbox — a real AcroForm widget and a drawn square (**self-test**)
- [x] sign — place, size, aspect, centring, undo (**self-test**); the library flyout posed
- [x] add text — placement, the face and size pickers, restyle, undo (**self-test**)
- [x] cover (**self-test**)
- [x] **redact** (#173) — arm, mark, the guard's verdict, save-with-marks carries none, apply (**self-test**), and posed
- [x] edit body text — 22,267 line edits across the corpus with 0 changes against the baseline (§11)
- [x] undo and redo (**self-test**)
- [x] save, and save as (**self-test**, including a save that fails while making the bytes and leaves the original untouched)
- [ ] set protection, then remove it — **hands**: the dialog is the only way in. The engine side is covered by `SecurityTests` in the Core suite.
- [x] shrink — availability re-evaluated after a flatten (**self-test**)
- [x] print through CUPS — a queue made, `lp` accepted the document, the app's `--print-check` reads the queue back (§10)
- [x] close with unsaved changes — the dialog, and the journal kept (D1) (**self-test**)
- [x] recovery after a kill — a journal is written while the edit is live, survives `kill -9`, and the app relaunches with it present (§10)

### Peak memory, against the Windows figures on #147

App, opening the document and rendering the first page (`/usr/bin/time -v`, 1440×900):

| document | file | Linux app peak RSS | ratio |
|---|---:|---:|---|
| `demo.pdf` (1 page) | 0.1 MB | 214 MB | — |
| `deep-10000.pdf` (10,000 pages) | 6 MB | 263 MB | — |
| `big-1gb.pdf` (400 pages) | 1,049 MB | 222 MB | 0.21x |
| `huge-2_5gb.pdf` (1,000 pages) | 2,685 MB | **228 MB** | **0.09x** |

A 2.5 GB document costs **14 MB more than a one-page one**. Windows on #147 was a 266 MB
working set at open on the same file, peaking at 441 MB through a scroll to page 1000.

Engine, through the whole battery on one document — every page rendered, zoom extremes,
whole-document search, save and reopen:

| document | peak | ratio |
|---|---:|---|
| `huge-2_5gb.pdf` | 189 MB | 0.07x |
| `big-1gb.pdf` | 126 MB | 0.12x |

## 9. Packaging and the sandbox

| # | Check | Seen |
|---|---|---|
| 9.1 | The Flatpak's granted permissions are `ipc`, `x11`, `dri` and nothing else | ✅ |
| 9.2 | A file in the real home is **not** readable from inside the sandbox | ✅ |
| 9.3 | `org.freedesktop.portal.FileChooser` answers from inside the sandbox | ✅ |
| 9.4 | Which provider a dialog actually uses | **hands** — Avalonia resolves portal-or-fallback when a dialog opens, and no startup inspection can tell them apart |
| 9.5 | The third-party notices are in the package, beside the binary | ✅ |
| 9.6 | The engine loads from `$ORIGIN` and nothing else | ✅ |
| 9.7 | The `.deb` installs on a machine with no .NET, ICU or X libraries | ✅ |
| 9.8 | The `.desktop` entry validates, offers `application/pdf`, and claims nothing | ✅ |
| 9.9 | All nine hicolor icon sizes and the scalable icon are installed | ✅ — **this was broken; see the defects** |

## 10. What the second desktop is for

GNOME and KDE were both run, as **mutter** and **kwin_x11** under Xvfb with dbus and
`xdg-desktop-portal` — real window managers and the real portal, not `gnome-shell` or
`plasmashell`, which want systemd, logind and a seat. Window management, decorations,
placement, focus, fonts, theming and the portal are genuine; a desktop's own panel,
overview and file dialog *chrome* are not there to be tested.

Under each: the window is mapped and managed, `WM_CLASS` is `MegaPDF.MegaPDF` — which is
what `StartupWMClass=MegaPDF` in the desktop entry claims, so the running window sits
inside the launcher icon rather than beside it — the app reports the right desktop,
`--self-test` and `--render-check` pass, and a document renders.

**What this does not cover:** the GNOME and KDE *file dialogs* as a person sees them,
the shell's own presentation of the app, and anything that needs a pointer. Those want
a real desktop session on a real machine, and the rows marked **hands** above are the
list to walk when there is one.

## 11. The battery

`MegaPDF.Stress run --root <corpus> --extra-root <large set> --phases edits`, 4,337
corpus documents plus the 13 generated large ones, 12 workers, on the #157 harness.

Against the stored `core-edits-p25` baseline: **document outcome changes: none;
22,267 edits compared, 0 changes**; no read-back failures, no crashes, no hangs.
Outcomes `{'ok': 4263, 'format': 62, 'encrypted': 12}` on the shared documents, and all
13 large files `ok` — including `huge-2_5gb.pdf`, which no battery could open before #157.

## 12. Defects this pass found

All three are fixed on the branch that carries this file.

1. **The `.deb`'s icons were installed outside any icon theme.** Every size landed at
   `/usr/share/icons/48x48/apps/megapdf.png`, with no `hicolor` between. No icon theme
   has a directory there, so nothing would ever have found the icon: the menu entry and
   every PDF file would have shown a generic one. A `cp -R` into a destination that did
   not exist yet; the fallback meant to catch it could not, because `cp` had succeeded.
2. **The find screen could not be posed in French.** `--screenshot-state find` searched
   for the literal `"equipment"`; the French demo agreement says `"équipement"`. The
   check refused to write a shot that would have claimed to show something it did not,
   so the screen had never been photographed in French **on any platform**.
3. **The empty state's tagline was left-aligned the moment it wrapped.** Only
   `HorizontalAlignment` was set, not `TextAlignment`. In English the sentence fits on
   one line at every width. In French it wraps at every width including the default
   1280, and its second line sat flush left under a centred title and a centred button.

## 13. Things that look like defects but are not

- **"Échap" reads as "Echap" in the mode banner at 1×.** The accent is there: at 13 px
  in DejaVu Sans the acute is two pixels tall and hinting flattens it to a dash. Every
  application on the machine draws it that way; magnifying the glyph shows it present
  and correctly placed, and the string in the catalogue has the É.
- **At 480 px the French and English toolbars are pixel-identical.** The toolbar has
  overflowed to icons alone by then, so there are no words in it to translate.
- **The third-party notices window is in English in a French run.** It is the licence
  text, reproduced as the licences require.
- **`xdg-mime query default application/pdf` answers `megapdf.desktop` after installing
  the `.deb`.** On a machine with no other PDF handler and no `mimeapps.list`, that
  query names the only candidate. Installing writes MegaPDF into no `mimeapps.list`:
  it offers, it does not claim, which is what `LSHandlerRank=Alternate` says on the Mac.
- **There is no Wayland backend.** `Avalonia.X11` is the only Linux windowing backend in
  the published output, so a Wayland session runs through XWayland.
- **Helvetica / Times / Courier are untranslated in the font picker** — they are brand
  names.
