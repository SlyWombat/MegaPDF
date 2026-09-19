. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\redact\case.pdf' 'D:\megapdf-qa\rc2\redact\nvda.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\redact\nvda.pdf'
$root = $AE::FromHandle($h)
(BtnById $root 'AddTextButton').SetFocus(); Start-Sleep 1
Write-Host ("  focused before: " + $AE::FocusedElement.Current.Name)
[System.Windows.Forms.SendKeys]::SendWait(' '); Start-Sleep 2
$f = $AE::FocusedElement.Current
Write-Host ("  focused after Space: " + $f.Name + " (" + $f.AutomationId + ")")
$a = BtnById $root 'AddTextButton'; Write-Host ("  Add text offscreen (in overflow): " + $a.Current.IsOffscreen)
Kill-App
