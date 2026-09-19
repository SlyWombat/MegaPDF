. '$PSScriptRoot\common.ps1'
Set-AppSetting 'Language' 'fr-CA'
Copy-Item 'D:\megapdf-qa\rc2\agreement.pdf' 'D:\megapdf-qa\rc2\fr-test.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\fr-test.pdf'
Step 'F1-open'
Write-Host ("  title: " + (Title) + " | " + (Texts))
Open-More ($AE::FromHandle($h)) | Out-Null; Start-Sleep 1; Shot $h 'F2-more'; K '{ESC}'
Btn 'RedactButton'; Step 'F3-redact-armed'; Write-Host ("  armed: " + (Texts))
Drag $h 966 505 1300 535
Step 'F4-marked'; Write-Host ("  marked: " + (Texts))
Front (MainH); [System.Windows.Forms.SendKeys]::SendWait('^s'); Start-Sleep 3
Step 'F5-confirm'; Write-Host ("  confirm: " + (Texts))
K '{ESC}'; Start-Sleep 1
Btn 'SettingsButton'; Start-Sleep 2; Shot $h 'F6-settings'; Write-Host ("  settings: " + ((Texts) -replace '(.{400}).*','$1'))
K '{ESC}'
