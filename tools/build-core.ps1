# Builds the shared engine core as megapdf_core.dll for win-x64 (#38, ADR-003).
#
#     tools\build-core.ps1            # -> core\build\win-x64\Release\megapdf_core.dll
#
# MegaPDF.Core.csproj runs this itself when the DLL is missing or older than
# core\*.cpp, so `dotnet build` is normally all anyone types. Needs MSVC and the
# Windows SDK (VS 2022 Build Tools with the C++ workload) and CMake; the Build
# Tools' own CMake is used when none is on PATH. CMake generates the pdfium
# import library from libs\pdfium\win-x64\pdfium.dll's export table.
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
$build = Join-Path $repo 'core\build\win-x64'
& $cmake -S $src -B $build -A x64 -DCMAKE_BUILD_TYPE=Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
& $cmake --build $build --config Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

$dll = Join-Path $build 'Release\megapdf_core.dll'
if (-not (Test-Path $dll)) { throw "build finished but $dll is missing" }
Write-Host "built $dll"
