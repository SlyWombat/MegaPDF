param([int]$X = 876, [int]$Y = 950)
. (Join-Path $PSScriptRoot "lib.ps1")
$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Click-InShot $h $X $Y
Start-Sleep -Seconds 3
# park in the left gutter and let any hover tooltip time out before the shot
$r = New-Object Win+RECT
[Win]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
[Win]::SetCursorPos([int]($r.Left + 60), [int](($r.Top + $r.Bottom) / 2)) | Out-Null
Start-Sleep -Seconds 3
Shot $h "03-signature"
