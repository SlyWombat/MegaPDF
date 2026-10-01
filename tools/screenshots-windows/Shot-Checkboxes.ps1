# Coordinates are read off the previous shot, so they belong to the caller and
# not to this file -- the frame changes with the display. Defaults are the
# 3038x1989 frame this was first written against.
# Ticking a box leaves a keyboard-focus ring round it, and the ring is **not** drawn
# in the brand accent -- #4a93e2 against the brand's #0e6fd8 -- so the capture gate's
# accent check cannot see a pixel of it. It has been in the shipped Microsoft Store
# screenshot since 2.0, in all three languages, at an identical 341 px (#613). The
# gate has a `focus_ring` check now; this is the other half of the fix.
#
# Clearing it takes Escape, not a click. -ClearX/-ClearY is a blank part of the page,
# which takes the ring off the *viewer's* frame; the ring on a page region survives a
# click that lands on no other region, which is why the first attempt at this changed
# nothing. Escape is what drops it, and Clear-Badges is the Front-plus-Escape pair
# Place-Signature.ps1 already uses for the same reason.
param([int]$X = 870, [int]$Y1 = 978, [int]$Y2 = 1053, [string]$Name = "03-checkbox",
      [int]$ClearX = 1700, [int]$ClearY = 1250)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")   # commit the inline text edit
Start-Sleep -Seconds 3
Click-InShot $h $X $Y1      # "Include delivery and pickup"
Start-Sleep -Seconds 2
Click-InShot $h $X $Y2      # "Damage insurance accepted" -- third box stays empty
Start-Sleep -Seconds 2
Click-InShot $h $ClearX $ClearY   # blank paper: off the viewer's own frame
Start-Sleep -Seconds 1
Clear-Badges $h                   # Front + Escape: off the box itself
Start-Sleep -Seconds 1
Park $h
Shot $h $Name
