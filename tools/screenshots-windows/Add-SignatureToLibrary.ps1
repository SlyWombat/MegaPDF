param([Parameter(Mandatory=$true)][string]$Img, [int]$X = 1587, [int]$Y = 378)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "SignaturesButton" | Out-Null
Start-Sleep -Seconds 2
# "Add from image..." in the flyout. This coordinate assumes a library with ONE
# entry above it -- with a different number of saved signatures the row moves, so
# run Open-SignatureFlyout.ps1 first and re-read it off that shot.
Click-InShot $h $X $Y
Start-Sleep -Seconds 3
Send-Path $Img
Start-Sleep -Seconds 4
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "SignaturesButton" | Out-Null
Start-Sleep -Seconds 2
Park $h
Shot $h "s3b-library"
