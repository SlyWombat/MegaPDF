. '$PSScriptRoot\common.ps1'
$global:SHOTDIR = 'D:\megapdf-qa\rc2\large'
Kill-App
$null = Start-App
$h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
$t0 = Get-Date
Btn 'OpenButton'; Send-Path 'D:\megapdf-qa\large\huge-267.pdf' | Out-Null
for ($i = 0; $i -lt 90; $i++) { Start-Sleep 1; if ((Texts) -match 'of 1000') { break } }
Write-Host ("  reopened in " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s; title " + (Title) + "; " + (Texts))
Front (MainH)
K '^f'; K 'edited for'
for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if ((Texts) -match '\d+ of \d+' -and (Texts) -notmatch 'Searching') { break } }
Start-Sleep 2
Write-Host ("  find 'edited for': " + (Texts))
Step 'H4-reopened-found'
K '{ESC}'
Kill-App
