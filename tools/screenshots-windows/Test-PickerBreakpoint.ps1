# The toolbar's label breakpoint while the font and size pickers are on the row (#802).
#
# Test-FullBreakpoint.ps1 measures the bar without them. The pickers appear only while an
# added text box is being edited or is selected, and they add the same fixed width in every
# language (the app measures both, MainWindow.Toolbar.cs LabelledToolbarWidth), so this opens
# a document, puts Add text's inline editor on the page, and captures the toolbar strip at each
# window width in -Widths (physical px; divide by the display scale for effective px).
#
# Run it on the packaged build, in the language under test (Set-Language.ps1 first), at the
# display scale the Store frame uses (150 %). Read each strip: labelled, or icons only.
# Nothing here photographs the page, only the toolbar band.
param([Parameter(Mandatory = $true)][string]$Pdf,
      [string]$Widths = "2050,2010,2000,1990,1980,1960,1940",
      [int]$X = 1390, [int]$Y = 1420, [string]$Text = "Test", [string]$Tag = "pk")
. (Join-Path $PSScriptRoot "lib.ps1")
& (Join-Path $PSScriptRoot "Setup-Frame.ps1") -W 2500 -T 1550 -Pdf $Pdf -Fit "ActualSizeItem" -ZoomIn 0 -Name "$Tag-probe"
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "AddTextButton" | Out-Null
Start-Sleep -Seconds 2
Click-InShot $h $X $Y
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait($Text)
Start-Sleep -Seconds 2
foreach ($w in ($Widths -split "," | ForEach-Object { [int]$_ })) {
    # No Front here: a click on the title bar would commit the editor and take the pickers away.
    Set-Size $h $w 1550 0 0
    Start-Sleep -Milliseconds 1200
    ShotStrip $h ("$Tag-" + $w) 120
}
