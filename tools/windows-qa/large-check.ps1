. '$PSScriptRoot\common.ps1'
$f = Get-Item 'D:\megapdf-qa\large\huge-copy.pdf'
Write-Host ("  file time: " + $f.LastWriteTime.ToString('HH:mm:ss') + "  now " + (Get-Date).ToString('HH:mm:ss') + "  size " + $f.Length)
Write-Host ("  texts: " + (Texts))
Write-Host ("  title: " + (Title))
