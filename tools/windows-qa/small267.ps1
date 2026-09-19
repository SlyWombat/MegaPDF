. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\small-267.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\small-267.pdf'
Click-InShot $h 980 520; Start-Sleep 2
K '^a'; K 'Name: Jane Whitfield'; K '{ENTER}'; Start-Sleep 1
K '^s'; Start-Sleep 5
Write-Host ("  title after save: " + (Title))
Kill-App
