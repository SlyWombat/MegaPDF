param([Parameter(Mandatory=$true)][string]$Pdf, [Parameter(Mandatory=$true)][string]$Out)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
[System.Windows.Forms.SendKeys]::SendWait("^s")      # Shrink refuses to run on a dirty document
Start-Sleep -Seconds 5
Front $h
Click-Btn ($AE::FromHandle($h)) "Open" | Out-Null
Send-Path $Pdf
Start-Sleep -Seconds 5
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Shot $h "s4a-scan-open"
Click-Btn ($AE::FromHandle($h)) "Shrink for email" | Out-Null
Start-Sleep -Seconds 12                              # downsample + re-encode before the save picker
Send-Path $Out
Start-Sleep -Seconds 8
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Park $h
Shot $h "04-shrink"
