. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\flow-test.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\flow-test.pdf'
Step 'A1-open'
Write-Host ("  title: " + (Title))
K '^f'; K 'Equipment'; Start-Sleep 2
Step 'A2-find'
Write-Host ("  find: " + (Texts))
K '{ESC}'
Click-InShot $h 894 660; Start-Sleep 1
Click-InShot $h 894 702
Step 'A3-ticked'
Btn 'SignaturesButton'; Start-Sleep 2
Shot $h 'A4a-sigflyout'
