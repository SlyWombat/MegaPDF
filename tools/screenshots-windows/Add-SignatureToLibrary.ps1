param([Parameter(Mandatory=$true)][string]$Img)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "Signatures" | Out-Null
Start-Sleep -Seconds 2
Click-InShot $h 1587 378        # "Add from image..." in the flyout
Start-Sleep -Seconds 3
Send-Path $Img
Start-Sleep -Seconds 4
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "Signatures" | Out-Null
Start-Sleep -Seconds 2
Park $h
Shot $h "s3b-library"
