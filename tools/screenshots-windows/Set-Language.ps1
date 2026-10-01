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
# Three more pieces of the machine's state that are part of the frame (#613):
#
#  * The first-run "Make MegaPDF your PDF app?" card. It is shown once per settings
#    file, so a freshly installed package puts it over the page in the first shot of
#    every run — including reading mode, where it is the only chrome left and lands
#    in the middle of the picture. Dismissing it here is how a run that reinstalled
#    the package comes out the same as one that did not.
#  * The page colours, which reading mode honours. A set shot in sepia or night is
#    not a set shot in the dark theme; it just looks like one.
#  * "Open documents in reading mode", which would put the slots that are *not*
#    reading mode into reading mode.
$json | Add-Member -NotePropertyName DefaultAppCardShown -NotePropertyValue $true -Force
$json | Add-Member -NotePropertyName PageColours -NotePropertyValue 'Normal' -Force
$json | Add-Member -NotePropertyName OpenInReadingMode -NotePropertyValue $false -Force
# No BOM: the app falls back to defaults if it finds one.
[System.IO.File]::WriteAllText($path, ($json | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("settings: Language=$Lang Theme=$Theme PageColours=Normal " +
            "OpenInReadingMode=False DefaultAppCardShown=True (restart the app to pick them up)")
