param([string]$Name = "99-state")
. (Join-Path $PSScriptRoot "lib.ps1")
$p = Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
Shot $p.MainWindowHandle $Name
