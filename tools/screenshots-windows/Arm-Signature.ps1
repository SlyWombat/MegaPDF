param([int]$Notches = 6)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Click-InShot $h 1587 383       # the megawoman-sig library item -> placement mode
Start-Sleep -Seconds 2
Scroll $h $Notches             # bring the signature line into view
Park $h
Shot $h "s3c-placement-mode"
