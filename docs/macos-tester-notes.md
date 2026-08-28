# MegaPDF for Mac — what to test

The build is **notarized and sandboxed**, so it opens by double-click like any
other app. No right-click-Open, no `xattr` incantation. If macOS refuses to open
it, that is itself a finding worth reporting.

It is the same configuration the Mac App Store build ships in — Developer ID
rather than Store signing, but the same sandbox — so what you see here is what
Store users will get.

## Please spend your time on these

CI already proves the engine works: 135 tests and 44 end-to-end checks run
inside the shipped bundle on every build, covering open, tick, fill, sign, type,
cover, find, save, reopen and undo. **Repeating that by hand is not the best use
of your time.** These are the things no automated check can reach.

**1. Recent documents across launches.** Open a PDF, quit the app completely,
reopen it, and pick that file from the Recent list on the welcome screen.

*Why it matters:* under the sandbox a stored file path grants nothing — the app
has to hold a security-scoped bookmark instead. That code has never run
anywhere. If the file opens, bookmarks work. If it says the file has moved or
cannot be opened for editing, they do not.

**2. Save back over the original.** Open a PDF from somewhere ordinary
(Documents, Desktop, a Downloads folder), tick a checkbox, press ⌘S, then reopen
the file and confirm the tick is still there.

*Why it matters:* the sandbox grants access to the file you picked, not its
folder, so saving uses a different mechanism from the Windows app's. A Save that
appears to work but does not change the file is the failure to watch for.

**3. Printing.** Open a document, press ⌘P.

*Why it matters:* the print panel is driven through macOS's own printing
components by hand-written interop. CI can prove the wiring is sound but cannot
open a panel. Does the panel appear, does the preview look right, and does a
printed or PDF-exported page match what is on screen?

**4. Does it feel like a Mac app?** This is a judgement only a person can make.

- Are the shortcuts right? ⌘O, ⌘S, ⌘P, ⌘Z, ⌘⇧Z, ⌘F, ⌘+/−/0. **Ctrl instead of
  ⌘ anywhere is a bug.**
- Is text crisp on a retina display, at 100% and zoomed in?
- Does scrolling feel normal — momentum, trackpad, a long document?
- Does the app follow the system light/dark setting?

## Known and deliberate

- **The toolbar is words, not icons.** The Windows app uses a Windows-only icon
  font. Real icons are still to come; empty boxes would have been worse.
- **Placed signatures cannot yet be dragged or resized.** Clicking one removes
  it. Undo brings it back.
- **Added text cannot be dragged.** Clicking it removes it.
- **Shrink is disabled until you save.** It works on the saved file, deliberately,
  so it can never degrade what is open in front of you.

## Reporting

The status bar along the bottom says what just happened — if something goes
wrong, that line is usually the most useful thing to quote. Please say which Mac
and which macOS version, and whether it is Apple Silicon or Intel.
