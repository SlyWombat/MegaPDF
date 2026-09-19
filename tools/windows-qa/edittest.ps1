. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\edit-test.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\edit-test.pdf'
Btn 'WhiteoutButton'; Start-Sleep 1
Drag $h 876 838 1316 866
Shot $h 'E1-after-drag'
Write-Host ("  after drag: " + (Texts))
K '{ESC}'
Shot $h 'E2-after-esc'
Write-Host ("  after esc: " + (Texts))
Click-InShot $h 980 520; Start-Sleep 2
Shot $h 'E3-after-click'
Write-Host ("  after click: " + (Texts))
Click-InShot $h 980 520; Start-Sleep 2
Shot $h 'E4-after-second-click'
Write-Host ("  after 2nd click: " + (Texts))
