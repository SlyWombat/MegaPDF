# #146 RC: Shrink on a scan and on the 280 MB scan, then a real print to Microsoft Print to PDF.
$env:MEGAPDF_SHOTDIR = 'D:\megapdf-qa\rc2\shots'
. 'D:\megapdf-qa\pkg.ps1'
$global:SHOTDIR = $env:MEGAPDF_SHOTDIR
$dir = 'D:\megapdf-qa\rc2'
function Step($name) { Start-Sleep -Milliseconds 800; $h = MainH; if ($h) { Park $h; Shot $h $name } }
function Btn($id) { Click-Btn ($AE::FromHandle((MainH))) $id | Out-Null; Start-Sleep -Milliseconds 900 }
function DialogButtonLike($prefix) {
    $root = $AE::FromHandle((MainH))
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $b = @($root.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.Name -like "$prefix*" } | Select-Object -First 1
    if (-not $b) { return $false }
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
    return $true
}
Remove-Item "$dir\scan - smaller.pdf", "$dir\big - smaller.pdf" -ErrorAction SilentlyContinue

Kill-App
$null = Start-App
$h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
Btn 'OpenButton'; Send-Path "$dir\scan.pdf" | Out-Null; Start-Sleep 8
$h = MainH; Set-Size $h 2560 1600 0 0; Front $h
Step 'D1-scan-open'
Btn 'ShrinkButton'
Start-Sleep 10
Send-Path "$dir\scan - smaller.pdf" | Out-Null
Start-Sleep 14
Step 'D2-shrink-result'
DialogButtonLike 'OK' | Out-Null
$before = (Get-Item "$dir\scan.pdf").Length
$after = (Get-Item "$dir\scan - smaller.pdf" -ErrorAction SilentlyContinue).Length
Write-Host "scan shrink: $before -> $after"

# The 280 MB scan.
Btn 'OpenButton'; Send-Path 'D:\megapdf-large\big-scan-250mb.pdf' | Out-Null; Start-Sleep 20
$h = MainH; Set-Size $h 2560 1600 0 0; Front $h
Step 'D3-big-scan-open'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Btn 'ShrinkButton'
Start-Sleep 20
Send-Path "$dir\big - smaller.pdf" | Out-Null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep 10
    if (Test-Path "$dir\big - smaller.pdf") { break }
}
Start-Sleep 10
Step 'D4-big-shrink-result'
$bigAfter = (Get-Item "$dir\big - smaller.pdf" -ErrorAction SilentlyContinue).Length
Write-Host ("big scan shrink: 293265245 -> $bigAfter in " + [int]$sw.Elapsed.TotalSeconds + " s")
$p = Get-Process MegaPDF -ErrorAction SilentlyContinue | Select-Object -First 1
Write-Host ("working set after the big shrink: " + [int]($p.WorkingSet64 / 1MB) + " MB")
DialogButtonLike 'OK' | Out-Null
