# One-shot dev-build installer: trusts the dev certificate and installs the MSIX.
# (Replaces the generated Add-AppDevPackage.ps1, which tries to acquire a legacy
# "developer license" that Windows 11 neither has nor needs.)
# Run from the package folder, or pass -PackageDir.
param(
    [string]$PackageDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$msix = Get-ChildItem $PackageDir -Filter *.msix | Select-Object -First 1
$cer = Get-ChildItem $PackageDir -Filter *.cer | Select-Object -First 1
if (-not $msix -or -not $cer) { throw "No .msix/.cer found in $PackageDir" }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Elevating to trust the signing certificate (one UAC prompt)..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-Command", "Import-Certificate -FilePath '$($cer.FullName)' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    )
}
else {
    Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}

Write-Host "Installing $($msix.Name)..."
Add-AppxPackage -Path $msix.FullName
Write-Host "Done. MegaPDF is in the Start menu. Uninstall any time via Settings > Apps."
