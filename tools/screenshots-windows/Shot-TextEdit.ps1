param([int]$X = 680, [int]$Y = 755, [string]$Text = "Name: Dana Whitfield")
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Click-InShot $h $X $Y          # lands on the text run -> inline editor opens
Start-Sleep -Seconds 2
Park $h
Shot $h "s1a-editor-open"
[System.Windows.Forms.SendKeys]::SendWait("^a")
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait($Text)
Start-Sleep -Milliseconds 900
Shot $h "01-edit-text"
