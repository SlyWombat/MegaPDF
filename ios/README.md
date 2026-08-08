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

## Signing & TestFlight path (when app work starts)

Uses the existing Apple Developer account; no Mac required at any step:

1. In App Store Connect → Users and Access → Integrations, create an
   **App Store Connect API key** (role: App Manager). Store as repo secrets:
   `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8` (the .p8 contents).
2. Release workflow (future `ios-release.yml`):
   - `xcodebuild archive -project MegaPDF.xcodeproj -scheme MegaPDF \
      -destination 'generic/platform=iOS' -allowProvisioningUpdates \
      -authenticationKeyID $ASC_KEY_ID -authenticationKeyIssuerID $ASC_ISSUER_ID \
      -authenticationKeyPath <key.p8>` — cloud-managed signing creates the
      certs/profiles automatically.
   - `xcodebuild -exportArchive` with `method: app-store-connect`, then upload
     via `xcrun altool --upload-app` (or fastlane `pilot`) using the same key.
3. TestFlight internal testers install via the TestFlight app.

Register the app id (`com.megapdf.ios`) and the App Store Connect app record
before the first archive.
