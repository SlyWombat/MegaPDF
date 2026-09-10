# Shot 5 (05-add-text.png): the inline editor open on a blank line with the size
# and font pickers above it -- the state ShowInlineEditor produces when handed a
# non-null style, which only MegaPDF's own text boxes get (#43).
#
# Run AFTER Place-Signature.ps1: the shot shows the signature already on its line,
# so the date reads as the next thing you would fill in.
#
# Do NOT click the size box open before shooting, whatever the older README said.
# Clicking it commits the inline edit and dismisses the pickers, and it can leave
# an access-key tooltip painted on the page. Both boxes are legible closed, which
# is all the caption promises.
#
# X/Y are read off a probe shot, like every coordinate in this harness. The
# editor's top-left lands ON the click, so aim ~60px above the rule you want the
# text to sit on, at that rule's left end.
param([int]$X = 1430, [int]$Y = 1080, [string]$Text = "March 18, 2026")
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-Btn ($AE::FromHandle($h)) "AddTextButton" | Out-Null
Start-Sleep -Seconds 2
Click-InShot $h $X $Y
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait($Text)
Start-Sleep -Seconds 2
Park $h
Shot $h "05-add-text"
