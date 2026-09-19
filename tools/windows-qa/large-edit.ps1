. '$PSScriptRoot\common.ps1'
$global:SHOTDIR = 'D:\megapdf-qa\rc2\large'
function Mem { $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { [math]::Round($p.PeakWorkingSet64 / 1MB) } else { 0 } }
$h = MainH; Front $h
Click-InShot $h 944 888; Start-Sleep 3
Shot $h 'L7-editor'
K '^a'; K 'Chapter 1, page 2 - RC walk 2026-09-18'; K '{ENTER}'; Start-Sleep 3
Write-Host ("  title after edit: " + (Title))
Step 'L8-edited'
$t0 = Get-Date
K '^s'
for ($i = 0; $i -lt 60; $i++) { Start-Sleep 2; if ((Title) -notmatch '^\S\s') { break } }
Write-Host ("  save: " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak WS " + (Mem) + " MB, title " + (Title))
Step 'L9-saved'
Write-Host ("  size: " + (Get-Item 'D:\megapdf-qa\large\huge-copy.pdf').Length)
