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

MegaPDF has no accounts, no sign-in, no purchases, no subscriptions, no
user-generated content shared with anyone, and makes no network connections.
Nothing in the app is gated: every feature below is reachable immediately on
first launch with no credentials.

**3. What the app does, and for whom**
MegaPDF fills in, checks and signs PDF forms entirely on the device. It is for
people who are sent a PDF form — a rental agreement, a school permission slip, an
insurance or claim form — and need to complete and return it. It replaces the
print → sign → scan → email loop, without the subscription or cloud upload that
comparable apps require. Everything happens locally; the document never leaves the
device.

**4. Setting up and reaching the main features** (no login or credentials exist)
Any PDF works as a sample; the app ships no content of its own and needs none.
A purpose-built blank form is attached to the submission and kept at
`docs/review/MegaPDF-Test-Form.pdf` (regenerate: `python3 tools/gen_review_form.py`):
it has three printed squares, two real AcroForm checkboxes, a signature rule,
and the word "insurance" four times for the search demonstration.
1. Launch the app → Home screen → tap **Open PDF** → the iOS Files picker opens →
   choose any PDF from Files or iCloud Drive.
2. **Check a box:** tap a checkbox or an empty printed square on the page — it is
   marked immediately. Both real AcroForm checkboxes and drawn squares work.
3. **Clear a mark:** tap a marked box again and the mark is removed.
4. **Sign:** tap **Sign** → **Draw** to sign with a finger or Apple Pencil, or
   **Photos** to use a photograph of a signature on paper (the white background is
   removed automatically). Then tap the saved signature and tap the page to place
   it; drag to move it, use the handles to resize.
5. **Search:** tap the magnifier, type a word — every match is highlighted and the
   up/down arrows step through them.
6. **Save:** tap **Save** — the edited PDF is written back to the original file
   in place (a "Saved" confirmation appears); **Save a copy**, in the overflow
   menu, writes a new file through the Files picker instead.

> **Scope note (checked against `ios/MegaPDF/Engine/` on 2026-08-15):** the iOS
> engine does checkboxes (AcroForm widgets and drawn squares), signature stamps,
> search, render and save — there is **no text editing on iOS**. Text editing is a
> Windows-only feature; `TESTING.md` describes the Windows app. Do not tell App
> Review about a feature the build does not have.

**5. External services, tools or platforms**
None. The app makes no network requests at all — no analytics, no crash
reporting, no advertising identifiers, no authentication, no payment processing,
no AI or data-provider services, and no server component of any kind. It embeds
one third-party open-source library, **PDFium** (BSD-3-Clause), which renders and
edits PDFs entirely offline and is compiled into the app. The only outbound link
anywhere in the app is a single "view the source" link on the About screen, which
opens the public GitHub repository in Safari.

**6. Regional differences**
None. The app behaves identically in every region. There is no geo-gating, no
region-specific content, pricing or feature set, and no server that could vary by
region. It is localised in English (en-CA) only.

**7. Regulated industry / protected third-party material**
Not applicable. MegaPDF is a general-purpose document utility. It operates only on
files the user already has and already opened, contains no third-party protected
material, and provides no service in a regulated industry.

**Permissions and privacy**
The app requests no permissions at all, and shows no permission dialogs. Adding a
signature from a photo uses SwiftUI's `PhotosPicker`, which runs out of process
and hands back only the single chosen image, so no photo-library authorisation is
requested and the app never gets library-wide access — the build ships no
`NSPhotoLibraryUsageDescription` because none is needed. There are no location,
contacts, camera or App Tracking Transparency prompts either. No data is collected; privacy policy:
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
