# Shot 6 — redaction, the 2.0 feature the other five shots don't show (#146 §3).
#
# Arms Redact, drags a mark across the customer's name, and then (with -Save) saves
# a redacted copy: the confirmation's "Save as a copy", the app's own suggested name,
# and the result — the name gone to a black bar and the summary bar saying what was
# removed. That result is the listing image; the marked state and the confirmation
# are kept in work\ for the record.
#
# Run it on the finished agreement (after Shot-Shrink.ps1 has saved it), in the same
# 2500x1550 frame at 100 %. The drag is in screenshot coordinates, like every other
# step: from just before the name to just past it, on the "Name:" line.
param([int]$X1 = 850, [int]$Y1 = 520, [int]$X2 = 1040, [int]$Y2 = 556,
      [switch]$Save, [string]$Out,
      [ValidateSet('en-US', 'fr-CA', 'fr-FR')][string]$Lang = 'en-US')
. (Join-Path $PSScriptRoot "lib.ps1")

function Drag-InShot($h, $x1, $y1, $x2, $y2) {
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($h, [ref]$r) | Out-Null }
    [Win]::SetCursorPos($r.Left + $x1, $r.Top + $y1) | Out-Null; Start-Sleep -Milliseconds 300
    [Win]::mouse_event(0x02, 0, 0, 0, [IntPtr]::Zero)
    # In steps, so the app sees a drag rather than a jump.
    for ($i = 1; $i -le 12; $i++) {
        [Win]::SetCursorPos([int]($r.Left + $x1 + ($x2 - $x1) * $i / 12), [int]($r.Top + $y1 + ($y2 - $y1) * $i / 12)) | Out-Null
        Start-Sleep -Milliseconds 40
    }
    Start-Sleep -Milliseconds 200
    [Win]::mouse_event(0x04, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "RedactButton" | Out-Null
Start-Sleep -Seconds 1
Drag-InShot $h $X1 $Y1 $X2 $Y2
Park $h
Shot $h "s6a-redact-marked"
if (-not $Save) { return }

$resw = Join-Path $PSScriptRoot "..\..\src\MegaPDF.App\Strings\$Lang\Resources.resw"
[xml]$catalogue = Get-Content $resw -Encoding UTF8
function Str($name) { ($catalogue.root.data | Where-Object { $_.name -eq $name }).value }

Front $h
[System.Windows.Forms.SendKeys]::SendWait("^s")
Start-Sleep -Seconds 2
Park $h
Shot $h "s6b-redact-confirm"
$copy = BtnByName ($AE::FromHandle($h)) (Str "RedactSaveCopy")
if (-not $copy) { Write-Host "!! no '$(Str "RedactSaveCopy")' button in the confirmation"; return }
$copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
if (-not $Out) {
    # The app's own suggestion, from the same catalogue string it formats it from.
    $base = [System.IO.Path]::GetFileNameWithoutExtension((Get-Process -Name MegaPDF |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowTitle.Split([char]0x2014)[0].Trim().TrimStart([char]0x25CF).Trim())
    $Out = Join-Path $env:MEGAPDF_SHOTDIR ($base + (Str "RedactedFileSuffix") + ".pdf")
}
Write-Host ("  saving as: " + $Out)
Send-Path $Out | Out-Null
Start-Sleep -Seconds 6
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
# Back from the picker (driven by keys), the keyboard focus ring is drawn on the page's
# first text run and on the viewer's frame. A click in the gutter clears only the frame;
# a click on blank paper, right of the Terms paragraph, clears both and leaves the
# summary bar up.
Click-InShot $h 1700 1250
Park $h
Shot $h "06-redact"
