. '$PSScriptRoot\common.ps1'
Copy-Item 'D:\megapdf-qa\rc2\redact\case.pdf' 'D:\megapdf-qa\rc2\redact\work.pdf' -Force
$h = OpenDoc 'D:\megapdf-qa\rc2\redact\work.pdf'
Step 'R0-probe'
