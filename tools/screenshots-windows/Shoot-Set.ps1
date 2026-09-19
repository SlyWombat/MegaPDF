# One language of the Microsoft Store set, end to end (#146 section 3).
#
#     .\Shoot-Set.ps1 -Lang en-US -Dir en
#     .\Shoot-Set.ps1 -Lang fr-CA -Dir fr-CA
#     .\Shoot-Set.ps1 -Lang fr-FR -Dir fr-FR
#
# Before it: the display at 150 % (Set-Scale.ps1 150, in its own process), the
# package to be shot installed, and fresh staging documents for the language
# (gen_store_docs.py <shotdir> --lang ...; the run saves over blank-agreement.pdf).
# The coordinates are the ones in README.md, read off the 2500x1550 frame.
#
# The six listing images land in artifacts\store\screenshots\<Dir>\; the probe
# and in-between frames go to its work\ folder, so the gate sees only the set.
param(
    [ValidateSet('en-US', 'fr-CA', 'fr-FR')][string]$Lang = 'en-US',
    [string]$Dir = 'en',
    [string]$Version = '2.0.0.0'
)
$ErrorActionPreference = 'Continue'
$H = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $H '..\..')).Path
$shots = Join-Path $repo "artifacts\store\screenshots\$Dir"
$env:MEGAPDF_SHOTDIR = $shots

# A set shot from the wrong build shows the wrong About and the wrong chrome.
$pkg = Get-AppxPackage -Name 'ElectricRV.MegaPDF'
if (-not $pkg -or $pkg.Version -ne $Version) { Write-Host "!! installed package is $($pkg.Version), not $Version"; exit 1 }
Write-Host "package: $($pkg.PackageFullName)"

# Accented text is built from code points: text piped through powershell.exe
# from WSL arrives in the OEM code page (README, "Type the accents").
$e = [char]0xE9   # e-acute
$g = [char]0xE8   # e-grave
switch ($Lang) {
    'fr-CA' { $Name = "Nom : H${e}l${g}ne B${e}langer"; $Date = '18 mars 2026' }
    'fr-FR' { $Name = "Nom : C${e}line Lef${g}vre";     $Date = '18 mars 2026' }
    default { $Name = 'Name: Jane Whitfield';           $Date = 'March 18, 2026' }
}

Get-Process MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
Get-ChildItem (Join-Path $env:LOCALAPPDATA 'MegaPDF\Recovery') -Filter *.journal -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
& "$H\Set-Language.ps1" -Lang $Lang -Theme Light
& "$H\Reset-SignatureLibrary.ps1"
& "$H\Setup-Frame.ps1" -W 2500 -T 1550 -Pdf "$shots\blank-agreement.pdf" -Fit "ActualSizeItem" -ZoomIn 0 -Name probe-frame
& "$H\Shot-TextEdit.ps1" -X 880 -Y 538 -Text $Name
& "$H\Shot-Checkboxes.ps1" -X 787 -Y1 707 -Y2 759
& "$H\Open-SignatureFlyout.ps1"
& "$H\Arm-Signature.ps1" -Notches 0 -X 510 -Y 248
& "$H\Place-Signature.ps1" -X 1022 -Y 1400
& "$H\Shot-AddText.ps1" -X 1390 -Y 1360 -Text $Date
& "$H\Shot-Shrink.ps1" -Pdf "$shots\scanned-agreement.pdf" -Lang $Lang
# Shot 6: redaction, on the agreement the steps above finished and Shrink saved. It
# writes a redacted copy beside it and leaves blank-agreement.pdf as it was.
& "$H\Setup-Frame.ps1" -W 2500 -T 1550 -Pdf "$shots\blank-agreement.pdf" -Fit "ActualSizeItem" -ZoomIn 0 -Name probe-frame-redact
& "$H\Shot-Redact.ps1" -Lang $Lang -Save

$work = Join-Path $shots 'work'
New-Item -ItemType Directory -Force $work | Out-Null
Get-ChildItem $shots -Filter *.png | Where-Object { $_.Name -notmatch '^0[1-6]-' } | Move-Item -Destination $work -Force
Get-ChildItem $shots -Filter *.png | Select-Object Name, Length | Format-Table | Out-String
