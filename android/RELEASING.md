# Releasing MegaPDF for Android

## App identity

| Field | Value |
|---|---|
| **Package name** (applicationId) | `ca.electricrv.megapdf` — locked in by the first AAB uploaded to Play, permanent thereafter |
| Play Console app name | MegaPDF |
| Version scheme | `versionName` = marketing (0.1.0…), `versionCode` = must increase every upload |
| Privacy policy URL | `https://electricrv.ca/megapdf/privacy/` |
| Data safety | Nothing collected, nothing shared; no ads; zero permissions |
| Sibling ids for reference | iOS `com.megapdf.ios` (+ diag `ca.electricrv.megapdf`) · SlyLED `ca.electricrv.slyled` · SlyTab `ca.electricrv.slytab` |

## Signing model

- **Play App Signing** holds the release key (enroll during first upload — it's
  the default). We sign bundles with an **upload key**, which Google can reset
  if lost.
- Upload keystore: `C:\Users\DavidSeaman\.megapdf-keys\upload-keystore.jks`
  (alias `upload`; password in `key-info.txt` next to it — back both up to a
  password manager). CI has it as the repo secrets
  `ANDROID_UPLOAD_KEYSTORE_B64` / `ANDROID_UPLOAD_KEYSTORE_PASSWORD` /
  `ANDROID_UPLOAD_KEY_ALIAS`.

## Cutting a release

1. Bump `versionCode` (must increase every Play upload) and `versionName` in
   `android/app/build.gradle.kts`; commit on `main`.
2. Tag and push: `git tag android-v0.1.0 && git push origin android-v0.1.0`.
3. `android-release.yml` runs unit tests, builds the signed AAB, and attaches
   it to a GitHub release for the tag. Download `app-release.aab` from there.

## First-time Play Console setup (once)

1. [play.google.com/console](https://play.google.com/console) → **Create app**
   (name *MegaPDF*, app, free).
2. **Internal testing** → create release → upload `app-release.aab` → accept
   Play App Signing enrollment.
3. **Testers**: create an email list (start with the Windows testers) and share
   the opt-in link.
4. Required declarations:
   - **Privacy policy:** `https://electricrv.ca/megapdf/privacy/` — the
     Electric RV-hosted policy (issue #25; must be live before submission).
     The interim GitHub Pages policy retires once it is.
   - **Data safety:** no data collected, no data shared — documents and
     signatures never leave the device; the app makes no network calls and
     declares zero permissions.
   - **Content rating:** questionnaire → utility, no user-generated content.
   - **Ads:** none.
5. Roll out the internal-testing release.

## Headless Play submission (one-time service-account setup)

With a Play service account, releases become tag-only: CI (or Claude via the
Play Developer API) uploads the AAB and rolls out the internal track — no
console visit. Setup (adapted from SlyTab's `docs/private/android-play-setup.md`):

1. Play Console → **Setup → API access** → link (or create) a Google Cloud
   project → **Create service account** (opens Google Cloud Console).
2. In Google Cloud: create the service account (name e.g. `play-publisher`),
   no special GCP roles needed. Then **Keys → Add key → JSON** and download.
3. Back in Play Console → API access → the new account → **Grant access** →
   role **Release manager**, scope **account-level** (covers MegaPDF, SlyTab,
   and future apps).
4. Save the JSON as `SplitWise\secrets\play-service-account.json` (the
   documented shared location — gitignored there; never commit it anywhere).
   Optionally also `gh secret set PLAY_SERVICE_ACCOUNT_JSON < file` in this
   repo so `android-release.yml` can gain a `play-submit` step.
5. Tell Claude it exists — the API flow is: OAuth token from the SA key →
   `edits` insert → bundle upload → `tracks/internal` release (versionCode
   from the AAB) → `edits` commit.

Note: the **first** AAB upload for a brand-new app should be done in the
console UI anyway (it locks the package name and enrolls Play App Signing
with confirmation prompts the API skips silently).

## Subsequent releases

Bump versions → tag → download AAB → Play Console → Internal testing → new
release. (Automating the Console upload via a service-account API key is a
possible follow-up in #19.)
