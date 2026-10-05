# One language of the Microsoft Store set, end to end (#146 section 3, re-cut for the
# 2.2 slots in #613).
#
#     .\Shoot-Set.ps1 -Lang en-US -Dir en
#     .\Shoot-Set.ps1 -Lang fr-CA -Dir fr-CA
#     .\Shoot-Set.ps1 -Lang fr-FR -Dir fr-FR
#
# Before it: the display at 150 % (Set-Scale.ps1 150, in its own process), the
# package to be shot installed, and fresh staging documents for the language
# (gen_store_docs.py <shotdir> --lang ...; the run saves over blank-agreement.pdf).
# The coordinates are the ones in README.md, read off the 2500x1550 frame. Since 2.1.1 the
# tab strip (#348) sits between the toolbar and the page, so every page coordinate is 60 px
# lower than on the 2.0/2.1.0 frame (page top at 213, not 153); the flyout row is where it was.
#
# The eight listing images land in artifacts\store\screenshots\<Dir>\; the probe
# and in-between frames go to its work\ folder, so the gate sees only the set.
#
# -------------------------------------------------------------------- the slot set
# Dave, 2026-10-01 (#613): "Redaction and whiteout are minor features that move to the
# back, signing, editing and reading are common features." The desktop set he approved
# is seven slots -- reading, text, sign, pages, search, redact, home -- and reading
# replaces the old viewer slot at the front rather than joining it.
#
# **On Windows that was more than a reorder**, and the difference was put to him rather
# than guessed at. The approved list was drawn up against the Mac set (viewer, text,
# search, sign, redact, home); the Windows set was never those six. It was edit-text,
# checkbox, signature, shrink, add-text, redact.
#
# **Dave ruled the same day: eight slots.** `checkbox` comes back, `shrink` and
# `add-text` stay retired, and the ninth slot the Store would allow is deliberately
# left empty. His reasoning places the slot as well as restoring it: form filling is
# the one real capability the approved seven never demonstrates, and the Linux
# reading-mode caption already leans on a filled-in form, so the feature was part of
# the story on another platform while being invisible on ours. Shrink and add-text are
# feature demos rather than user outcomes.
#
# So the order is reading, text, **checkbox**, sign, pages, search, redact, home. The
# checkbox slot sits between fixing the document and signing it, which is both where it
# belongs in the argument the captions make -- fix it, fill it, sign it -- and where it
# happens on the document itself: the name is corrected, the options are ticked, and
# then it is signed. `shrink` and `add-text` are still shot, into work\.
#
# ------------------------------------------------------------------ the documents
# Three slots need a document the one-page agreement cannot be: reading mode's bar
# reads "1 / 1" on it, the Pages pane shows a single tile, and Find highlights one
# hit. They run on rental-terms.pdf, twelve pages from the same generator.
#
# Both documents are copied to a staging folder under display names in the set's own
# language before anything is shot. Three places put a file name on camera -- the
# title bar, the tab, and reading mode's floating bar -- and "blank-agreement.pdf" on
# a French listing image is an English word on a French capture, which the 2.0 and
# 2.1 sets shipped and this one does not.
param(
    [ValidateSet('en-US', 'fr-CA', 'fr-FR')][string]$Lang = 'en-US',
    [string]$Dir = 'en',
    [string]$Version = '2.2.1.0',
    # Which phases to run. Each is a fresh launch, so they can be run one at a time
    # while the coordinates are being read off the probe shots.
    [ValidateSet('all', 'agreement', 'terms', 'home')][string]$Step = 'all',
    # Read off probe-pages.png: the two thumbnails the pages slot selects. Two
    # columns of tiles, so the pair that reads best is side by side on one row.
    [int]$TileX1 = 0, [int]$TileY1 = 0, [int]$TileX2 = 0, [int]$TileY2 = 0
)
$ErrorActionPreference = 'Continue'
$H = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $H '..\..')).Path
$shots = Join-Path $repo "artifacts\store\screenshots\$Dir"
$env:MEGAPDF_SHOTDIR = $shots

# A set shot from the wrong build shows the wrong About and the wrong chrome.
$pkg = Get-AppxPackage -Name 'ElectricRV.MegaPDF'
if (-not $pkg -or $pkg.Version -ne $Version) { Write-Host "!! installed package is $($pkg.Version), not $Version"; exit 1 }
Write-Host "package: $($pkg.PackageFullName)"

# Accented text is built from code points: text piped through powershell.exe
# from WSL arrives in the OEM code page (README, "Type the accents").
$e = [char]0xE9   # e-acute
$g = [char]0xE8   # e-grave
switch ($Lang) {
    'fr-CA' { $Name = "Nom : H${e}l${g}ne B${e}langer"; $Date = '18 mars 2026'; $Term = 'location' }
    'fr-FR' { $Name = "Nom : C${e}line Lef${g}vre";     $Date = '18 mars 2026'; $Term = 'location' }
    default { $Name = 'Name: Jane Whitfield';           $Date = 'March 18, 2026'; $Term = 'equipment' }
}
# The staging folder and the display names, per language. D:\Documents\<folder> reads
# as "D: > Documents > Locations" in the recents list -- a drive and two folders, and
# nothing of whoever shot it. Never a user profile or OneDrive: on this machine the
# app's own Open dialog starts in Dave's OneDrive, and he has objected to that
# reaching a public capture.
if ($Lang -eq 'en-US') {
    $folder = 'D:\Documents\Rentals'
    $docName = 'Equipment Rental Agreement.pdf'
    $termsName = 'Rental Terms and Conditions.pdf'
    $scanName = 'Scanned Agreement.pdf'
} else {
    $folder = 'D:\Documents\Locations'
    $docName = 'Contrat de location.pdf'
    $termsName = 'Conditions de location.pdf'
    $scanName = "Contrat num${e}ris${e}.pdf"
}
New-Item -ItemType Directory -Force $folder | Out-Null
$doc = Join-Path $folder $docName
$terms = Join-Path $folder $termsName
$scan = Join-Path $folder $scanName
Copy-Item -Force (Join-Path $shots 'blank-agreement.pdf') $doc
Copy-Item -Force (Join-Path $shots 'rental-terms.pdf') $terms
Copy-Item -Force (Join-Path $shots 'scanned-agreement.pdf') $scan
Write-Host "staged: $folder"

function Reset-App {
    Get-Process MegaPDF -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2
    Get-ChildItem (Join-Path $env:LOCALAPPDATA 'MegaPDF\Recovery') -Filter *.journal -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Reset-App
& "$H\Set-Language.ps1" -Lang $Lang -Theme Light
& "$H\Reset-SignatureLibrary.ps1"

# --------------------------------------------- slots 2, 3, 4 and 7: the agreement
# One launch, one document, in the order the story happens on it: the name is
# corrected, the options are ticked, the signature goes on the line, the file is
# saved, and then the corrected name is redacted out of it.
if ($Step -in 'all', 'agreement') {
    Reset-App
    & "$H\Setup-Frame.ps1" -W 2500 -T 1550 -Pdf $doc -Fit "ActualSizeItem" -ZoomIn 0 -Name probe-frame
    & "$H\Shot-TextEdit.ps1" -X 880 -Y 598 -Text $Name -Name "02-text"
    # Slot 3 (Dave, #613). It also commits the inline edit, which every slot after it
    # needs — which is why it stayed in the run even while it was retired.
    & "$H\Shot-Checkboxes.ps1" -X 787 -Y1 767 -Y2 819 -Name "03-checkbox"
    & "$H\Open-SignatureFlyout.ps1"
    & "$H\Arm-Signature.ps1" -Notches 0 -X 510 -Y 248
    & "$H\Place-Signature.ps1" -X 1022 -Y 1460 -Name "04-sign"
    # Retired from the listing set (#613), still shot, into work\.
    & "$H\Shot-AddText.ps1" -X 1390 -Y 1420 -Text $Date -Name "s-add-text"
    & "$H\Shot-Redact.ps1" -Lang $Lang -Save -Y1 580 -Y2 616 -Name "07-redact"
}

# -------------------------------------------- slots 1, 5 and 6: the twelve-page terms
# Shot in the order that leaves the window cleanest for the next step: the pane opens
# and closes, the find bar opens and closes, and reading mode is last because it is the
# one state that takes the whole window.
if ($Step -in 'all', 'terms') {
    Reset-App
    & "$H\Setup-Frame.ps1" -W 2500 -T 1550 -Pdf $terms -Fit "ActualSizeItem" -ZoomIn 0 -Name probe-terms
    if ($TileX1) {
        & "$H\Shot-Pages.ps1" -X1 $TileX1 -Y1 $TileY1 -X2 $TileX2 -Y2 $TileY2
    } else {
        & "$H\Shot-Pages.ps1"      # probe only; read the coordinates off probe-pages.png
    }
    & "$H\Shot-Find.ps1" -Term $Term
    & "$H\Shot-Reading.ps1" -Pages 3
}

# ------------------------------------------------------------------ slot 8: home
# The posed recents list -- see Shot-Home.ps1, which is the privacy control for this
# slot rather than a convenience.
if ($Step -in 'all', 'home') {
    & "$H\Shot-Home.ps1" -Paths @($terms, $doc, $scan)
}

if ($Step -eq 'all') {
    $work = Join-Path $shots 'work'
    New-Item -ItemType Directory -Force $work | Out-Null
    Get-ChildItem $shots -Filter *.png | Where-Object { $_.Name -notmatch '^0[1-8]-' } | Move-Item -Destination $work -Force
    Get-ChildItem $shots -Filter *.png | Select-Object Name, Length | Format-Table | Out-String
}
