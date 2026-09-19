# Raises the UAC prompt for the WACK run and then simply waits on it. Started by the
# scheduled task "MegaPDF WACK" in Dave's interactive session, which has no execution
# time limit, so nothing on the WSL side (a tool call ending, a shell exiting) can
# cancel the pending prompt. Approving it starts wack-run.ps1 elevated.
$log = 'D:\megapdf-qa\wack-launch.log'
"$(Get-Date -Format s) raising UAC for wack-run.ps1" | Out-File $log -Append
try {
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', 'D:\Projects\MegaPDF\artifacts\store\wack-run.ps1') -ErrorAction Stop
    "$(Get-Date -Format s) approved; wack-run.ps1 started elevated" | Out-File $log -Append
}
catch {
    "$(Get-Date -Format s) UAC not approved: $($_.Exception.Message)" | Out-File $log -Append
}
