# X/Y are read off the flyout shot, like every other coordinate here. The
# flyout hangs off the Signatures toolbar button, so its position does not
# scale with window width -- re-read it, do not compute it.
param([int]$Notches = 6, [int]$X = 1587, [int]$Y = 383)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Click-InShot $h $X $Y       # the signature library item -> placement mode
Start-Sleep -Seconds 2
Scroll $h $Notches             # bring the signature line into view
Park $h
Shot $h "s3c-placement-mode"
