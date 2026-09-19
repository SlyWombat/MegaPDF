. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\redact\case.pdf' 'D:\megapdf-qa\rc2\redact\work.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\redact\work.pdf'
Step 'V0-off'
Btn 'RedactButton'
Step 'V1-armed'
$b = BtnById ($AE::FromHandle((MainH))) 'RedactButton'
$tp = $null
if ($b.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { Write-Host ("  UIA toggle state armed: " + $tp.Current.ToggleState) } else { Write-Host '  !! no TogglePattern' }
Write-Host ("  UIA control type: " + $b.Current.ControlType.ProgrammaticName + ", name: " + $b.Current.Name)
Btn 'RedactButton'
Step 'V2-off-again'
if ($b.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { Write-Host ("  UIA toggle state after second press: " + $tp.Current.ToggleState) }
Write-Host ("  texts: " + (Texts))
foreach ($id in 'WhiteoutButton','AddTextButton','SignaturesButton') { $x = BtnById ($AE::FromHandle((MainH))) $id; $t = $null; Write-Host ("  {0}: toggle pattern {1}" -f $id, $x.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$t)) }
