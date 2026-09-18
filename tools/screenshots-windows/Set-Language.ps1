# The language and theme a capture run uses, set the way the app itself stores them.
#
# The packaged build has no command line to pass these on (--language only reaches an
# unpackaged build), so the run writes settings.json and starts the app fresh. The three
# store sets are en-US, fr-CA and fr-FR (#91).
param(
    [ValidateSet('en-US', 'fr-CA', 'fr-FR')][string]$Lang = 'en-US',
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light'
)
$ErrorActionPreference = 'Stop'
$path = Join-Path $env:LOCALAPPDATA 'MegaPDF\settings.json'
if (-not (Test-Path $path)) { Write-Host "!! no settings at $path -- run the app once"; exit 1 }

$json = Get-Content $path -Raw | ConvertFrom-Json
$json.Language = $Lang
$json.Theme = $Theme
# No BOM: the app falls back to defaults if it finds one.
[System.IO.File]::WriteAllText($path, ($json | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "settings: Language=$Lang Theme=$Theme (restart the app to pick them up)"
