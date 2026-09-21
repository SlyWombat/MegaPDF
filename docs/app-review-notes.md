# App Review Information — MegaPDF iOS

Apple rejected submission `17d75c27-bffc-4e94-b0de-a91ad6e16591` (2026-08-14) under
**Guideline 2.1 — Information Needed**. Nothing was reported as broken.

The Notes field was **not** empty — it held 357 characters covering roughly items 3
and 4:

> MegaPDF is a local-only PDF form filler: no account, no server, no network
> access. To test: open any PDF via the Files picker (any form with checkboxes
> works - e.g. an IRS form), tap a checkbox to check it, tap Sign -> Draw to create
> a signature, tap the page to place it, then Save. The app writes back to the
> original file via the system file coordinator.

That was accurate but far short of Apple's checklist: no screen recording, no list
of devices tested, no statement about external services, regions or regulated
material. The replacement below answers all seven points explicitly, because a
2.1 rejection is answered by completeness, not brevity.

Paste the block below into **App Store Connect → your version → App Review
Information → Notes** (replacing what is there), and reply to the Resolution
Center message with the same text plus the screen recording. Keeping it here means
the next submission starts with it already written.

---

## Notes field text

MegaPDF has no accounts, sign-in, purchases, subscriptions or shared user content,
and makes no network connections. Every feature below is reachable on first
launch with nothing to enter.

**3. What the app does, and for whom**
MegaPDF fills in, checks, signs and corrects PDF forms entirely on the device, for
people sent a form (a rental agreement, a permission slip, a claim form) who need
to complete and return it. The document never leaves the device.

**4. Reaching the main features**
Any PDF works. The form in the attached recording is at
https://github.com/SlyWombat/MegaPDF/blob/main/docs/review/MegaPDF-Test-Form.pdf
1. Home → **Open PDF** → the Files picker → choose a PDF.
2. **Check a box:** tap a checkbox or an empty printed square; tap again to clear.
3. **Sign:** **Sign** → **Draw** (finger or Apple Pencil) or **Photo** (a photo of
   a signature on paper; the background is removed) → tap the saved signature →
   tap the page to place it; drag to move, handles to resize.
4. **Add text:** **Add text** → tap a blank line → type, choose size and font →
   **Add**. Tap it again to move it or fix a typo.
5. **Change the document's own text:** tap a line of printed text, retype it,
   **Save**. It keeps its size and font; Undo restores the original exactly.
6. **Search:** the magnifier → type a word; the arrows step through the matches.
7. **Save:** **Save** writes back to the original file ("Saved" confirms);
   **Save a copy** (under **More**) writes a new file through the Files picker.
8. **Redact:** **More** (the ⋯ button) → **Redact** → drag across what must come
   out. A mark can be tapped to select it, then moved, reshaped, or taken off
   with its × (**Clear all marks** is under **More**); Undo puts it back. Then
   **Save**: a confirmation says redaction permanently removes the content and
   offers **Overwrite the original** or **Save as a copy**, then says what was
   removed. The content is taken out of the file, not covered.
9. **Protect:** **More** → **Password…** sets, changes or removes a
   document password. A protected PDF asks for it when opened.

**1. Screen recording**
Attached: MegaPDF 2.1.0 on an iPhone 17 Pro Max simulator, iOS 26.5: cold
launch, open the form from Files, tick and clear boxes, draw and place a
signature, search, save, reopen from Recents, and the Photos picker opening and
closing. Steps 8 and 9 are not in it; they need only an open PDF.

**2. Devices and OS versions tested**
iPhone 17 Pro Max and iPad Pro 13-inch (M5) simulators on iOS 26.5.

**5. External services, tools or platforms**
None. No network requests: no analytics, crash reporting, advertising, sign-in,
payments, AI services or server. One embedded open-source library, PDFium
(BSD-3-Clause), works offline. The only outbound link is "view the source" on
the About screen, which opens GitHub in Safari.

**6. Regional differences**
None. English (en-CA) and French (fr-CA, fr), following the device language.

**7. Regulated industry / protected third-party material**
Not applicable: a general-purpose document utility working on the user's own
files.

**Permissions and privacy**
No permissions are requested and no permission dialogs appear. Signing from a
photo uses SwiftUI's PhotosPicker, which runs out of process and returns only the
chosen image, so no photo-library access is requested. No data is collected;
privacy policy: https://electricrv.ca/megapdf/privacy/

---

## Notes field text (macOS)

MegaPDF for Mac is the same app as the iPhone version, on the same record: no
accounts, no sign-in, no purchases, no subscriptions, no user-generated content
shared with anyone, and no network connections. Every feature is reachable on
first launch with nothing to enter.

**What it does, and for whom**
Fills in, checks and signs PDF forms entirely on the Mac, for people who are
sent a form and need to complete and return it. The document never leaves the
machine. It is sandboxed and opens only the files the user chooses.

**Reaching the main features**
Any PDF works; the blank form used in the attached recording is at
https://github.com/SlyWombat/MegaPDF/blob/main/docs/review/MegaPDF-Test-Form.pdf
1. Launch → **Open** (or ⌘O) → choose a PDF.
2. **Check a box:** click a checkbox or an empty printed square — it is marked
   at once; click again to clear it.
3. **Sign:** **Sign** → draw a new signature, or use a photo of one (the white
   background is removed) → click the saved signature → click the page to place
   it; drag to move, handles to resize.
4. **Add text:** **Add text** → click a blank line → type → Return; the font
   and size pickers in the toolbar apply to it.
   **Change the document's own text:** with no tool selected, click a line of
   printed text, retype it in place, Return. It keeps its size and font; ⌘Z
   restores the original exactly.
5. **Cover:** paints white over anything, by dragging, and says that it only
   covers. **Redact** is the tool that removes: drag over what has to come out,
   then Save; a confirmation offers **Overwrite the original** or **Save as a
   copy** and then says what was removed. The content is gone from the file.
6. **Find:** ⌘F → type → Return steps through the matches.
7. **Save:** ⌘S writes back to the file that was opened; **Save As** writes a
   copy. **File → Password…** sets, changes or removes a password; **File →
   Save a smaller copy for email** makes a smaller copy. **Print** (⌘P) uses the standard macOS print panel.

**Screen recording**
Attached: the flow above on a Mac mini (M4) running macOS 26.6.2 — ticks, the
signature placed, text added, find.

**Devices tested**
Mac mini (M4), macOS 26.6.2; the sandboxed Store build is exercised end to end
inside its container on every CI run (open, tick, fill, sign, type, cover,
find, save, reopen, undo).

**External services** None. No network requests, analytics, crash reporting,
advertising, authentication, payment or server component. One embedded
open-source library, PDFium (BSD-3-Clause), compiled in. The only outbound link
is "view the source" on the About panel, which opens GitHub in the browser.

**Regional differences** None. English (en-CA) and French (fr-CA, fr); the
language follows the system setting.

**Regulated industry / third-party material** Not applicable: a general-purpose
document utility working on the user's own files.

**Permissions and privacy** Sandboxed. Entitlements: user-selected file access,
printing, and `com.apple.security.cs.allow-jit`, which the .NET runtime needs to
compile its code; nothing else. No data is collected; privacy policy:
https://electricrv.ca/megapdf/privacy/

---

## Item 2 — devices and OS tested (needs filling in)

Apple wants **physical** devices. Fill in what was actually used, e.g.:

> Tested on iPhone <model> running iOS <version> and iPad <model> running
> iPadOS <version>, both via TestFlight build 27.

For the record, what CI covers: the iOS build and unit tests run on macOS runners
against the simulator; the App Store build (0.2.8, build 27) is the artifact under
review. Minimum deployment target is **iOS 16.0**; the app is universal
(`TARGETED_DEVICE_FAMILY = "1,2"`, iPhone and iPad).

## Item 1 — the screen recording (needs a device)

Must be captured on a physical device on the latest OS, starting from launch, in
one take. MegaPDF has no accounts, purchases, UGC or sensitive-data prompts, so
only the core flow is needed:

1. Launch from the home screen — show the app opening cold.
2. Home screen → tap **Open PDF** → pick a PDF in Files.
3. Tap two checkboxes/squares — show them marked.
4. Tap one marked box a second time to clear it, then tap it again.
5. Tap **Sign** → **Draw** → sign → save → tap the signature → tap the page to
   place it → drag it onto the signature line.
6. Tap the magnifier → search a word → step through matches with the arrows.
7. Tap **Save** → the "Saved" confirmation appears → **Close** → reopen the file
   from the Recents list on the home screen to show the edits persisted.
8. Tap **Sign → Photos** once so the system photo picker appears, then close it.
   Note for whoever films this: **no permission dialog will appear** — `PhotosPicker`
   is out-of-process. The app has no permission prompts to record.

Keep it unhurried; a couple of minutes is fine.
