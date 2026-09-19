$ErrorActionPreference = 'Stop'
$store = 'D:\Projects\MegaPDF\artifacts\store'
$src = 'D:\Projects\MegaPDF\src\MegaPDF.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AppPackages\MegaPDF.App_2.0.0.0_Test\MegaPDF.App_2.0.0.0_x64.msix'
Copy-Item $src "$store\wack-test.msix" -Force
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
& $signtool sign /fd SHA256 /sha1 606D40BABE571A55D85E2C0BD26AA17A40B5D9F3 "$store\wack-test.msix" 2>&1 | Select-Object -Last 2
Remove-Item "$store\wack-done.marker", "$store\wack-report.xml", "$store\wack-run.log" -ErrorAction SilentlyContinue
Write-Host ("wack-test.msix ready: " + [math]::Round((Get-Item "$store\wack-test.msix").Length / 1MB, 1) + " MB")
