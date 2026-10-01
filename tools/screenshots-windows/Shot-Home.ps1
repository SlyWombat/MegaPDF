# Slot 7 — the empty window and its recents list (#613).
#
# **This is the one capture in the set that can photograph the machine's own files,
# and Dave has objected to exactly that before.** The empty state lists recent
# documents by name with their folder underneath (MainWindow.xaml), and on this
# machine the real list starts in his own documents. The harness README has said
# "never shoot it" since the English set was first taken, for that reason alone.
#
# So the list is posed rather than photographed. Mac and iOS have an app-side
# fixture for this (`--screenshot-state home` fills the list from
# DemoContent.Recents); Windows has none, so this writes `recent.json` itself,
# with nothing in it but the run's own staging documents, and puts the machine's
# own list back afterwards (-Restore). Until the Windows app grows the same
# fixture, that file **is** the privacy control: if this script does not run,
# nothing else stops the real list reaching the image.
#
# The folder the documents sit in is on camera too -- the row's second line is the
# path, as Explorer names it (RecentLocation.Segments) -- so the caller stages them
# somewhere that reads as a place a person keeps documents, and never under a user
# profile or OneDrive.
param(
    [string[]]$Paths = @(),
    [string]$Name = "07-home",
    [int]$W = 2500, [int]$T = 1550,
    [switch]$Restore
)
. (Join-Path $PSScriptRoot "lib.ps1")

$recent = Join-Path $env:LOCALAPPDATA 'MegaPDF\recent.json'
$saved = Join-Path $env:LOCALAPPDATA 'MegaPDF\recent.before-capture.json'

if ($Restore) {
    if (Test-Path $saved) {
        Move-Item -Force $saved $recent
        Write-Host "restored the machine's own recents list"
    } else {
        Write-Host "nothing set aside at $saved"
    }
    return
}
if (-not $Paths) { Write-Host "!! -Paths is required: the documents the posed list shows"; exit 1 }
foreach ($p in $Paths) {
    # A path the file has gone from is listed as "Not found", which is not the picture.
    if (-not (Test-Path $p)) { Write-Host "!! no such document: $p"; exit 1 }
}

Get-Process MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
if ((Test-Path $recent) -and -not (Test-Path $saved)) { Move-Item $recent $saved }
# Newest first, which is the order the list draws them in.
$entries = $Paths | ForEach-Object {
    [pscustomobject]@{ Path = $_; ScrollOffset = 0; ZoomPercent = 100; Bookmark = $null }
}
# No BOM: the app parses this file the same way it parses settings.json.
[System.IO.File]::WriteAllText($recent, (ConvertTo-Json @($entries) -Depth 4),
                               (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("  posed recents: " + (($Paths | ForEach-Object { Split-Path $_ -Leaf }) -join ', '))

$p = Start-App
if (-not $p) { Write-Host "!! app did not start"; exit 1 }
$h = $p.MainWindowHandle
[Win]::ShowWindow($h, 9) | Out-Null
Start-Sleep -Seconds 3
Set-Size $h $W $T 0 0
Clear-Badges $h
Start-Sleep -Seconds 2

# Nothing may be hovered: a 2026-09-19 Mac re-shoot of this very pose came back with
# the first recent highlighted and its path tooltip up, because the pointer was left
# over the window (docs/qa/mac-store-captures.md).
Park $h
Start-Sleep -Milliseconds 800
Shot $h $Name
