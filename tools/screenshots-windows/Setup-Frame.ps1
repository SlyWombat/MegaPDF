param([int]$W = 2800, [int]$T = 2000, [string]$Pdf = "", [int]$ZoomIn = 0, [string]$Fit = "Fit page", [string]$Name = "probe-width")
. (Join-Path $PSScriptRoot "lib.ps1")
$p = Start-App
if (-not $p) { Write-Host "!! app did not start"; exit 1 }
$h = $p.MainWindowHandle
[Win]::ShowWindow($h, 9) | Out-Null
Start-Sleep -Seconds 3
Set-Size $h $W $T 0 0
Clear-Badges $h
if ($Pdf) {
    Click-Btn ($AE::FromHandle($h)) "Open" | Out-Null
    Send-Path $Pdf
    $p = Start-App
if (-not $p) { Write-Host "!! app did not start"; exit 1 }
$h = $p.MainWindowHandle
    Front $h
}
$root = $AE::FromHandle($h)
if ($Fit) { Click-Btn $root $Fit | Out-Null }
for ($i = 0; $i -lt $ZoomIn; $i++) { Click-Btn $root "Zoom in" | Out-Null }
Start-Sleep -Seconds 2
Blur $h
Shot $h $Name
