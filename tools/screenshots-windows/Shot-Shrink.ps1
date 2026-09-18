# Leave -Out off and the copy is saved under the name the app itself suggests, taken
# from the same string the app formats it from -- in French "... - reduit.pdf", not
# "... - smaller.pdf", which is what an English -Out put in the French store shots (#146).
#
# Why the catalogue and not the dialog: the save dialog's file-name box is not in the
# dialog's UI Automation subtree (131 descendants, every one of them a column header, a
# list item or the search box), so there is nothing to read the suggestion out of.
param([Parameter(Mandatory=$true)][string]$Pdf, [string]$Out,
      [ValidateSet('en-US', 'fr-CA', 'fr-FR')][string]$Lang = 'en-US')
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
[System.Windows.Forms.SendKeys]::SendWait("^s")      # Shrink refuses to run on a dirty document
Start-Sleep -Seconds 5
Front $h
Click-Btn ($AE::FromHandle($h)) "OpenButton" | Out-Null
Send-Path $Pdf
Start-Sleep -Seconds 5
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Shot $h "s4a-scan-open"
Click-Btn ($AE::FromHandle($h)) "ShrinkButton" | Out-Null
Start-Sleep -Seconds 12                              # downsample + re-encode before the save picker
if (-not $Out) {
    $resw = Join-Path $PSScriptRoot "..\..\src\MegaPDF.App\Strings\$Lang\Resources.resw"
    [xml]$catalogue = Get-Content $resw -Encoding UTF8
    $format = ($catalogue.root.data | Where-Object { $_.name -eq "SmallerFileName" }).value
    $base = [System.IO.Path]::GetFileNameWithoutExtension($Pdf)
    $Out = Join-Path (Split-Path -Parent $Pdf) (($format -replace "\{0\}", $base) + ".pdf")
    Write-Host ("  saving as: " + (Split-Path -Leaf $Out))
}
Send-Path $Out
Start-Sleep -Seconds 8
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Park $h
Shot $h "04-shrink"
