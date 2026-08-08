# MegaPDF for iOS

Native Swift + SwiftUI implementation of MegaPDF's mobile v1 scope
(fill-check-sign — see SDD §6). Currently at the **groundwork** stage (#20):
the CI pipeline is proven; app work starts after the engine ADR is decided
(`docs/adr-001-ios-pdf-engine.md`).

## Ground rules

Same as Android (`android/README.md`): no code sharing with .NET or Kotlin —
`MegaPDF.Core` is the behavioral reference; the SDD §6.2 contracts
(`MegaPDF_Id` tagging, checkbox heuristic, signature pixel math) are binding
and verified against the shared fixtures from `tools/gen_test_fixtures.py`.
No accounts, no cloud, no network.

## Building without a Mac

There is no local Mac: **GitHub Actions macOS runners are the build machine.**

- The Xcode project is *generated*, not committed: `ios/project.yml` is the
  source of truth; CI runs `xcodegen generate` (locally: `brew install
  xcodegen`). Never hand-edit or commit `MegaPDF.xcodeproj`.
- `ios-ci.yml` builds for the iOS Simulator with `CODE_SIGNING_ALLOWED=NO`,
  so no signing assets are needed for CI verification.

## Signing & TestFlight (#24 — wired)

No Mac at any step. Repo secrets `ASC_KEY_ID` / `ASC_ISSUER_ID` / `ASC_KEY_P8`
(team App Store Connect API key) and `APPLE_TEAM_ID` are set; the bundle id
`com.megapdf.ios` is registered.

- Tag `ios-v0.1.0` → `ios-release.yml` archives with cloud-managed signing
  (certs/profiles created automatically via the API key) and uploads straight
  to TestFlight (`-exportArchive` with `destination: upload`). The build
  number is the workflow run number; the marketing version comes from the tag.
- **One-time manual prerequisite:** create the app record in
  [App Store Connect](https://appstoreconnect.apple.com) → My Apps → **+ New
  App** → platform iOS, bundle id `com.megapdf.ios`, name *MegaPDF*. Uploads
  fail with "no suitable application records" until this exists.
- Internal testers install via the TestFlight app once a build processes.
