. '$PSScriptRoot\common.ps1'
function DialogButtonLike($prefix) {
    $root = $AE::FromHandle((MainH))
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $b = @($root.FindAll($TS::Descendants, $c)) | Where-Object { $_.Current.Name -like "$prefix*" } | Select-Object -First 1
    if (-not $b) { Write-Host "!! no button like '$prefix'"; return $false }
    Write-Host "   press '$($b.Current.Name)'"
    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1500
    return $true
}
Copy-Item 'D:\megapdf-qa\rc2\redact\case.pdf' 'D:\megapdf-qa\rc2\redact\work.pdf' -Force
Remove-Item 'D:\megapdf-qa\rc2\redact\work-redacted.pdf' -ErrorAction SilentlyContinue
$h = OpenDoc 'D:\megapdf-qa\rc2\redact\work.pdf'
Btn 'RedactButton'
Step 'R1-armed'
Write-Host ("armed: " + (Texts))
Drag $h 966 368 1226 394
Step 'R2-name-marked'
Btn 'RedactButton'
Drag $h 920 680 1120 880
Step 'R3-image-marked'
Front (MainH)
[System.Windows.Forms.SendKeys]::SendWait('^s'); Start-Sleep 3
Write-Host ("confirmation: " + (Texts))
Step 'R4-confirmation'
DialogButtonLike 'Save as a copy' | Out-Null
Start-Sleep 3
Send-Path 'D:\megapdf-qa\rc2\redact\work-redacted.pdf' | Out-Null
Start-Sleep 10
Write-Host ("summary: " + (Texts))
Step 'R5-summary'
Write-Host ("title: " + (Title) + "; saved: " + (Get-Item 'D:\megapdf-qa\rc2\redact\work-redacted.pdf' -ErrorAction SilentlyContinue).Length + "; original: " + (Get-Item 'D:\megapdf-qa\rc2\redact\work.pdf').Length)
