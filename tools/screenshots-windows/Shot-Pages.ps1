# Slot 4 — the page tools (#174), the easy picture of the two the 2.2 set adds (#613).
#
# The Pages pane opens on the left with a thumbnail per page and a selection on two
# of them. On Windows the pane is a GridView (DocumentView.xaml, PageTiles), so the
# thumbnails come out as a **grid of tiles** rather than the single column the other
# platforms draw. That difference is worth photographing rather than normalising: it
# is what a Windows user's Pages pane looks like.
#
# Two tiles selected, not one: the pane's whole argument is that it is somewhere to
# work, and every command on the Pages menu acts on a selection. One selected tile
# reads as "you are looking at page 3"; two read as "you have picked these".
#
# F4 toggles the pane (MainWindow.Pages.cs). The tile coordinates are read off a
# probe shot like every other coordinate in this harness -- they move with the page
# count, because the pane sizes its thumbnails to the document's aspect ratio.
param([int]$X1 = 0, [int]$Y1 = 0, [int]$X2 = 0, [int]$Y2 = 0, [string]$Name = "05-pages")
. (Join-Path $PSScriptRoot "lib.ps1")

$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Blur $h
[System.Windows.Forms.SendKeys]::SendWait("{F4}")
Start-Sleep -Seconds 3        # the thumbnails are rendered one at a time

$root = $AE::FromHandle($h)
$tiles = ById $root "PageTiles" $global:CT::List
if (-not $tiles) { Write-Host "!! the Pages pane did not open: no PageTiles"; exit 1 }
$count = $tiles.FindAll($global:TS::Children,
    (New-Object System.Windows.Automation.PropertyCondition($global:AE::ControlTypeProperty, $global:CT::ListItem))).Count
Write-Host "  pages pane: open, $count tiles"
if ($count -lt 2) { Write-Host "!! $count tile(s) -- the pane needs a multi-page document to read as a grid"; exit 1 }

if (-not $X1) {
    # No coordinates yet: shoot the pane so they can be read off it, put it away
    # again, and stop. Leaving it open was how the first dry run's find shot came
    # back with the Pages pane in it.
    Park $h
    Shot $h "probe-pages"
    Front $h
    [System.Windows.Forms.SendKeys]::SendWait("{F4}")
    Start-Sleep -Seconds 1
    Write-Host "  probe only -- re-run with -X1/-Y1/-X2/-Y2 read off probe-pages.png"
    return
}

Click-InShot $h $X1 $Y1
Start-Sleep -Milliseconds 700
# Ctrl+click adds to the selection (SelectionMode="Extended"); a plain click would
# replace it.
[Win]::keybd_event(0x11, 0, 0, [IntPtr]::Zero)          # Ctrl down
Click-InShot $h $X2 $Y2
[Win]::keybd_event(0x11, 0, 2, [IntPtr]::Zero)          # Ctrl up
Start-Sleep -Seconds 1

$selected = $tiles.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection().Count
Write-Host "  selection: $selected tile(s)"
if ($selected -ne 2) { Write-Host "!! expected 2 selected tiles, got $selected -- re-read the coordinates" }

Park $h
Shot $h $Name

# Put the pane away: the slots after this one are of the page, full width.
Front $h
[System.Windows.Forms.SendKeys]::SendWait("{F4}")
Start-Sleep -Seconds 1
