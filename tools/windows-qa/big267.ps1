param([string]$Step)
. '$PSScriptRoot\common.ps1'
$global:SHOTDIR = 'D:\megapdf-qa\rc2\large'
$doc = 'D:\megapdf-qa\large\huge-267.pdf'
function Mem { $p = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { [math]::Round($p.PeakWorkingSet64 / 1MB) } else { 0 } }
switch ($Step) {
  'open' {
    if (-not (Test-Path $doc)) { $t = Get-Date; Copy-Item 'D:\megapdf-large\huge-2_5gb.pdf' $doc; Write-Host ("  copied in " + [int]((Get-Date) - $t).TotalSeconds + " s") }
    Kill-App
    $null = Start-App
    $h = MainH; Set-Size $h 2560 1600 0 0; Start-Sleep 2
    $t0 = Get-Date
    Btn 'OpenButton'; Send-Path $doc | Out-Null
    for ($i = 0; $i -lt 90; $i++) { Start-Sleep 1; if ((Texts) -match 'Page 1 of 1000') { break } }
    Write-Host ("  open: " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak " + (Mem) + " MB")
    $h = MainH; Set-Size $h 2560 1600 0 0; Front $h; Start-Sleep 1
    Click-Zoom ($AE::FromHandle($h)) 'FitPageItem' | Out-Null; Start-Sleep 4
    Step 'H0-probe'
  }
  'edit' {
    $h = MainH; Front $h
    Click-InShot $h $args[0] $args[1]; Start-Sleep 3
    Shot $h 'H1-editor'
    K '^a'; K 'Chapter 1, page 1 - edited for #267'; K '{ENTER}'; Start-Sleep 3
    Write-Host ("  title after edit: " + (Title))
    Step 'H2-edited'
    $before = (Get-Item $doc).LastWriteTime
    $t0 = Get-Date
    K '^s'
    for ($i = 0; $i -lt 300; $i++) { Start-Sleep 1; if ((Get-Item $doc).LastWriteTime -ne $before -and (Title) -notmatch '^\S\s' -and (Texts) -notmatch 'Saving|Checking') { break } }
    Write-Host ("  save: " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + " s, peak " + (Mem) + " MB, title " + (Title) + ", size " + (Get-Item $doc).Length)
    Step 'H3-saved'
  }
}
