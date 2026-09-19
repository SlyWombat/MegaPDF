$env:MEGAPDF_SHOTDIR = 'D:\megapdf-qa\rc2\shots'
. 'D:\megapdf-qa\pkg.ps1'
$global:SHOTDIR = $env:MEGAPDF_SHOTDIR
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
function OpenDoc($path) {
    Kill-App
    $null = Start-App
    $h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
    Btn 'OpenButton'; Send-Path $path | Out-Null; Start-Sleep 5
    $h = MainH; Set-Size $h 2560 1600 0 0; Front $h; Start-Sleep 1
    Click-Zoom ($AE::FromHandle($h)) 'FitPageItem' | Out-Null; Start-Sleep 2
    return $h
}
