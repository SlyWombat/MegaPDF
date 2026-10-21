# Elevated WACK run for MegaPDF Store prep. Trusts the dev cert and enables sideloading
# only for the duration of the run, then puts the machine back exactly as it found it:
# cert trust, sideloading, and the installed QA package.
#
# Started by wack-launch.ps1 (the "MegaPDF WACK" scheduled task) once the UAC prompt is
# approved; RELEASING.md 2.6 and tools/Store-Submission.md have the whole recipe. It used
# to live untracked in artifacts\store\, where no review ever saw it (#717).
#
# #717: it used to begin with Get-AppxPackage ElectricRV.MegaPDF | Remove-AppxPackage --
# whatever was installed, the Store install included. Removing a package deletes its data
# folder, and the Store install shares the test build's identity, so its folder too. Now:
#   * the person's data is copied aside before anything else and compared at the end
#     (Start-/Stop-QaUserDataWatch);
#   * a Store-signed package stops the run before anything changes: appcert installs the
#     package under test over that identity, which would replace the person's app;
#   * a test build is removed only when this tooling installed it, and reinstalled after.
#
# No timeout anywhere: this script only starts once the UAC prompt is approved, and it
# runs appcert to completion.
#
# Keep this file ASCII-only: Windows PowerShell reads a .ps1 with no BOM as ANSI, so a
# stray em dash breaks parsing with a misleading "Missing closing }".
$ErrorActionPreference = 'Continue'
$dir = 'D:\Projects\MegaPDF\artifacts\store'
$qaPackage = 'D:\megapdf-qa\pkg2\MegaPDF-signed.msix'
Start-Transcript "$dir\wack-run.log" -Force
. (Join-Path $PSScriptRoot 'qa-safety.ps1')
$watch = Start-QaUserDataWatch 'wack-run'

$reg = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$thumb = '606D40BABE571A55D85E2C0BD26AA17A40B5D9F3'

$before = Get-QaPackage
if ($before -and $before.SignatureKind -eq 'Store') {
    Write-Host "STOP: $($before.PackageFullName) is the Microsoft Store install, the person's own MegaPDF." -ForegroundColor Red
    Write-Host 'appcert installs the package under test over the same identity and removes it after, which would' -ForegroundColor Red
    Write-Host 'replace the app and delete its data. Run WACK on another account or machine. Nothing was changed.' -ForegroundColor Red
    $null = Stop-QaUserDataWatch $watch
    Set-Content "$dir\wack-done.marker" 'refused: Store install present'
    Stop-Transcript
    exit 1
}
if ($before -and -not (Test-QaPackageIsOurs $before)) {
    Write-Host "STOP: $($before.PackageFullName) (SignatureKind $($before.SignatureKind)) was not installed by the QA tooling, so this run will not remove it. Nothing was changed." -ForegroundColor Red
    $null = Stop-QaUserDataWatch $watch
    Set-Content "$dir\wack-done.marker" 'refused: unrecorded package present'
    Stop-Transcript
    exit 1
}
$wasInstalled = [bool]$before

# Restore whatever this machine had, not a hard 0: sideloading and the cert trust are on
# here for the QA installs, and removing either would break the next one.
$sideloadWasOn = 0
try { $sideloadWasOn = (Get-ItemProperty $reg -ErrorAction Stop).AllowAllTrustedApps } catch { }
$certWasTrusted = [bool](Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $thumb })
Write-Host "before: sideloading=$sideloadWasOn certTrusted=$certWasTrusted testPackageInstalled=$wasInstalled"
try {
    if (-not $certWasTrusted) {
        Import-Certificate -FilePath "$dir\wack-test.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        Write-Host 'Cert trusted (LocalMachine\TrustedPeople).'
    }
    Set-ItemProperty $reg -Name AllowAllTrustedApps -Value 1
    Write-Host 'Sideloading enabled.'

    # WACK installs the package under test itself; start it from a clean slate. Only a
    # test build this tooling installed can be here now (checked above).
    Remove-QaTestPackage

    # appcert refuses to write over an existing report ("Please specify a unique report
    # file name") and exits -1 without running a single test, after the cert and
    # sideloading have already been switched on. Clear it first.
    if (Test-Path "$dir\wack-report.xml") {
        Remove-Item "$dir\wack-report.xml" -Force
        Write-Host 'Removed previous wack-report.xml.'
    }

    $appcert = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe'
    & $appcert reset
    Write-Host "appcert reset exit: $LASTEXITCODE"
    & $appcert test -appxpackagepath "$dir\wack-test.msix" -reportoutputpath "$dir\wack-report.xml"
    Write-Host "appcert test exit: $LASTEXITCODE"
}
finally {
    # Anything installed now was installed during this run, by appcert: the slate was clean
    # before it started. Recorded as the tooling's, then removed -- never a Store package.
    $after = Get-QaPackage
    if ($after -and $after.SignatureKind -in 'Developer', 'None') {
        Register-QaTestPackage
        Remove-QaTestPackage
    } elseif ($after) {
        Write-Host "left installed: $($after.PackageFullName) is signed '$($after.SignatureKind)', not a test build" -ForegroundColor Red
    }
    if ($wasInstalled -and (Test-Path $qaPackage)) {
        try { Install-QaTestPackage $qaPackage; Write-Host 'QA package reinstalled.' }
        catch { Write-Host "QA package reinstall failed: $_" }
    }
    if (-not $certWasTrusted) {
        Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $thumb } | Remove-Item
        Write-Host 'Reverted cert trust.'
    }
    Set-ItemProperty $reg -Name AllowAllTrustedApps -Value $sideloadWasOn
    Write-Host "Sideloading back to $sideloadWasOn."
    $null = Stop-QaUserDataWatch $watch
    Set-Content "$dir\wack-done.marker" 'done'
    Stop-Transcript
}
