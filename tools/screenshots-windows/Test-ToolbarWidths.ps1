param([string]$Exe, [string]$Widths = "3100,2900,2200,1800,1366")
. (Join-Path $PSScriptRoot "lib.ps1")
Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process $Exe
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { break }
}
Start-Sleep -Seconds 4
$p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Host "!! no window"; exit 1 }
$h = $p.MainWindowHandle
foreach ($w in ($Widths -split "," | ForEach-Object { [int]$_ })) {
    Set-Size $h $w 1000 0 0
    Start-Sleep -Milliseconds 1200
    Front $h
    ShotStrip $h ("tb-" + $w)
}
