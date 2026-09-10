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
  unsigned `.msix`, and `makeappx.exe` produces the `.msixbundle`/`.msixupload` if
  ever needed — the whole chain is headless (verified 2026-09-09). Partner Center
  accepts the plain `.msix` for a single-architecture submission.
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
  -p:WindowsAppSDKSelfContained=true` compiles cleanly, and with
  `-p:GenerateAppxPackageOnBuild=true` emits the `.msix` itself. The Store identity
  is already written into `Package.appxmanifest`, so the VS "Associate App with the
  Store" step is not needed either. See "Build the upload package" below.

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

**No Visual Studio required.** Verified end to end on GPD-DAVE 2026-09-09 with only the
per-user .NET SDK and the Windows SDK. The earlier note that the `.msixupload` bundle was
"wizard-dependent" was wrong: `AppxBundle=Always` alone does not emit a bundle, but
`makeappx.exe bundle` produces one directly, and a `.msixupload` is just a zip around the
`.msixbundle`.

For a single-architecture (x64) submission the plain `.msix` is enough — Partner Center
accepts it and the bundle adds nothing. Build a bundle only if you ship more than one
architecture.

### 1. Build the Store package

Produces an unsigned `.msix` (~40s), which is what the Store wants — it re-signs on
ingestion:

```
dotnet build src/MegaPDF.App/MegaPDF.App.csproj \
  -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 \
  -p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=false \
  -p:UapAppxPackageBuildMode=StoreUpload \
  -p:SelfContained=true -p:WindowsAppSDKSelfContained=true \
  -p:GenerateAppxPackageOnBuild=true
```

Output: `src/MegaPDF.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/AppPackages/MegaPDF.App_<ver>_x64_Test/MegaPDF.App_<ver>_x64.msix`.
The `_Test` folder name is cosmetic — the package inside carries the Store identity.

**ARM64:** the same command with `-p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64`.
It cross-compiles from an x64 machine with no extra toolchain and lands under
`bin/ARM64/.../win-arm64/AppPackages/`. Upload **both** packages to the same
submission — same identity and version, different `ProcessorArchitecture` — and the
Store serves each device the right one, so ARM64 machines run native code instead
of x64 emulation. WACK cannot test the ARM64 package on x64 hardware (appcert runs
against the host architecture); Store certification tests both server-side.

Note: the build rewrites `MaxVersionTested` from `TargetFramework`, so shipped
packages carry **10.0.19041.0** even though `Package.appxmanifest` says
`10.0.26100.0`. Harmless — it records what was tested and does not gate
installation — but the manifest is not what ships.

### 2. Bundle it (optional, multi-arch only)

Put the `.msix` alone in a directory whose path has no spaces (`C:\temp\…` — makeappx
chokes on the OneDrive path), then:

```
makeappx.exe bundle /d C:\temp\<dir> /bv <ver> /p C:\temp\MegaPDF.App_<ver>_x64.msixbundle /o
```

`makeappx.exe` lives in `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\`.
Pass `/bv` explicitly or the bundle version defaults to `0.0.0.0` and the Store rejects it.
A `.msixupload` is then just a zip containing that `.msixbundle` (plus the `.appxsym`
symbols file when one exists).

### 3. Verify before submitting

Read the identity out of the built package rather than trusting the filename:

```
python3 -c "import zipfile,re;m=zipfile.ZipFile('<pkg>.msix').read('AppxManifest.xml').decode();print(re.findall(r'<Identity[^>]*>',m))"
```

Expect `Name="ElectricRV.MegaPDF"`, `Publisher="CN=AF0F2AB7-88E9-4EB3-A296-189E990F689E"`,
the version you intended, and no `AppxSignature.p7x` entry in the zip.

### What VS would add, and why it does not matter

`mspdbcmf.exe` ships only with full VS, so the **symbols** (`.appxsym`) package is not
generated and the build warns about it. Symbols are optional for submission — they only
improve crash-report readability in Partner Center. Not a reason to install VS.

## Certification prep
- Run the **Windows App Certification Kit (WACK)** against the built package; fix
  anything it flags before submitting.
- **Done 2026-09-09 for 1.7.0.0: overall PASS**, 22 tests passed, the same 2
  optional FAILs as before (report: `artifacts/store/wack-report.xml`, run against
  a dev-signed copy of the Store package; the 2026-07-22 1.5.0.0 report is kept
  beside it as `wack-report-1.5.0.0-2026-07-22.xml`).
- ⚠️ **`appcert` refuses to overwrite an existing report** — it prints "Please
  specify a unique report file name", exits `-1` without running a single test,
  and does so *after* the runner has trusted the cert and enabled sideloading, so
  a stale report costs a whole elevated round-trip. `wack-run.ps1` now deletes the
  previous report first.
- The two *optional* FAILs are known Windows App SDK / self-contained .NET noise,
  not app code:
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
  - Use **https://electricrv.ca/megapdf/privacy/** on the submission's
    Properties page. Source: `website/megapdf/privacy/index.html`, deployed with
    `website/deploy.py --privacy`. Effective 8 August 2026, it covers Windows,
    Android, and iOS in one document, and it is the same URL both mobile store
    listings reference.
  - **Superseded:** the original 2026-07-22 policy, `docs/privacy.html` hosted
    via GitHub Pages (`main` `/docs`) at
    https://slywombat.github.io/MegaPDF/privacy.html, is Windows-only and still
    served. Do not cite it in a submission.

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
