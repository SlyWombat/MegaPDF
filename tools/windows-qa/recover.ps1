param([string]$Case)
. '$PSScriptRoot\common.ps1'
. 'D:\megapdf-qa\rc2\activate.ps1'
function Press($name) {
    $root = $AE::FromHandle((MainH))
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $b = @($root.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $b) { Write-Host "!! no button '$name'"; return }
    Write-Host "   press '$name'"; $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 3
}
function Crash() { Get-Process MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
    Get-ChildItem "$env:LOCALAPPDATA\MegaPDF\Recovery" | ForEach-Object { "   journal {0}: {1} lines" -f $_.Name.Substring(0,8), (Get-Content $_.FullName).Count } }
function Report($l) { Write-Host "[$l] title: $(Title)"; Write-Host "[$l] texts: $((Texts) -replace ' \| RECENT.*','')" }
$a = 'D:\megapdf-qa\rc2\rec-a.pdf'; $b = 'D:\megapdf-qa\rc2\rec-b.pdf'
switch ($Case) {
  'close' {
    Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' $a -Force
    $h = OpenDoc $a; Click-InShot $h 894 660; Start-Sleep 1
    Front $h; [System.Windows.Forms.SendKeys]::SendWait('%{F4}'); Start-Sleep 2
    Report 'alt-f4'; Press 'Cancel'; Report 'after cancel'
    Front (MainH); [System.Windows.Forms.SendKeys]::SendWait('%{F4}'); Start-Sleep 2
    Press "Don't save"; Start-Sleep 2
    Write-Host ("   still running: " + [bool](Get-Process MegaPDF -ErrorAction SilentlyContinue))
    Write-Host ("   journals left: " + @(Get-ChildItem "$env:LOCALAPPDATA\MegaPDF\Recovery").Count)
  }
  'plain' {
    Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' $a -Force
    $h = OpenDoc $a; Click-InShot $h 894 660; Start-Sleep 1; Crash
    Start-Process 'shell:AppsFolder\ElectricRV.MegaPDF_fba94j4nmgb9y!App'
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; if (MainH) { break } }; Start-Sleep 6
    Set-Size (MainH) 2560 1600 0 0; Start-Sleep 1
    Report 'relaunched'; Press 'Restore'; Start-Sleep 2; Report 'restored'; Step 'K1-plain-restored'
  }
  'launched-same' {
    Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' $a -Force
    $h = OpenDoc $a; Click-InShot $h 894 702; Start-Sleep 1; Crash
    $pid2 = Activate-File $a
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; if (MainH) { break } }; Start-Sleep 6
    Set-Size (MainH) 2560 1600 0 0; Start-Sleep 1
    Report 'launched with the crashed file'; Press 'Restore'; Start-Sleep 3; Report 'restored'; Step 'K2-launched-same-restored'
  }
  'launched-other' {
    Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' $a -Force; Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' $b -Force
    $h = OpenDoc $a; Click-InShot $h 894 660; Start-Sleep 1; Crash
    $pid2 = Activate-File $b
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; if (MainH) { break } }; Start-Sleep 6
    Set-Size (MainH) 2560 1600 0 0; Start-Sleep 1
    Report 'launched with another file'; Press 'Restore'; Start-Sleep 3; Report 'restored, other file opening'
    Press "Don't save"; Start-Sleep 3; Report 'after dont save'
  }
}
