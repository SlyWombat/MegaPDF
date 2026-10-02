# Register the "MegaPDF WACK" scheduled task that raises the UAC prompt for the
# certification run. RELEASING.md 2.6: started only when someone is at the console
# to click Yes, so this script registers and does NOT start it.
#
# The task exists because the prompt must outlive whatever started it. A UAC
# prompt raised from a WSL tool call dies when that call returns; a task in the
# interactive session has no execution time limit and simply waits.
#
# Keep this file ASCII-only: Windows PowerShell reads a .ps1 with no BOM as ANSI,
# so a stray em dash breaks parsing with a misleading "Missing closing }".
$ErrorActionPreference = 'Stop'
$task = 'MegaPDF WACK'

Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

$arg = '-NoProfile -ExecutionPolicy Bypass -File "D:\Projects\MegaPDF\tools\windows-qa\wack-launch.ps1"'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

# ExecutionTimeLimit 0 = none. The prompt may sit overnight.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $task -Action $action -Principal $principal `
    -Settings $settings -Description 'Raises the UAC prompt for the MegaPDF WACK certification run (RELEASING.md 2.6). Start manually when someone can click Yes.' | Out-Null

$t = Get-ScheduledTask -TaskName $task
Write-Host "registered: $($t.TaskName) state=$($t.State)"
Write-Host "  runs: $((Get-ScheduledTask -TaskName $task).Actions[0].Arguments)"
Write-Host "  start it with: Start-ScheduledTask -TaskName '$task'"
