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
    [string]$Name = "08-home",
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

# --------------------------------------------------------------------------------
# What the empty state must look like, asserted rather than eyeballed (#617).
#
# The first capture of this screen found the busy strip up with no work running, Stop
# and "Stopping…" painted over each other, and Undo and Redo enabled. One cause: with
# no tab `Shell.Active` is null, so every `x:Bind` through it falls back to its
# target property's default — `Visibility`'s is Visible and `Control.IsEnabled`'s is
# **true**. Three of those were visible in the image; the fix (#629) found 28
# commands enabled altogether, most of them in flyouts that no screenshot shows.
#
# So this reads the window rather than the picture. It is cheap, it runs on every
# home capture from now on, and it is the only thing standing between a regression in
# that binding and a listing image of an app that looks like it has a document open.
$root = $AE::FromHandle($h)
$enabled = @()
$checked = 0
function Test-Command($id) {
    $script:checked++
    $e = ById $root $id $global:CT::Button
    if (-not $e) { $e = ById $root $id $global:CT::MenuItem }
    if (-not $e) { $e = ById $root $id $global:CT::SplitButton }
    if (-not $e) { return }                       # not in the tree: nothing to assert
    if ($e.Current.IsEnabled) { $script:enabled += $id }
}

# The toolbar row, and the commands the "…" overflow holds. Open is the one command
# that is meant to work with no document open, so it is not in this list.
foreach ($id in 'SaveButton', 'SaveAsButton', 'PrintButton', 'SecurityButton',
                'ShrinkButton', 'SignaturesButton', 'AddTextButton', 'WhiteoutButton',
                'RedactButton', 'UndoButton', 'RedoButton', 'ZoomInButton',
                'ZoomOutButton', 'ZoomMenuButton', 'FindButton', 'PagesPaneButton',
                'PagesMenuButton', 'ClearMarksButton', 'ReadingModeButton') {
    Test-Command $id
}
# The flyouts are separate popup HWNDs, so their items are only in the tree while the
# flyout is open; both are searched from RootElement for that reason.
foreach ($opener in 'MoreButton', 'PagesMenuButton') {
    $b = BtnById $root $opener
    if (-not $b -or -not $b.Current.IsEnabled) { continue }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    $popup = $global:AE::RootElement
    foreach ($id in 'PagesPaneItem', 'RotateLeftItem', 'RotateRightItem', 'DeletePagesItem',
                    'InsertBlankPageItem', 'InsertPagesFromFileItem', 'ExtractPagesItem',
                    'MovePagesUpItem', 'MovePagesDownItem', 'ReadingModeItem',
                    'ClearMarksItem') {
        $script:checked++
        $item = ById $popup $id $global:CT::MenuItem
        if ($item -and $item.Current.IsEnabled) { $script:enabled += $id }
    }
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 500
}

Write-Host ("  empty state: $checked commands checked, " + $enabled.Count + " enabled")
if ($enabled.Count) {
    Write-Host ("!! these are enabled with no document open (#617): " + ($enabled -join ', '))
    Write-Host "!! the capture is of a window that looks like it has a document in it"
    exit 1
}
Write-Host "  empty state: every command disabled but Open, as it should be (#617)"
