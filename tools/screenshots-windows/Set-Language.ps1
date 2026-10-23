# The language and theme a capture run uses, set the way the app itself stores them.
#
# The packaged build has no command line to pass these on (--language only reaches an
# unpackaged build), so the run writes settings.json and starts the app fresh. The five
# store sets are en-US, fr-CA, fr-FR (#91), es-419 and es-ES (#724). The value written is
# the tag itself: AppLanguage.Choices holds exactly these five, "es-419" is Latin American
# Spanish (served by the neutral Strings\es catalogue) and "es-ES" Spain's own.
param(
    [ValidateSet('en-US', 'fr-CA', 'fr-FR', 'es-419', 'es-ES')][string]$Lang = 'en-US',
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light'
)
$ErrorActionPreference = 'Stop'
# #717: back up the person's MegaPDF data first, and compare it when this PowerShell exits.
. (Join-Path $PSScriptRoot '..\windows-qa\qa-safety.ps1')
Watch-QaUserDataUntilExit (Split-Path -Leaf $MyInvocation.PSCommandPath)
# The app's data folder: MEGAPDF_USER_DATA's when it is set (an unpackaged build driven
# through MEGAPDF_EXE reads it, #717), otherwise the real one, which only a packaged run
# needs -- the package cannot be handed an environment variable.
$data = if ($env:MEGAPDF_USER_DATA) { $env:MEGAPDF_USER_DATA } else { Join-Path $env:LOCALAPPDATA 'MegaPDF' }
$path = Join-Path $data 'settings.json'
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
