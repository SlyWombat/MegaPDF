$env:MEGAPDF_SHOTDIR = 'D:\megapdf-qa\rc2\shots'
. 'D:\megapdf-qa\pkg.ps1'
$global:SHOTDIR = $env:MEGAPDF_SHOTDIR
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\flow-test.pdf' -Force
Kill-App
$null = Start-App
$h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
Click-Btn ($AE::FromHandle($h)) 'OpenButton' | Out-Null; Send-Path 'D:\megapdf-qa\rc2\flow-test.pdf' | Out-Null; Start-Sleep 5
$h = MainH; Set-Size $h 2560 1600 0 0; Front $h; Start-Sleep 1
Click-Zoom ($AE::FromHandle($h)) 'FitPageItem' | Out-Null; Start-Sleep 2
Park $h; Shot $h 'P0-fitpage'
