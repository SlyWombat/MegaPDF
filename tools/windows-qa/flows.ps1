# RC sweep: the editing flows, once each, on the installed package.
$env:MEGAPDF_SHOTDIR = 'D:\megapdf-qa\rc2\shots'
. 'D:\megapdf-qa\pkg.ps1'
$global:SHOTDIR = $env:MEGAPDF_SHOTDIR
$dir = 'D:\megapdf-qa\rc2'
$doc = "$dir\flow-test.pdf"
function K($k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 900 }
function Step($name) { Start-Sleep -Milliseconds 900; $h = MainH; if ($h) { Park $h; Shot $h $name } }
function Btn($id) { Click-Btn ($AE::FromHandle((MainH))) $id | Out-Null; Start-Sleep -Milliseconds 900 }
function Drag($h, $x1, $y1, $x2, $y2) {
    $r = New-Object Win+RECT; [Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
    [Win]::SetCursorPos($r.Left + $x1, $r.Top + $y1) | Out-Null; Start-Sleep -Milliseconds 300
    [Win]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 250
    for ($i = 1; $i -le 12; $i++) {
        [Win]::SetCursorPos($r.Left + $x1 + ($x2 - $x1) * $i / 12, $r.Top + $y1 + ($y2 - $y1) * $i / 12) | Out-Null
        Start-Sleep -Milliseconds 50
    }
    [Win]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 1200
}
function Title { [Win]::Title((MainH)) }
function Texts {
    $root = $AE::FromHandle((MainH))
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
    (@($root.FindAll($TS::Descendants, $c)) | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 2 }) -join ' | '
}

Copy-Item "$dir\agreement.pdf" $doc -Force
Kill-App
$null = Start-App
$h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
Btn 'OpenButton'; Send-Path $doc | Out-Null; Start-Sleep 5
$h = MainH; Set-Size $h 2560 1600 0 0; Front $h; Start-Sleep 1
Step 'G1-open'
Write-Host ("  title: " + (Title))

Write-Host '--- find'
K '^f'
Start-Sleep 1
K 'Equipment'
Start-Sleep 2
Step 'G2-find'
Write-Host ("  find status: " + (Texts))
K '{ESC}'

Write-Host '--- tick two boxes'
Click-InShot $h 662 940
Start-Sleep 1
Click-InShot $h 662 1010
Step 'G3-ticked'

Write-Host '--- sign'
Btn 'SignaturesButton'
Start-Sleep 1
Click-InShot $h 510 248
Start-Sleep 2
Click-InShot $h 1000 1480
Start-Sleep 2
Click-InShot $h 1800 800
Step 'G4-signed'

Write-Host '--- add text'
Btn 'AddTextButton'
Start-Sleep 1
Click-InShot $h 1500 1440
Start-Sleep 2
K 'March 18, 2026'
K '{ENTER}'
Step 'G5-added-text'

Write-Host '--- whiteout'
Btn 'WhiteoutButton'
Start-Sleep 1
Drag $h 640 1246 1400 1280
Step 'G6-whiteout'
K '{ESC}'

Write-Host '--- edit a line of text'
Click-InShot $h 800 714
Start-Sleep 2
K '^a'
K 'Name: Dana Whitfield-Reyes'
K '{ENTER}'
Step 'G7-edited'

Write-Host '--- undo three times, redo three times'
K '^z'; K '^z'; K '^z'
Step 'G8-undone'
K '^y'; K '^y'; K '^y'
Step 'G9-redone'

Write-Host '--- save'
K '^s'
Start-Sleep 6
Step 'G10-saved'
Write-Host ("  title after save: " + (Title))
Write-Host ("  file size: " + (Get-Item $doc).Length)
