. '$PSScriptRoot\common.ps1'
$pw = Find-Dialog @('print-test.pdf - Print')
if ($pw -eq [IntPtr]::Zero) { Write-Host '!! no print dialog'; return }
$r = New-Object Win+RECT; [Win]::GetWindowRect($pw, [ref]$r) | Out-Null
$pe = $AE::FromHandle($pw)
$bc = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
$btn = @($pe.FindAll($TS::Descendants, $bc)) | Where-Object { $_.Current.Name -eq 'Print' } | Select-Object -First 1
if (-not $btn) { Write-Host '!! no Print button in the dialog'; @($pe.FindAll($TS::Descendants, $bc)) | ForEach-Object { Write-Host ('   button: ' + $_.Current.Name) }; return }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4
$sv = [IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $sv -eq [IntPtr]::Zero; $i++) { Start-Sleep 1; $sv = Find-Dialog @('Save Print Output As') }
if ($sv -eq [IntPtr]::Zero) { Write-Host '!! no Save Print Output As'; return }
$dr = New-Object Win+RECT; [Win]::GetWindowRect($sv, [ref]$dr) | Out-Null
Click-At ([int](($dr.Left + $dr.Right) / 2)) ([int]($dr.Top + 20)); [Win]::SetForegroundWindow($sv) | Out-Null; Start-Sleep 1
Write-Host ("foreground: " + [Win]::Title([Win]::GetForegroundWindow()))
[System.Windows.Forms.SendKeys]::SendWait('D:\megapdf-qa\rc2\printed.pdf'); Start-Sleep 1
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
for ($i = 0; $i -lt 30; $i++) { Start-Sleep 1; if ((Test-Path 'D:\megapdf-qa\rc2\printed.pdf') -and (Get-Item 'D:\megapdf-qa\rc2\printed.pdf').Length -gt 0) { break } }
Start-Sleep 3
Write-Host ("printed: " + (Get-Item 'D:\megapdf-qa\rc2\printed.pdf' -ErrorAction SilentlyContinue).Length)
Step 'P2-after-print'
Write-Host ("app texts: " + (Texts))
