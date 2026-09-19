param([string]$Lang = 'en-US')
. '$PSScriptRoot\common.ps1'
. 'D:\megapdf-qa\rc2\activate.ps1'
$log = Join-Path $env:TEMP 'nvda.log'
function Speech($step) {
    Start-Sleep -Milliseconds 1800
    $size = (Get-Item $log).Length
    $stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    $stream.Seek($script:mark, 'Begin') | Out-Null
    $buffer = New-Object byte[] ($size - $script:mark)
    $stream.Read($buffer, 0, $buffer.Length) | Out-Null; $stream.Close()
    $script:mark = $size
    $said = @()
    foreach ($line in ([System.Text.Encoding]::UTF8.GetString($buffer) -split "`n")) {
        if ($line -match 'Speaking\s+(\[.*\])') {
            $parts = [regex]::Matches($matches[1], "'([^']*)'") | ForEach-Object { $_.Groups[1].Value }
            $joined = ($parts | Where-Object { $_ -and $_ -notmatch '^(en_US|fr_CA|fr_FR)$' }) -join ' | '
            if ($joined) { $said += $joined }
        }
    }
    Write-Host ("STEP {0}: {1}" -f $step, $(if ($said) { $said -join '  //  ' } else { '(nothing)' }))
}
if (-not (Get-Process nvda -ErrorAction SilentlyContinue)) {
    Start-Process 'C:\Program Files\NVDA\nvda.exe' -ArgumentList '--debug-logging'
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep 1; if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt (Get-Date).AddSeconds(-5)) { break } }
    Start-Sleep 5
}
Set-AppSetting 'Language' $Lang
Copy-Item 'D:\megapdf-qa\rc2\redact\case.pdf' 'D:\megapdf-qa\rc2\redact\nvda.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\redact\nvda.pdf'
$script:mark = (Get-Item $log).Length
Write-Host "=== $Lang"
Btn 'RedactButton'; Speech 'Redact armed'
Btn 'RedactButton'; Speech 'Redact disarmed'
# The recovery dialog: an unsaved tick, a kill, a relaunch.
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\nvda-rec.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\nvda-rec.pdf'
Click-InShot $h 894 660; Start-Sleep 1
Get-Process MegaPDF | Stop-Process -Force; Start-Sleep 2
$script:mark = (Get-Item $log).Length
Start-Process 'shell:AppsFolder\ElectricRV.MegaPDF_fba94j4nmgb9y!App'
for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; if (MainH) { break } }; Start-Sleep 6
Speech 'recovery dialog appears'
K '{TAB}'; Speech 'recovery tab 1'
K '{TAB}'; Speech 'recovery tab 2'
K '{ESC}'; Start-Sleep 2
Kill-App
