# #427 regression check: a document opened into an already-running MegaPDF from outside
# (Explorer double-click, "Open with", or a second `MegaPDF.exe <file>` launch -- the
# single-instance redirect from #348 phase 2) must land as a second tab with Signatures,
# Add text, Whiteout and Redact all enabled, exactly like a tab opened from inside the app.
#
# Root cause (fixed alongside this script): AddDocument() makes a brand-new tab Active
# before its document has even started opening. When that document then opens fast enough
# -- no splash, no dialog, nothing else on the UI thread in between, which is exactly what
# the redirect path looks like -- WinUI's x:Bind can drop one of the PropertyChanged
# notifications DocumentPath/Capabilities/Busy fire in that burst, and the toolbar's
# {x:Bind Shell.Active.IsSigningAllowed} et al. are left showing the value from the instant
# the tab became Active (everything disabled) instead of where the view model actually
# settled. See DocumentViewModel.OpenSettled for the fix and the full account.
#
# This is a genuine race, not a deterministic sequencing bug -- it reproduced 3 of 8 quick
# attempts against the pre-fix build on GPD-DAVE, and 0 of 30 against the fix. -Attempts
# below defaults to 15 for exactly that reason: a single pass proves nothing either way.
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File redirect-toolbar-check.ps1 `
#       -Exe D:\Projects\MegaPDF\src\MegaPDF.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\MegaPDF.exe
#
# Exits 0 with "PASS" iff every attempt lands with all four editing buttons enabled;
# exits 1 with a per-attempt report otherwise. Kills any MegaPDF process it finds first
# (this is a fresh-instance test) and again when it's done; writes its two scratch PDFs
# under $env:TEMP and removes them at the end.

param(
    [string]$Exe = $env:MEGAPDF_EXE,
    [int]$Attempts = 15
)

$ErrorActionPreference = 'Stop'
if (-not $Exe -or -not (Test-Path $Exe)) {
    Write-Host "usage: redirect-toolbar-check.ps1 -Exe <path to unpackaged MegaPDF.exe> [-Attempts N]"
    Write-Host "  (or set `$env:MEGAPDF_EXE)"
    exit 2
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

# Two distinct, real fixtures from the repo's own test corpus -- neither open yet, which is
# what a redirect needs to land as a genuinely new tab rather than just activating one.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dir = Join-Path $env:TEMP "megapdf-427-check-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force $dir | Out-Null
$a = Join-Path $dir "a.pdf"
$b = Join-Path $dir "b.pdf"
Copy-Item (Join-Path $repoRoot "tests\MegaPDF.Core.Tests\Fixtures\cid-font.pdf") $a
Copy-Item (Join-Path $repoRoot "tests\MegaPDF.Core.Tests\Fixtures\scaled-jpeg.pdf") $b

function BtnEnabled($root, $id) {
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $el = $root.FindFirst($TS::Descendants, $c1)
    if (-not $el) { return $null }
    return $el.Current.IsEnabled
}

$ids = 'SignaturesButton', 'AddTextButton', 'WhiteoutButton', 'RedactButton'
$failures = 0

try {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 800

        Start-Process -FilePath $Exe -ArgumentList "`"$a`""
        $proc = $null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            $proc = Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
            if ($proc) { break }
        }
        if (-not $proc) {
            Write-Host "attempt $attempt : FAIL -- first launch never got a window"
            $failures++
            continue
        }
        Start-Sleep -Seconds 3   # splash + crash-recovery offer + first open settle

        # The redirect: a second launch against the same exe while the first is still up.
        Start-Process -FilePath $Exe -ArgumentList "`"$b`""
        Start-Sleep -Milliseconds 700
        $proc.Refresh()
        $root = $AE::FromHandle($proc.MainWindowHandle)

        $states = @{}
        foreach ($id in $ids) { $states[$id] = BtnEnabled $root $id }
        $ok = ($states.Values | Where-Object { $_ -ne $true }).Count -eq 0

        $summary = ($ids | ForEach-Object { "$_=$($states[$_])" }) -join ' '
        Write-Host ("attempt {0,2}: {1}  {2}" -f $attempt, $summary, $(if ($ok) { "OK" } else { "FAIL" }))
        if (-not $ok) { $failures++ }
    }
} finally {
    Get-Process -Name MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}

if ($failures -eq 0) {
    Write-Host "`nPASS -- $Attempts/$Attempts redirected opens left every editing button enabled."
    exit 0
} else {
    Write-Host "`nFAIL -- $failures/$Attempts redirected opens left at least one editing button disabled (#427)."
    exit 1
}
