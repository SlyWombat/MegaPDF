# Coordinates are read off the previous shot, so they belong to the caller and
# not to this file -- the frame changes with the display. Defaults are the
# 3038x1989 frame this was first written against.
param([int]$X = 870, [int]$Y1 = 978, [int]$Y2 = 1053)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")   # commit the inline text edit
Start-Sleep -Seconds 3
Click-InShot $h $X $Y1      # "Include delivery and pickup"
Start-Sleep -Seconds 2
Click-InShot $h $X $Y2      # "Damage insurance accepted" -- third box stays empty
Start-Sleep -Seconds 2
Park $h
Shot $h "02-checkbox"
