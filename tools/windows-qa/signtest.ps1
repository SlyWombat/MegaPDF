. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\sign-test.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\sign-test.pdf'
Btn 'SignaturesButton'; Start-Sleep 2
Click-InShot $h 540 494; Start-Sleep 2
$r = New-Object Win+RECT; [Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
[Win]::SetCursorPos($r.Left + 1080, $r.Top + 1195) | Out-Null; Start-Sleep 1
Shot $h 'S1-placing'
Write-Host ("  texts while placing: " + (Texts))
Click-InShot $h 1080 1195; Start-Sleep 2
Shot $h 'S2-placed'
Click-InShot $h 2000 1300; Start-Sleep 1
Step 'S3-letgo'
Click-InShot $h 980 520; Start-Sleep 2
Shot $h 'S4-editor'
Write-Host ("  texts with editor: " + (Texts))
K '^a'; K 'Name: Dana Whitfield-Reyes'
Shot $h 'S5-typed'
K '{ENTER}'
Step 'S6-committed'
Write-Host ("  title: " + (Title))
