# Captures what the Windows app looks like, using the app's own --screenshot
# mode (#84). The macOS, iOS and Android equivalents run in CI; this one does
# not, and cannot:
#
#   RenderTargetBitmap needs a compositor. On a GitHub windows-latest runner the
#   app launches, creates the output file, and then hangs in RenderAsync — the
#   artifact is a zero-byte PNG and the step still goes green, because
#   PowerShell does not fail a step on a child process's exit code. That was
#   tried and reverted; see #84.
#
# So Windows conformance screenshots are a local procedure. This script is the
# whole of it.
#
#   pwsh tools/screenshot-windows.ps1 [-Out <dir>] [-Fixtures <dir>]
#
# Needs a .NET 8 SDK. If there is none, a per-user install with no admin:
#   irm https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\di.ps1
#   & $env:TEMP\di.ps1 -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"

param(
    [string]$Out = "$PSScriptRoot\..\artifacts\windows-screenshots",
    [string]$Fixtures = "$env:TEMP\megapdf-fixtures"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot\.."
$dotnet = if ($env:DOTNET_ROOT) { "$env:DOTNET_ROOT\dotnet.exe" }
          elseif (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") { "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
          else { "dotnet" }

& $dotnet build "$repo\src\MegaPDF.App\MegaPDF.App.csproj" -c Release -r win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force -Path $Fixtures, $Out | Out-Null
python3 "$repo\tools\gen_test_fixtures.py" $Fixtures

$exe = "$repo\src\MegaPDF.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\MegaPDF.exe"
$pdf = "$Fixtures\demo.pdf"

# name, extra args. The three light states plus dark, which is where a wrong
# token value survives longest — every one is defined twice and only the light
# half is ever looked at.
$shots = @(
    @{ name = "01-empty-state";   args = @() },
    @{ name = "02-find";          args = @($pdf, "--screenshot-state", "find") },
    @{ name = "03-mode-banner";   args = @($pdf, "--screenshot-state", "mode") },
    @{ name = "04-dark-document"; args = @($pdf, "--theme", "dark") },
    @{ name = "05-dark-find";     args = @($pdf, "--theme", "dark", "--screenshot-state", "find") }
)

foreach ($shot in $shots) {
    $path = Join-Path $Out "$($shot.name).png"
    & $exe @($shot.args) --screenshot $path
    # A zero-byte or missing PNG is the failure this script exists to make loud:
    # the app creates the file before it renders into it.
    if ($LASTEXITCODE -ne 0) { throw "$($shot.name): exit $LASTEXITCODE" }
    if (-not (Test-Path $path)) { throw "$($shot.name): no file written" }
    if ((Get-Item $path).Length -eq 0) { throw "$($shot.name): wrote an empty PNG — RenderAsync produced nothing" }
}

Get-ChildItem $Out | Select-Object Name, Length
