# The bar is at its widest with every command enabled AND Save carrying its
# unsaved dot. Measure exactly at the Full breakpoint, where clipping would survive.
param([string]$Pdf, [string]$Widths = "3000,3010,3040")
. (Join-Path $PSScriptRoot "lib.ps1")
$p = Start-App
$h = $p.MainWindowHandle
Start-Sleep -Seconds 3
Set-Size $h 3060 2000 0 0
Clear-Badges $h
Click-Btn ($AE::FromHandle($h)) "Open" | Out-Null
Send-Path $Pdf
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Set-Size $h 3060 2000 0 0
Front $h
Click-Btn ($AE::FromHandle($h)) "Fit page" | Out-Null
Click-Btn ($AE::FromHandle($h)) "Zoom in" | Out-Null
Start-Sleep -Seconds 1
Click-InShot $h 870 978          # tick a box so Save shows its dot and Undo enables
Start-Sleep -Seconds 2
foreach ($w in ($Widths -split "," | ForEach-Object { [int]$_ })) {
    Set-Size $h $w 2000 0 0
    Start-Sleep -Milliseconds 1200
    Front $h
    ShotStrip $h ("bp-" + $w) 120
}
