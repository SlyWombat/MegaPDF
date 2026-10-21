# The two rules every Windows QA script lives by (#717):
#
#   1. A QA script never loses the person's own MegaPDF data. Start-QaUserDataWatch copies
#      %LOCALAPPDATA%\MegaPDF and the package's own data folder
#      (%LOCALAPPDATA%\Packages\ElectricRV.MegaPDF_fba94j4nmgb9y) to
#      D:\megapdf-qa\backup-<timestamp>\ before anything else runs, and
#      Stop-QaUserDataWatch compares them at the end and says, loudly, what changed.
#
#   2. A QA script never removes a package it did not install itself. Removing a package
#      deletes its data folder under %LOCALAPPDATA%\Packages, and the Store install and
#      every test build share one identity (the test cert's subject is the Store
#      publisher), so one folder. A Store-signed package is Dave's: Remove-QaTestPackage
#      stops and says so. A Developer- or None-signed one is removed only when
#      Install-QaTestPackage (or Register-QaTestPackage) recorded installing it.
#
# Dot-source it:  . (Join-Path $PSScriptRoot 'qa-safety.ps1')
#
# Keep this file ASCII-only: Windows PowerShell reads a .ps1 with no BOM as ANSI, so a
# stray em dash breaks parsing with a misleading "Missing closing }".

$global:QaPackageName = 'ElectricRV.MegaPDF'
$global:QaPackageFamily = 'ElectricRV.MegaPDF_fba94j4nmgb9y'
$global:QaScratch = 'D:\megapdf-qa'
# What Install-QaTestPackage installed: the PackageFullName, one line. Removal needs it.
$global:QaInstalledMarker = Join-Path $global:QaScratch 'qa-installed-package.txt'

function Get-QaUserDataFolders {
    [ordered]@{
        'MegaPDF' = (Join-Path $env:LOCALAPPDATA 'MegaPDF')
        'Package' = (Join-Path $env:LOCALAPPDATA "Packages\$global:QaPackageFamily")
    }
}

# .NET, not Get-FileHash: the check also runs from PowerShell.Exiting, when the engine is
# shutting down and a cmdlet from a module that is not loaded yet cannot be.
function Get-QaFileHash([string]$Path) {
    $hash = 'unreadable'
    $stream = $null
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete')
        $bytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash($stream)
        $hash = [System.BitConverter]::ToString($bytes).Replace('-', '')
    } catch { }
    if ($stream) { try { $stream.Dispose() } catch { } }
    $hash
}

function Get-QaFingerprint([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder)) { return $null }
    $lines = foreach ($item in Get-ChildItem -LiteralPath $Folder -Recurse -Force -ErrorAction SilentlyContinue) {
        $rel = $item.FullName.Substring($Folder.Length)
        if ($item.PSIsContainer) { "folder  $rel" }
        else {
            "$(Get-QaFileHash $item.FullName)  $rel"
        }
    }
    return @($lines | Sort-Object)
}

# Copies the person's MegaPDF data aside and fingerprints it. Returns the watch that
# Stop-QaUserDataWatch takes. Call it before the script does anything else.
function Start-QaUserDataWatch([string]$Label = 'qa') {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $backup = Join-Path $global:QaScratch "backup-$stamp"
    New-Item -ItemType Directory -Force $backup | Out-Null
    $before = @{}
    foreach ($entry in (Get-QaUserDataFolders).GetEnumerator()) {
        $before[$entry.Key] = Get-QaFingerprint $entry.Value
        if ($null -ne $before[$entry.Key]) {
            # robocopy, not Copy-Item: it keeps timestamps and carries on past a locked file.
            $null = & robocopy.exe $entry.Value (Join-Path $backup $entry.Key) /E /COPY:DAT /DCOPY:T /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS /NP
            if ($LASTEXITCODE -ge 8) { Write-Host "!! the backup of $($entry.Value) was incomplete (robocopy $LASTEXITCODE)" -ForegroundColor Red }
        }
    }
    $recent = Join-Path $env:APPDATA 'Microsoft\Windows\Recent'
    $watch = [pscustomobject]@{
        Label  = $Label
        Backup = $backup
        Before = $before
        Recent = @(Get-ChildItem -LiteralPath $recent -Filter *.lnk -Force -ErrorAction SilentlyContinue | ForEach-Object Name)
        Start  = Get-Date
    }
    $summary = ($before.GetEnumerator() | ForEach-Object { if ($null -eq $_.Value) { "$($_.Key): absent" } else { "$($_.Key): $($_.Value.Count) entries" } }) -join ', '
    Write-Host "user data backed up to $backup ($summary)"
    return $watch
}

# Compares the person's MegaPDF data with what Start-QaUserDataWatch found. Identical: the
# backup is deleted and $true comes back. Different: every difference is printed in red,
# the backup is kept for restoring, and $false comes back.
function Stop-QaUserDataWatch($Watch) {
    if (-not $Watch) { return $true }
    $report = @()
    foreach ($entry in (Get-QaUserDataFolders).GetEnumerator()) {
        $was = $Watch.Before[$entry.Key]
        $now = Get-QaFingerprint $entry.Value
        if ($null -eq $was -and $null -eq $now) { continue }
        if ($null -eq $now) { $report += "DELETED: $($entry.Value) (restore it from $($Watch.Backup)\$($entry.Key))"; continue }
        if ($null -eq $was) { $report += "CREATED: $($entry.Value)"; continue }
        foreach ($d in Compare-Object $was $now) {
            $side = if ($d.SideIndicator -eq '<=') { 'was' } else { 'now' }
            $report += "$($entry.Key) $side  $($d.InputObject)"
        }
    }
    # Explorer's Recent folder: a shortcut per file the app or its file dialog opened.
    $recent = Join-Path $env:APPDATA 'Microsoft\Windows\Recent'
    $added = @(Get-ChildItem -LiteralPath $recent -Filter *.lnk -Force -ErrorAction SilentlyContinue |
               Where-Object { $Watch.Recent -notcontains $_.Name } | ForEach-Object Name)
    if ($added.Count -gt 0) {
        Write-Host "note: $($added.Count) shortcut(s) were added to Explorer's Recent folder during the run: $($added -join ', ')" -ForegroundColor Yellow
    }
    if ($report.Count -eq 0) {
        Remove-Item -LiteralPath $Watch.Backup -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "user data: unchanged since $($Watch.Start.ToString('s')) (backup removed)"
        return $true
    }
    $report | Set-Content (Join-Path $Watch.Backup 'DIFFERENCES.txt')
    Write-Host ''
    Write-Host '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!' -ForegroundColor Red
    Write-Host "!! $($Watch.Label): THE PERSON'S MEGAPDF DATA CHANGED DURING THIS RUN" -ForegroundColor Red
    Write-Host "!! the copy taken before it is in $($Watch.Backup)" -ForegroundColor Red
    foreach ($line in $report) { Write-Host "!!   $line" -ForegroundColor Red }
    Write-Host '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!' -ForegroundColor Red
    return $false
}

# For a script that dot-sources a helper rather than running top to bottom: the check
# runs when this PowerShell exits, whichever way the script ends short of being killed.
function Watch-QaUserDataUntilExit([string]$Label = 'qa') {
    if ($global:QaExitWatch) { return }
    $global:QaExitWatch = Start-QaUserDataWatch $Label
    $null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action {
        $null = Stop-QaUserDataWatch $global:QaExitWatch
    }
}

# The installed MegaPDF package, or $null.
function Get-QaPackage {
    Get-AppxPackage $global:QaPackageName -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Test-QaPackageIsOurs($Package) {
    if (-not $Package) { return $false }
    if ($Package.SignatureKind -notin 'Developer', 'None') { return $false }
    if (-not (Test-Path -LiteralPath $global:QaInstalledMarker)) { return $false }
    return ((Get-Content -LiteralPath $global:QaInstalledMarker -TotalCount 1).Trim() -eq $Package.PackageFullName)
}

# Removes the installed MegaPDF package only if this tooling installed it. Anything else
# stops the script: a Store-signed package is Dave's own install, and removing it would
# delete his data with it.
function Remove-QaTestPackage {
    $pkg = Get-QaPackage
    if (-not $pkg) { return }
    if ($pkg.SignatureKind -eq 'Store') {
        throw "STOP: $($pkg.PackageFullName) is the Microsoft Store install (SignatureKind Store) -- the person's own MegaPDF. A QA script never removes it: removing a package deletes its data under $env:LOCALAPPDATA\Packages\$($pkg.PackageFamilyName). Do this run on another account or machine, or ask Dave."
    }
    if (-not (Test-QaPackageIsOurs $pkg)) {
        throw "STOP: $($pkg.PackageFullName) (SignatureKind $($pkg.SignatureKind)) was not installed by this tooling ($global:QaInstalledMarker does not name it). Not removing it. Back up its data (Start-QaUserDataWatch) and remove it by hand if it really is a test build."
    }
    Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
    Remove-Item -LiteralPath $global:QaInstalledMarker -Force -ErrorAction SilentlyContinue
    Write-Host "removed the test package $($pkg.PackageFullName)"
}

# Records an installed Developer/None-signed package as this tooling's, so it may later be
# removed: for a package installed by something this tooling ran (WACK installs the package
# under test itself), never for one that was there before the script started.
function Register-QaTestPackage {
    $pkg = Get-QaPackage
    if (-not $pkg) { return }
    if ($pkg.SignatureKind -notin 'Developer', 'None') {
        throw "STOP: $($pkg.PackageFullName) is signed '$($pkg.SignatureKind)', not a test build. Not recording it as one."
    }
    New-Item -ItemType Directory -Force $global:QaScratch | Out-Null
    Set-Content -LiteralPath $global:QaInstalledMarker -Value $pkg.PackageFullName
}

# Installs a test build, replacing only a test build this tooling installed. Add-AppxPackage
# of the same version is a no-op (RELEASING.md), so the old one is removed first -- through
# Remove-QaTestPackage, which refuses the Store install. Never installs over the Store
# install either: same identity, so it would replace Dave's app in place.
function Install-QaTestPackage([Parameter(Mandatory = $true)][string]$Msix) {
    if (-not (Test-Path -LiteralPath $Msix)) { throw "no such package: $Msix" }
    Remove-QaTestPackage
    Add-AppxPackage -Path $Msix -ErrorAction Stop
    Register-QaTestPackage
    $pkg = Get-QaPackage
    Write-Host "installed the test package $($pkg.PackageFullName) (SignatureKind $($pkg.SignatureKind))"
}
