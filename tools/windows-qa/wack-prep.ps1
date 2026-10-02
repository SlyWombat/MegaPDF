# Stage a test-signed copy of a Store package for the App Certification Kit.
#
# RELEASING.md 2.6 says this must be the *CI* x64 package, never a local
# rebuild, so -Msix defaults to the newest artifacts/store/rc-<x.y.z.0>/ x64
# package - which is where `gh run download -n MegaPDF-store-packages` puts it,
# beside the SHA256SUMS naming the CI run. This script used to carry a hardcoded
# path to a local 2.0.0.0 build directory, which is the thing 2.6 forbids.
#
# Keep this file ASCII-only: Windows PowerShell reads a .ps1 with no BOM as ANSI,
# so a stray em dash breaks string parsing with a misleading "Missing closing }".
param(
    [string]$Msix
)
$ErrorActionPreference = 'Stop'
$store = 'D:\Projects\MegaPDF\artifacts\store'

if (-not $Msix) {
    $rc = Get-ChildItem $store -Directory -Filter 'rc-*' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $rc) { throw "no rc-<x.y.z.0> folder under $store - download MegaPDF-store-packages from the release commit's CI run first" }
    $cand = Get-ChildItem $rc.FullName -Filter '*_x64.msix' | Select-Object -First 1
    if (-not $cand) { throw "no *_x64.msix in $($rc.FullName)" }
    $Msix = $cand.FullName
}
if (-not (Test-Path $Msix)) { throw "no such package: $Msix" }

# The report and the done-marker are refused over rather than overwritten:
# appcert exits -1 AFTER the elevated round-trip if a report is in the way,
# which wastes the one interactive click this whole gate needs.
Remove-Item "$store\wack-done.marker", "$store\wack-report.xml", "$store\wack-run.log" -ErrorAction SilentlyContinue

Copy-Item $Msix "$store\wack-test.msix" -Force
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
& $signtool sign /fd SHA256 /sha1 606D40BABE571A55D85E2C0BD26AA17A40B5D9F3 "$store\wack-test.msix" 2>&1 | Select-Object -Last 2

$size = [math]::Round((Get-Item "$store\wack-test.msix").Length / 1MB, 1)
Write-Host "wack-test.msix ready: $size MB"
Write-Host "  from: $Msix"
