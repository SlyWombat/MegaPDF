# Builds the shared engine core as megapdf_core.dll for win-x64 or win-arm64 (#38,
# ADR-003, #288).
#
#     tools\build-core.ps1               # -> core\build\win-x64\Release\megapdf_core.dll
#     tools\build-core.ps1 -Arch arm64   # -> core\build\win-arm64\Release\megapdf_core.dll
#
# arm64 on an x64 machine cross-compiles, and needs the Build Tools' "MSVC v143 - VS
# 2022 C++ ARM64/ARM64EC build tools" component; the GitHub windows-latest image has it.
#
# MegaPDF.Core.csproj runs this itself when the DLL is missing or older than
# core\*.cpp, so `dotnet build` is normally all anyone types. Needs MSVC and the
# Windows SDK (VS 2022 Build Tools with the C++ workload) and CMake; the Build
# Tools' own CMake is used when none is on PATH. CMake generates the pdfium
# import library from libs\pdfium\win-<arch>\pdfium.dll's export table.
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "${env:ProgramFiles}\CMake\bin\cmake.exe"
    )
    $cmake = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $cmake) {
    throw "CMake not found. Install VS 2022 Build Tools with the 'Desktop development with C++' workload (it includes CMake), or CMake itself."
}

$src = Join-Path $repo 'core'
$build = Join-Path $repo "core\build\win-$Arch"
$platform = @{ x64 = 'x64'; arm64 = 'ARM64' }[$Arch]
& $cmake -S $src -B $build -A $platform -DCMAKE_BUILD_TYPE=Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
& $cmake --build $build --config Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

$dll = Join-Path $build 'Release\megapdf_core.dll'
if (-not (Test-Path $dll)) { throw "build finished but $dll is missing" }
Write-Host "built $dll"
