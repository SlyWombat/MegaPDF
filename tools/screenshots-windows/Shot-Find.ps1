# Slot 5 — Find, every match highlighted at once (#613).
#
# Ctrl+F opens the find bar and focuses its query box (DocumentView.xaml.cs,
# ShowFindBar), so the term is typed straight after it. Nothing is pressed
# afterwards: the search runs as you type and the bar already says how many matches
# there are, while Enter would step to the next one and move the page under the
# highlights for no gain.
#
# The term is the ordinary word of the document's trade -- "equipment" in English,
# "location" in both French variants -- chosen in gen_store_docs.py so that the page
# the bar lands on carries several hits rather than one, and so that it has no accent
# in it: text piped to powershell.exe from WSL arrives in the OEM code page, so an
# accented term would be typed as mojibake (README, "Type the accents").
#
# This is the one slot whose capture legitimately shows a second row under the
# toolbar; the gate knows it by pose (tools/capture-gate/stores.py, rows_by_pose).
param([string]$Term = "equipment", [string]$Name = "05-search")
. (Join-Path $PSScriptRoot "lib.ps1")

$h = (Get-Process -Name MegaPDF | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
Front $h
Blur $h
[System.Windows.Forms.SendKeys]::SendWait("^f")
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait($Term)
Start-Sleep -Seconds 3        # search-as-you-type, then the highlights are drawn

$root = $AE::FromHandle($h)
if (-not (BtnById $root "FindNextButton")) {
    # Named by id in DocumentView.xaml; if the bar is not up there is nothing to shoot.
    Write-Host "  (no FindNextButton by id -- checking for the query box instead)"
    if (-not (ById $root "FindQuery" $global:CT::Edit)) {
        Write-Host "!! the find bar did not open"; exit 1
    }
}
Park $h
Shot $h $Name

# Escape closes the bar and clears the highlights, so the next slot starts clean.
Front $h
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Start-Sleep -Seconds 1
