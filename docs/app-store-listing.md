# App Store listing — paste-ready copy (MegaPDF for iOS)

Everything App Store Connect asks for at submission, in order. Fields with
character limits show the count in brackets.

---

## App Information

| Field | Value |
|---|---|
| **Name** [30] | `MegaPDF` |
| **Subtitle** [30] | `Fill, check & sign PDFs` |
| **Primary category** | Productivity |
| **Secondary category** | Business |
| **Content rights** | Does not contain, show, or access third-party content |
| **Age rating** | Answer **No** to every question → **4+** |
| **Copyright** | © 2026 David Seaman |

## URLs

| Field | Value |
|---|---|
| **Support URL** | `https://github.com/SlyWombat/MegaPDF` |
| **Marketing URL** (optional) | `https://github.com/SlyWombat/MegaPDF` |
| **Privacy Policy URL** | `https://slywombat.github.io/MegaPDF/privacy.html` |

## Promotional Text [170]

> Someone emailed you a PDF to sign? Open it, tap the boxes, drop in your
> signature, save. Done in under a minute — no account, no subscription.

*(156 characters)*

## Description [4000]

> **Open. Fix. Save. Done.**
>
> MegaPDF does the one job most people actually have with a PDF: someone sent
> you a form, and you need to send it back filled in, checked off, and signed.
> No account. No subscription. No cloud. Everything happens on your device.
>
> **Check any box**
> Tap a checkbox and it's checked — real interactive form fields and plain
> printed squares alike. MegaPDF recognizes drawn checkboxes that other apps
> treat as decoration.
>
> **Sign like you mean it**
> Draw your signature with a finger, or photograph the one on paper — the
> white background disappears automatically. Your signatures stay in a private
> library on your device; drop one onto any document, move and resize it until
> it sits right on the line.
>
> **Save without fear**
> Save writes back to the original file — safely. MegaPDF verifies every
> document before it touches your original, so a failed save can never corrupt
> the file someone sent you. Or keep the original and save a copy.
>
> **Private by design**
> MegaPDF requests zero permissions and makes zero network connections. Your
> documents and your signature never leave your device — there is no server
> for them to go to. The app is open source, so anyone can verify that.
>
> **Works with everything**
> Open PDFs from Mail, Files, iCloud Drive, Google Drive, or any app that
> shares files. Documents you fill and sign here open perfectly in Adobe
> Acrobat, desktop PDF apps, and MegaPDF for Windows and Android — same
> engine, same result, on every platform.
>
> MegaPDF is deliberately simple. It doesn't rearrange pages, run OCR, or
> bury you in toolbars. It opens, it fixes, it saves. Done.

*(~1,600 characters — room to grow)*

## Keywords [100]

> `pdf,sign,signature,fill,form,checkbox,esign,editor,document,annotate,fill and sign`

*(84 characters, comma-separated)*

## What's New (version 0.1.0)

> First release. Open PDFs from anywhere, tap checkboxes to check them (form
> fields and printed squares alike), sign with a drawn or photographed
> signature, and save safely back to the original file. No account, no
> tracking, no network access — everything stays on your device.

## App Privacy (Data Collection)

Select **"Data is not collected"** for every category. Justification if asked:
the app makes no network connections at all (it requests no network
entitlements), has no analytics SDKs, no accounts, and processes documents
entirely on-device.

## Screenshots

Run the **iOS Screenshots** workflow (Actions → iOS Screenshots → Run
workflow), download the `appstore-screenshots` artifact, and upload:

| File | Slot | Suggested caption (optional overlay text) |
|---|---|---|
| `iphone-6_9-viewer.png` | iPhone 6.9" #1 | *Checked and signed in under a minute* |
| `iphone-6_9-sign.png` | iPhone 6.9" #2 | *Your signatures, saved on your device* |
| `iphone-6_9-draw.png` | iPhone 6.9" #3 | *Draw it once, use it everywhere* |
| `iphone-6_9-home.png` | iPhone 6.9" #4 | *No account. No cloud. No tracking.* |
| `ipad-13-*.png` | iPad 13" #1–4 | same order |

Order matters: the viewer shot (a filled, signed agreement) leads.

## App Review Information

| Field | Value |
|---|---|
| Sign-in required | **No** — there are no accounts |
| Contact | dave@drscapital.com |
| Notes | see below |

> MegaPDF is a local-only PDF form filler: no account, no server, no network
> access. To test: open any PDF via the Files picker (any PDF with checkboxes
> works — e.g. an IRS form), tap a checkbox to check it, tap Sign → Draw to
> create a signature, tap the page to place it, then Save. The app writes
> back to the original file via the system file coordinator.

## TestFlight

**Beta App Description:**
> MegaPDF fills, checks, and signs PDF forms entirely on your device — no
> account, no cloud. This beta covers the full loop: open, check boxes, place
> a drawn or photographed signature, and save back to the original file.

**What to Test:**
> 1. Open a PDF from Mail or Files (real forms with checkboxes are best).
> 2. Tap printed checkbox squares — do they get an ✗? Tap again to remove.
> 3. Sign → Draw a signature, place it, drag/resize it, save.
> 4. Reopen the saved file in another app (Files preview, Acrobat) — is
>    everything where you put it?
> 5. If you also use MegaPDF on Windows or Android: sign there, open here —
>    the signature should be movable on both.

---

*Android/Play counterpart: `android/RELEASING.md`. Windows/Microsoft Store
copy: the Store runbook. Keep the three tellings of the story consistent.*
