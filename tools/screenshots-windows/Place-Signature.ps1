# -DeselectX/-DeselectY is a blank spot on the page: a tap outside a selected
# signature is what lets go of it. Read it off the probe like the other coordinates.
param([int]$X = 876, [int]$Y = 950, [int]$DeselectX = 1700, [int]$DeselectY = 1150)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Click-InShot $h $X $Y
Start-Sleep -Seconds 3
# A dropped signature stays selected, and its selection box -- a rectangle with
# handles, straight across the signature line -- is in the shot. In a listing image
# a signature has to look placed, not mid-edit, so tap a blank part of the page,
# which is what deselects it (#146). Escape only works if the page holds focus,
# and after a placement click it does not.
Front $h
Start-Sleep -Seconds 1
Click-InShot $h $DeselectX $DeselectY
Start-Sleep -Seconds 2
# The signature flyout is a menu, and WinUI leaves accelerator badges painted after one
# ("Ctrl+F" turned up in the middle of the page in the first re-shoot). ESC leaves menu mode.
Clear-Badges $h
Start-Sleep -Seconds 1
# park in the left gutter and let any hover tooltip time out before the shot
$r = New-Object Win+RECT
[Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
[Win]::SetCursorPos([int]($r.Left + 60), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
Start-Sleep -Seconds 3
Shot $h "03-signature"
