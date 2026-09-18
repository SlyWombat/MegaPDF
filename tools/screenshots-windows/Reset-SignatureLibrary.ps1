# The signature library the capture run uses, put into a known state (#146).
#
# The store shots show the signature flyout, so whatever else the machine happens
# to hold shows up in the listing images — on GPD-DAVE that was a leftover
# "Daniel R. Foster" from an old test, above a MegaWoman row labelled with the
# file it was seeded from. Seeding through the UI (Add-SignatureToLibrary.ps1)
# reproduces both problems, because the app names an imported signature after its
# file and adds it to whatever is already there.
#
# This writes the library directly instead: one entry, from the repo's own PNG,
# under the name the listing should show. Any machine then produces the same
# images. The library it replaces is moved aside, not deleted.
param(
    [string]$Name = "MegaWoman",
    [string]$Png  = (Join-Path $PSScriptRoot "..\assets\megawoman-sig.png"),
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'

$dir   = Join-Path $env:LOCALAPPDATA 'MegaPDF\Signatures'
$saved = Join-Path $env:LOCALAPPDATA 'MegaPDF\Signatures.before-capture'

if ($Restore) {
    if (-not (Test-Path $saved)) { Write-Host "nothing set aside at $saved"; exit 0 }
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    Move-Item $saved $dir
    Write-Host "restored the machine's own signature library"
    exit 0
}

if (-not (Test-Path $Png)) { Write-Host "!! no signature image at $Png"; exit 1 }

# Keep the first library we find, so a real one is never lost to a capture run.
if ((Test-Path $dir) -and -not (Test-Path $saved)) { Move-Item $dir $saved }
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dir | Out-Null

# A fixed id, so re-running gives byte-identical library state.
$id = 'a11f0000000040008000000000000001'
$guid = ([guid]$id).ToString()
$target = Join-Path $dir "$id.png"
Copy-Item $Png $target -Force

# Written by hand rather than through ConvertTo-Json: Windows PowerShell 5.1 turns a
# one-element array into a bare object, and the index has to be a JSON array.
$json = @"
[
  {
    "Id": "$guid",
    "Name": "$Name",
    "PngPath": "$($target.Replace('\','\\'))",
    "CreatedUtc": "2026-01-01T00:00:00Z"
  }
]
"@
# No BOM: the app reads defaults if it finds one (see the settings note in pkg.ps1).
[System.IO.File]::WriteAllText(
    (Join-Path $dir 'index.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "signature library reset: one entry, '$Name'"
