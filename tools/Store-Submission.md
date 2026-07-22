# MegaPDF — Microsoft Store submission runbook

Distribution plan of record: **Microsoft Store** (SDD §5). The Store re-signs the
package with a Microsoft-trusted certificate on ingestion, so the self-signed dev
cert problems (error `0x800B010A`, "Publisher: Unknown", the `.cer`/Setup.exe trust
step) all disappear, and the Store handles updates.

## Status
- Partner Center developer account: **active as of 2026-07-22.**
- When creating the new app, product type = **"MSIX or PWA app"** (not EXE/MSI —
  Store re-signing, Store-managed updates, and package flights are MSIX-only).
- Name reserved 2026-07-22: **"Mega PDF"** (with a space — "MegaPDF" was taken).
  Manifest display names (`Properties/DisplayName`, `VisualElements DisplayName`,
  tile `ShortName`) updated to match; submission validation requires the package
  display name to match a reserved name.
- Store identity written into `Package.appxmanifest` 2026-07-22 (replaces the VS
  "Associate App with the Store" wizard step):
  - Identity Name `ElectricRV.MegaPDF`, Publisher `CN=AF0F2AB7-88E9-4EB3-A296-189E990F689E`
    (verified against PFN hash), PublisherDisplayName `Electric RV`.
  - Package Family Name `ElectricRV.MegaPDF_fba94j4nmgb9y`; Store ID `9PF4TRRH4M76`
    (listing link: https://apps.microsoft.com/detail/9PF4TRRH4M76).
  - Package SID `S-1-15-2-889272221-470063173-54854299-340821108-960714459-1437483129-3433287587`
    (not needed for submission; kept for future use, e.g. loopback exemption).
  - `tools/Setup.cs` AppUserModelId updated to the new family name.
- Builds now also work on the Sly machine: per-user Windows .NET SDK in
  `%LOCALAPPDATA%\Microsoft\dotnet` (no full VS needed; symbols package skipped —
  `mspdbcmf.exe` ships with VS only). Store-mode `dotnet build` produces an
  unsigned `.msix` (no `.msixupload` headlessly; Partner Center accepts `.msix`).
- Everything below marked "code — done" is already in the repo. Steps marked
  "needs account" or "needs reserved identity" are blocked until login.

## Already done in the repo (code)
- **Self-updater stands down on Store builds.** `MainWindow.CheckForUpdatesAsync`
  returns early when `Package.Current.SignatureKind == PackageSignatureKind.Store`.
  Same binary self-updates from GitHub when sideloaded and defers to the Store when
  Store-signed — no separate build config.
- **Store packaging mode validated to build.** A Release build with
  `-p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=false
  -p:UapAppxPackageBuildMode=StoreUpload -p:SelfContained=true
  -p:WindowsAppSDKSelfContained=true` compiles cleanly. (Producing the final
  `.msixupload` bundle headlessly is finicky — use the VS wizard below, which also
  writes the Store identity.)

## One-time account + identity steps (needs account)
1. Register Partner Center — **Company** account recommended (real publisher name +
   business identity; requires business verification, has lead time). Individual is
   cheaper/faster if you decide against Company.
2. **Reserve the app name "MegaPDF."** Partner Center then assigns the
   Store **Identity Name**, **Publisher** (`CN=<GUID>`), and **Publisher Display Name**.
3. **Associate the project with the Store:** in Visual Studio, right-click
   `MegaPDF.App` → Publish → **Associate App with the Store** → sign in → pick the
   reserved name. This rewrites `Package.appxmanifest` `<Identity>` and
   `<PublisherDisplayName>` to the Store-assigned values.
   - ⚠️ This changes the **package identity** (family name + publisher hash). It is a
     *different app* from the current self-signed test build: existing testers must
     **uninstall the old MegaPDF once** and install the Store version. Auto-update does
     not cross the identity boundary. The hardcoded `AppUserModelId` in `tools/Setup.cs`
     (`MegaPDF_spcj169vsxppp!App`) is only used by the sideload Setup.exe path — it does
     not affect Store installs, but update it if you keep shipping sideload builds.

## Build the upload package (needs reserved identity)
- Preferred: VS → `MegaPDF.App` → Publish → **Create App Packages → Microsoft Store**
  → select architectures (x64) → produces `…_x64.msixupload`. This handles bundling +
  identity correctly.
- The equivalent MSBuild flags (validated to build in Store mode; bundle output is
  wizard-dependent for this WinUI 3 self-contained project) are the `-p:` set listed
  under "Already done" above, plus `-p:AppxBundle=Always -p:AppxBundlePlatforms=x64`.

## Certification prep
- Run the **Windows App Certification Kit (WACK)** against the built package; fix
  anything it flags before submitting.
- **Done 2026-07-22: overall PASS** (report: `artifacts/store/wack-report.xml`, run
  against a dev-signed copy of the Store package). Two *optional* tests report
  FAIL — both are known Windows App SDK / self-contained .NET noise, not app code:
  - "General metadata correctness": `Microsoft.UI.Xaml.winmd` references WebView2
    types not present in the package (we don't use WebView2).
  - "Blocked executables": CreateProcess/ShellExecute references in `coreclr.dll`,
    `Microsoft.WindowsAppRuntime.dll`, `System.Diagnostics.Process.dll`, etc., plus
    string false-positives ("cmd", "Reg") in framework DLLs. `runFullTrust` apps may
    launch processes; informational only.
- To re-run WACK headlessly: build, sign a copy with the CurrentUser cert
  `CN=AF0F2AB7-…` (thumbprint `606D40BABE571A55D85E2C0BD26AA17A40B5D9F3`), then run
  `artifacts/store/wack-run.ps1` elevated (it temporarily trusts the cert + enables
  sideloading, runs appcert, reverts both).
- `runFullTrust` (the only declared capability) is allowed for packaged desktop apps;
  expect to briefly justify it during submission — standard for WinUI 3 desktop apps.

## Listing content (needs account)
- Description, screenshots, category, **age rating** (IARC questionnaire).
- **Privacy policy URL** — required. Data is local-only and telemetry is off by
  default (SDD §5), but the Store still wants a hosted policy page.
  - Done 2026-07-22: `docs/privacy.html` (no collection at all — no telemetry
    code exists yet; Store builds make zero network calls, sideload builds only
    hit the public GitHub releases feed). Hosted via GitHub Pages (`main`
    `/docs`): **https://slywombat.github.io/MegaPDF/privacy.html** — this is the
    URL for the submission's Properties page.

## Testers during rollout
- Use a **package flight** (Partner Center) or a **hidden listing** ("available but
  not discoverable," install via direct link) to give current testers
  **Microsoft-signed** installs before going public — this retires the sideload
  zip/Setup.exe/cert flow.

## Version rules
- Keep the 4-part version with **revision = 0** (`x.y.z.0`) — already the convention.
- Each submission's version must be higher than the last.

## After launch — decide
- Keep `tools/Build-Installer.ps1` + `Install-MegaPDF.ps1` for internal dev sideloading,
  or retire them once all testers are on the Store. The `UpdateChecker` can stay; it
  self-disables on Store builds.
