. '$PSScriptRoot\common.ps1'
function Wins { [Win]::Windows() | ForEach-Object { [Win]::Title($_) } | Where-Object { $_ } }
Copy-Item 'D:\megapdf-qa\rc2\flow-test.pdf' 'D:\megapdf-qa\rc2\print-test.pdf' -Force
Remove-Item 'D:\megapdf-qa\rc2\printed.pdf' -ErrorAction SilentlyContinue
$h = OpenDoc 'D:\megapdf-qa\rc2\print-test.pdf'
Btn 'PrintButton'; Start-Sleep 6
Write-Host ("windows: " + ((Wins | Where-Object { $_ -match 'Print' }) -join ' | '))
$pw = Find-Dialog @('print-test.pdf - Print')
if ($pw -eq [IntPtr]::Zero) { Write-Host '!! no print dialog'; return }
$r = New-Object Win+RECT; [Win]::GetWindowRect($pw, [ref]$r) | Out-Null
Write-Host ("print dialog rect: {0},{1} {2}x{3}" -f $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))
$bmp = New-Object System.Drawing.Bitmap ($r.Right-$r.Left), ($r.Bottom-$r.Top); $g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size); $bmp.Save('D:\megapdf-qa\rc2\shots\P1-print-dialog.png'); $g.Dispose(); $bmp.Dispose()
# Print is the dialog's default button.
Click-At ([int](($r.Left + $r.Right) / 2)) ([int]($r.Top + 20)); [Win]::SetForegroundWindow($pw) | Out-Null; Start-Sleep 1
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep 5
Write-Host ("windows after Print: " + ((Wins | Where-Object { $_ -match 'Save|Print' }) -join ' | '))
$sv = [IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $sv -eq [IntPtr]::Zero; $i++) { Start-Sleep 1; $sv = Find-Dialog @('Save Print Output As') }
if ($sv -eq [IntPtr]::Zero) { Write-Host '!! no Save Print Output As'; return }
$dr = New-Object Win+RECT; [Win]::GetWindowRect($sv, [ref]$dr) | Out-Null
Click-At ([int](($dr.Left + $dr.Right) / 2)) ([int]($dr.Top + 20)); [Win]::SetForegroundWindow($sv) | Out-Null; Start-Sleep 1
[System.Windows.Forms.SendKeys]::SendWait('D:\megapdf-qa\rc2\printed.pdf'); Start-Sleep 1
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
for ($i = 0; $i -lt 30; $i++) { Start-Sleep 1; if ((Test-Path 'D:\megapdf-qa\rc2\printed.pdf') -and (Get-Item 'D:\megapdf-qa\rc2\printed.pdf').Length -gt 0) { break } }
Start-Sleep 3
Write-Host ("printed: " + (Get-Item 'D:\megapdf-qa\rc2\printed.pdf' -ErrorAction SilentlyContinue).Length)
Step 'P2-after-print'
Write-Host ("app texts: " + (Texts))
