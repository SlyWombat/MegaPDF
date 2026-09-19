. '$PSScriptRoot\common.ps1'
$h = MainH; Front $h
Click-InShot $h 540 494; Start-Sleep 2
Click-InShot $h 1080 1225; Start-Sleep 2
Click-InShot $h 2000 1300; Start-Sleep 1
Step 'A4-signed'
Btn 'AddTextButton'; Start-Sleep 1
Click-InShot $h 1400 1180; Start-Sleep 2
K 'March 18, 2026'; K '{ENTER}'
Step 'A5-added-text'
Btn 'WhiteoutButton'; Start-Sleep 1
Drag $h 876 838 1316 866
Step 'A6-whiteout'
K '{ESC}'
Click-InShot $h 980 520; Start-Sleep 2
K '^a'; K 'Name: Dana Whitfield-Reyes'; K '{ENTER}'
Step 'A7-edited'
K '^z'; K '^z'; K '^z'
Step 'A8-undone'
K '^y'; K '^y'; K '^y'
Step 'A9-redone'
K '^s'; Start-Sleep 6
Step 'A10-saved'
Write-Host ("  title after save: " + (Title) + "  size " + (Get-Item 'D:\megapdf-qa\rc2\flow-test.pdf').Length)
Remove-Item 'D:\megapdf-qa\rc2\saved-as.pdf' -ErrorAction SilentlyContinue
Btn 'SaveAsButton'; Start-Sleep 2
Send-Path 'D:\megapdf-qa\rc2\saved-as.pdf' | Out-Null; Start-Sleep 6
Step 'A11-saved-as'
Write-Host ("  title after save as: " + (Title) + "  exists " + (Test-Path 'D:\megapdf-qa\rc2\saved-as.pdf'))
