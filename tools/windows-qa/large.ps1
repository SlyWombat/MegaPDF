# RC sweep: the 2.5 GB file, once — open, scroll, search, edit, save.
$env:MEGAPDF_SHOTDIR = 'D:\megapdf-qa\rc2\large'
. 'D:\megapdf-qa\pkg.ps1'
New-Item -ItemType Directory -Force $env:MEGAPDF_SHOTDIR | Out-Null
$global:SHOTDIR = $env:MEGAPDF_SHOTDIR
$src = 'D:\megapdf-large\huge-2_5gb.pdf'
$doc = 'D:\megapdf-qa\large\huge-copy.pdf'
New-Item -ItemType Directory -Force 'D:\megapdf-qa\large' | Out-Null
function K($k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 900 }
function Step($name) { Start-Sleep -Milliseconds 1200; $h = MainH; if ($h) { Park $h; Shot $h $name } }
function Btn($id) { Click-Btn ($AE::FromHandle((MainH))) $id | Out-Null; Start-Sleep -Milliseconds 900 }
function Texts {
    $root = $AE::FromHandle((MainH))
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
    (@($root.FindAll($TS::Descendants, $c)) | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 2 }) -join ' | '
}
function Mem { $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { [math]::Round($p.PeakWorkingSet64 / 1MB) } else { 0 } }

if (-not (Test-Path $doc)) { Write-Host "copying 2.5 GB..."; Copy-Item $src $doc -Force }
Write-Host ("file: " + [math]::Round((Get-Item $doc).Length / 1GB, 2) + " GB")
Kill-App
$null = Start-App
$h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
$t0 = Get-Date
Btn 'OpenButton'; Send-Path $doc | Out-Null
Start-Sleep 20
$h = MainH; Set-Size $h 2560 1600 0 0; Front $h; Start-Sleep 2
Write-Host ("  open+first page: " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak WS " + (Mem) + " MB")
Step 'L1-open'
Write-Host ("  status: " + (Texts))

Write-Host '--- scroll'
$t0 = Get-Date
Scroll $h -25
Start-Sleep 3
Write-Host ("  scrolled in " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak WS " + (Mem) + " MB")
Step 'L2-scrolled'
Write-Host ("  status: " + (Texts))

Write-Host '--- search'
$t0 = Get-Date
K '^f'
K 'page'
Start-Sleep 12
Write-Host ("  search: " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak WS " + (Mem) + " MB")
Step 'L3-search'
Write-Host ("  status: " + (Texts))
K '{ESC}'

Write-Host '--- edit a line and save'
Click-InShot $h 900 700
Start-Sleep 3
Step 'L4-editor'
K '^a'
K 'MegaPDF RC sweep'
K '{ENTER}'
Start-Sleep 3
Step 'L5-edited'
$t0 = Get-Date
K '^s'
Start-Sleep 60
Write-Host ("  save: " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak WS " + (Mem) + " MB")
Step 'L6-saved'
Write-Host ("  title: " + [Win]::Title((MainH)))
Write-Host ("  size after save: " + [math]::Round((Get-Item $doc).Length / 1GB, 2) + " GB")
