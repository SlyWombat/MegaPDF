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
$root = $AE::FromHandle($h)
$script:mark = (Get-Item $log).Length
Write-Host "=== $Lang"
(BtnById $root 'RedactButton').SetFocus(); Speech 'focus Redact (off)'
K ' '; Speech 'Space: Redact on'
K ' '; Speech 'Space again: Redact off'
K ' '; Speech 'Space: Redact on again'
K '{ESC}'; Speech 'Esc'
(BtnById $root 'WhiteoutButton').SetFocus(); Speech 'focus Whiteout (off)'
K ' '; Speech 'Space: Whiteout on'
Drag $h 920 680 1120 880
Speech 'whiteout drawn (tool ends)'
(BtnById $root 'AddTextButton').SetFocus(); Speech 'focus Add text'
K ' '; Speech 'Space: Add text on'
(BtnById $root 'RedactButton').SetFocus(); Speech 'focus Redact'
K ' '; Speech 'Space: Redact on (Add text should go off)'
Drag $h 966 368 1226 394
Speech 'mark drawn'
Kill-App
