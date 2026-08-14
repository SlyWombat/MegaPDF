# ElectricRV Mobile App Blueprint — iOS & Android, zero to both stores

The distilled, battle-tested recipe for shipping a native iOS + Android app in
this environment: Windows/WSL dev box, **no Mac ever**, GitHub Actions as the
build machine, everything automatable done via store APIs. MegaPDF
(github.com/SlyWombat/MegaPDF) is the reference implementation — every file
named below exists there, working. SlyLED and SlyTab contributed the earlier
lessons; this document supersedes reading them piecemeal.

---

## 1. House conventions

| Thing | Convention |
|---|---|
| Bundle/package ids | `ca.electricrv.<product>` (SlyLED, SlyTab, MegaPDF-Android follow it; MegaPDF-iOS predates it as `com.megapdf.ios`) |
| Branding / copyright / data controller | **Electric RV (Ontario, Canada)** — never a personal name |
| Privacy policy | Per-product page on electricrv.ca (`/product/privacy/`), Electric RV framing, PIPEDA + Law 25 + GDPR + CCPA section; **must be live before any store sees the URL** |
| Privacy contact | `privacy@electricrv.ca` — an **alias on dave@electricrv.ca in Microsoft 365** (mail is NOT on cPanel; MX → outlook.com) |
| Product web presence | Sub-site `electricrv.ca/<product>/` + teaser block on the main page (see §7) |
| Versioning | Marketing `x.y.z` from git tags (`ios-v*` / `android-v*`); build numbers from `$GITHUB_RUN_NUMBER` (iOS `CURRENT_PROJECT_VERSION`, Android `versionCode` — must strictly increase) |
| Release model | **Tag-and-forget**: pushing a tag builds, signs, and delivers to TestFlight / Play internal track with no human steps |

## 2. Credential inventory (what exists, where it lives)

Never print or commit any of these. Pipe files straight into `gh secret set`.

| Credential | Location | Notes |
|---|---|---|
| **Apple team ASC API key** | `SlyTab/secrets/AuthKey_QUC9SR2G3F.p8`; issuer id in `SlyTab/.env` (`APPLE_ASC_ISSUER_ID`) | Team key, works for provisioning + uploads + full ASC API. Repo secrets: `ASC_KEY_ID` / `ASC_ISSUER_ID` / `ASC_KEY_P8` |
| **Apple Team ID** | `V97FBD9SXN` | ⚠️ `APPLEDEVID` in SlyTab/.env is a DIFFERENT identifier — do not use it as the team id. Read the real one off any signing cert identity |
| **Apple Distribution cert + App Store profile** | Minted **via ASC API** per app (CSR with openssl → `POST /v1/certificates` type DISTRIBUTION → `POST /v1/profiles` type IOS_APP_STORE). Local copies in `~/.megapdf-keys/ios-dist/` pattern; secrets `IOS_DIST_P12_B64` / `IOS_DIST_P12_PASSWORD` / `IOS_DIST_PROFILE_B64` | Expire yearly — calendar it. p12 via `openssl pkcs12 -export` (add `-legacy` if macOS rejects it — SlyLED lesson) |
| **Android upload keystore** | Per-product, generated with Android Studio's bundled keytool (`/mnt/c/Program Files/Android/Android Studio/jre/bin/keytool.exe` — no JDK in WSL). Kept outside OneDrive (`C:\Users\...\.megapdf-keys\`); secrets `ANDROID_UPLOAD_*` | Play App Signing holds the real key; upload keys are resettable |
| **Play service account** | `SlyTab/secrets/play-service-account.json` (`play-publisher@electricrv-play.iam.gserviceaccount.com`); repo secret `PLAY_SERVICE_ACCOUNT_JSON` | Account-level access → works for ALL Play apps. ⚠️ Play Console's old "API access" page is GONE — service accounts are invited via **Users and permissions** like a human |
| **cPanel (website deploys)** | `Lighting Arduino/.env` (`CPANEL_HOST/PORT/USER/TOKEN`) | UAPI client pattern: that repo's `server/deploy.py` |
| **ASC API JWT helper** | `SlyTab/scripts/ops/asc-api.sh` (GET/POST/PATCH/DELETE any ASC endpoint) | Run with `PATH=/usr/bin:/bin` — the Windows Python shim breaks it |

## 3. iOS pipeline (the shape that works)

**Project**: XcodeGen — commit `ios/project.yml`, gitignore the `.xcodeproj`.
Native deps fetched+assembled by a script (`ios/scripts/fetch-pdfium.sh`
pattern), gitignored under `ios/Vendor/`, cached in CI by a versioned key.

**Load-bearing project.yml settings** (each earned through a rejection):
```yaml
GENERATE_INFOPLIST_FILE: YES
INFOPLIST_KEY_UILaunchStoryboardName: LaunchScreen   # REAL storyboard file.
    # UILaunchScreen generation does NOT satisfy iPad-multitasking validation.
INFOPLIST_KEY_UISupportedInterfaceOrientations_iPhone: <3 orientations>
INFOPLIST_KEY_UISupportedInterfaceOrientations_iPad: <all 4>   # required for device family 1,2
INFOPLIST_KEY_ITSAppUsesNonExemptEncryption: NO      # kills the TestFlight compliance prompt
ASSETCATALOG_COMPILER_APPICON_NAME: AppIcon          # 1024 single-size catalog; no icon = ITMS-90023
CODE_SIGN_STYLE: Automatic + DEVELOPMENT_TEAM: TEAMID_PLACEHOLDER  # sed'd in by CI
```

**☢️ THE DYLIB LAW** (cost 14 rejected builds): **never embed a bare non-Swift
`.dylib` in `Frameworks/`**. Apple's ingestion only tolerates `libswift*` as
loose dylibs; anything else shunts the delivery into a legacy codepath that
rejects with FALSE ITMS-90426/90429 "SwiftSupport" errors, pre-ingestion
(builds never even reach "Processing"), while `altool --validate-app` passes.
Wrap every third-party dylib as a **`.framework` bundle** (binary renamed, id
`@rpath/Name.framework/Name`, own Info.plist) and build a framework-flavored
xcframework. Also normalize prebuilts' `LC_BUILD_VERSION` (`vtool
-set-build-version ios <min> <sdk>`) if their minos exceeds your target.

**Release workflow** (`ios-release.yml`, on `ios-v*` tags):
1. Pin a stable Xcode explicitly (`xcode-select -s /Applications/Xcode_X.Y.Z.app`)
   — version heuristics fail; every version exists under multiple names.
2. Fetch/assemble native deps (cached).
3. `sed` the team id into project.yml → `xcodegen generate`.
4. Import p12 into a temp keychain (`security create-keychain … import …
   set-key-partition-list`), install the profile into **both**
   `~/Library/MobileDevice/Provisioning Profiles/` and
   `~/Library/Developer/Xcode/UserData/Provisioning Profiles/` (Xcode 16+ reads the latter).
5. Archive with **manual signing** (`CODE_SIGN_STYLE=Manual`,
   `CODE_SIGN_IDENTITY="Apple Distribution"`, `PROVISIONING_PROFILE_SPECIFIER`).
   ⚠️ Xcode-account "cloud signing" (`-allowProvisioningUpdates` + API key)
   does NOT work on accountless runners — "No Account for Team".
6. Export IPA (`method: app-store-connect`, `signingStyle: manual`, destination export).
7. `altool --validate-app` first (synchronous server validation, printable
   errors), then `altool --upload-app`, then **grep the output** for
   `UPLOAD FAILED|ERROR ITMS-` — altool exits 0 on failure.
8. Poll `GET /v1/builds` afterwards: "UPLOAD SUCCEEDED" ≠ ingested. A build
   that never appears = async rejection (email) — that's the dylib law or a
   packaging gate, not your signing.

**ASC automation** (everything but two things): screenshots
(appScreenshotSets/appScreenshots reserve→PUT parts→MD5 commit), listing
text/URLs (appStoreVersionLocalizations + appInfoLocalizations), price
(`appPriceSchedules` — local ids are `${name}` format), review details,
age rating, bundle-id registration, build attach, compliance
(`usesNonExemptEncryption`). **Console-UI-only**: App Privacy labels and
creating the app record.

## 4. Android pipeline

**Project**: plain Gradle (Kotlin DSL, version catalog) under `android/`,
path-filtered CI. Native code via CMake + prebuilt `.so` per ABI vendored in
the repo (small enough); instrumented tests on an emulator job
(`reactivecircus/android-emulator-runner`, KVM udev rule first).

**Release workflow** (`android-release.yml`, on `android-v*` tags): unit tests
→ signed AAB (keystore decoded from secrets into `$RUNNER_TEMP`, wired via
`ANDROID_UPLOAD_*` env into a conditional `signingConfigs` block so PR builds
stay unsigned) → GitHub release → **auto-submit to Play internal track** via
`tools/play_submit.py` (service-account JWT → edits → bundle upload → track →
commit; ~80 lines, stdlib only — copy it).

**Play API covers**: listing text, contacts, icon (512), feature graphic
(1024×500), screenshots, track releases. **Console-only**: Data safety,
content rating, target audience, ads declaration, category, testers, and
creating the app. Decline "Automatic integrity protection" for FOSS apps.

⚠️ First AAB upload permanently locks the package name — decide
`ca.electricrv.<product>` BEFORE tagging. (API upload of the very first bundle
works fine against a console-created app record.)

## 5. Marketing assets, the honest way

Give the app a **screenshot mode**: iOS launch argument (`-screenshot
home|viewer|…`), Android intent extra (`--es screenshot <state>`). It seeds
believable demo content (bundled demo document + a seeded signature/profile)
and opens the requested UI state. Then CI captures **the real app**:

- iOS: `simctl` boot (fuzzy device match — names change per Xcode), demo
  status bar (`simctl status_bar override --time 9:41 …`), launch per state,
  `simctl io screenshot`. Required sizes: 6.9" iPhone + 13" iPad.
- Android: emulator-runner + `adb am start --es … ; screencap`. ⚠️ the
  runner splits multi-line `script:` inputs — keep logic in a repo `.sh`.
  Crop status/nav bars in post (PIL) for a clean look.

Icon and feature graphic are **generated, not drawn**: render the product's
SVG design programmatically (PIL) at 1024 (iOS single-size) / 512 (Play) /
1024×500 (feature graphic: gradient + icon + tagline, Segoe UI from
`/mnt/c/Windows/Fonts/`). Handwriting for demos: Windows script fonts
(`FREESCPT.TTF`) render better than any procedural stroke generation.

Listing copy limits: App Store — name 30, subtitle 30, promo 170, description
4000, keywords 100. Play — title 30, short 80, full 4000. Keep one source
document per product (`docs/app-store-listing.md` pattern) with every field
paste-ready and counted.

## 6. Testing discipline (what made the ports safe)

Cross-platform behavior is guarded by **shared fixture files** generated by
one script (`tools/gen_test_fixtures.py` pattern — hand-built PDFs with
computed xrefs, but the idea generalizes: one generator, N platform test
suites asserting identical expectations). Platform-specific gotchas: JUnit
requires **void** test methods (Kotlin `= runBlocking {}` expression bodies
break it); JVM-testable logic (pure pixel math, stores) lives in plain
functions; engine-level behavior gets instrumented/simulator tests in CI.

## 7. Website presence

Astro-free static pages deployed via cPanel UAPI (creds §2): sub-site
`/public_html/<product>/` (landing + `privacy/`), a teaser block spliced into
the main `index.html` (back it up server-side first), assets from the real
app screenshots. Keep page source versioned in the product repo
(`website/` dir). MegaPDF's `website/` + its deploy snippets are the template.

## 8. Landmine appendix (symptom → cause)

| Symptom | Cause / fix |
|---|---|
| ITMS-90426/90429 SwiftSupport, builds never reach Processing | **Bare non-Swift dylib in Frameworks** → framework-bundle it (§3) |
| "No Account for Team" / "No Accounts with ASC Access" | Cloud signing on accountless runner → manual signing (§3.5) |
| "No profile … matching" with profile installed | Wrong team id (see APPLEDEVID trap) or profile not in the Xcode 16+ UserData dir |
| altool green but build rejected by email | altool exits 0 on failure → grep guard + builds-API poll |
| iTMSTransporter "install Transporter from App Store" | The CLI is a stub on modern macOS → use `altool --validate-app` for verbose server validation |
| macOS `security import` "MAC verification failed" | OpenSSL 3 p12 → regenerate with `-legacy` |
| Play Console has no "Setup → API access" | Page removed → invite the service account under Users and permissions |
| Play API 403 on a fresh app | Grant still propagating, or no bundle uploaded yet — Play masks unknown packages as 403 |
| emulator-runner "expecting done" syntax error | It splits script lines → call a repo shell script |
| Play `appPriceSchedules` "invalid id format" | Inline-created resources need `${local-id}` style ids |
| WSL `python3` behaves strangely with paths | It's the Windows shim → use `/usr/bin/python3` / `PATH=/usr/bin:/bin` |
| Play: "app does not support 16 KB memory page sizes" | CMake-built JNI lib on 4 KB default alignment → `target_link_options(... "-Wl,-z,max-page-size=16384")` (bblanchon PDFium prebuilts already comply; verify any prebuilt via ELF PT_LOAD p_align ≥ 0x4000) |
| Play API: "Only releases with status draft may be created on draft app" | A never-published app's first production release must be status `draft` via API; a human presses Send-for-review in the console |
| Xcode picks wrong version despite pinning logic | Every release exists under bare AND patch names → pin the exact path |

## 9. New-product checklist (condensed order of battle)

1. Decide ids (`ca.electricrv.<product>` both platforms) and product name.
2. Register iOS bundle id + mint cert/profile via ASC API; generate Android
   upload keystore; set all repo secrets.
3. Scaffold from MegaPDF's `ios/` + `android/` trees (project.yml, workflows,
   fetch scripts); get CI green on hello-world **before** feature work.
4. Feature work with shared fixtures + per-platform test suites.
5. Privacy page + landing on electricrv.ca (blocks store submissions).
6. Screenshot modes + capture workflows; generate icons/graphics; write the
   listing doc with counted fields.
7. User creates the two store app records (console/ASC UI — not automatable).
8. Fill both listings via API (scripts in MegaPDF's tools/ + session pattern).
9. Tag `ios-v0.1.0` / `android-v0.1.0` → TestFlight + Play internal.
10. User: App Privacy labels (ASC), Play content forms, testers. Then submit.

---
*Born from the MegaPDF campaign of 2026-08-08/09: two native apps from empty
directories to store review in a weekend, including a 14-build fight with
Apple's ingestion that ended in the Dylib Law. Update this document when a
store moves the furniture again — they will.*
