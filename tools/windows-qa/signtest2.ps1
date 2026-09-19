. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\sign-test2.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\sign-test2.pdf'
Btn 'SignaturesButton'; Start-Sleep 2
Click-InShot $h 540 494; Start-Sleep 2
Click-InShot $h 1080 1225; Start-Sleep 2
Click-InShot $h 2000 1300; Start-Sleep 1
Step 'S7-line-fixed'
Btn 'SignaturesButton'; Start-Sleep 2
Click-InShot $h 540 494; Start-Sleep 2
Click-InShot $h 1765 1500; Start-Sleep 2
Click-InShot $h 2000 1300; Start-Sleep 1
Step 'S8-corner-clamped'
K '^s'; Start-Sleep 5
Write-Host ("  title: " + (Title))
