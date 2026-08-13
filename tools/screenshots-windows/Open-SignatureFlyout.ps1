. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "Signatures" | Out-Null
Start-Sleep -Seconds 2
Park $h
Shot $h "s3a-sig-flyout"
