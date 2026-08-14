. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")   # commit the inline text edit
Start-Sleep -Seconds 3
Click-InShot $h 870 978      # "Include delivery and pickup"
Start-Sleep -Seconds 2
Click-InShot $h 870 1053      # "Damage insurance accepted" -- third box stays empty
Start-Sleep -Seconds 2
Park $h
Shot $h "02-checkbox"
