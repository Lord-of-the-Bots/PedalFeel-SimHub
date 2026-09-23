param(
    [string]$Toolchain = (Join-Path $PSScriptRoot '.tools\llvm-mingw-20260922-ucrt-x86_64'),
    [ValidateSet('x86','x64','both')][string]$Architecture = 'both'
)
$ErrorActionPreference = 'Stop'
$targets = if ($Architecture -eq 'both') { @('x86','x64') } else { @($Architecture) }
$upstream = Join-Path $PSScriptRoot 'vendor\pedalfeel'
foreach ($target in $targets) {
    $triple = if ($target -eq 'x86') { 'i686' } else { 'x86_64' }
    $compiler = Join-Path $Toolchain "bin\$triple-w64-mingw32-clang++.exe"
    if (-not (Test-Path -LiteralPath $compiler)) { throw "Missing portable compiler: $compiler. Supply -Toolchain; see README.md." }
    $outDir = Join-Path $PSScriptRoot "build\$target"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $common = @('-std=c++20','-O2','-static','-DNOMINMAX','-DWIN32_LEAN_AND_MEAN',
        '-Wall','-Wextra','-Werror', '-I', (Join-Path $PSScriptRoot 'include'),
        '-I', (Join-Path $PSScriptRoot 'src'), '-I', (Join-Path $upstream 'include'))
    $sources = @((Join-Path $PSScriptRoot 'src\engine.cpp'),
        (Join-Path $PSScriptRoot 'src\preview.cpp'),
        (Join-Path $PSScriptRoot 'src\pedalfeel_api.cpp'),
        (Join-Path $upstream 'src\renderer.cpp'),
        (Join-Path $upstream 'src\iracing_telemetry.cpp'))
    & $compiler @common '-DPF_BUILD_DLL' '-shared' @sources '-o' (Join-Path $outDir 'PedalFeel.Native.dll')
    if ($LASTEXITCODE -ne 0) { throw "Native DLL build failed: $target" }
    & $compiler @common @sources (Join-Path $PSScriptRoot 'tests\bridge_tests.cpp') '-o' (Join-Path $outDir 'bridge_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Bridge test build failed: $target" }
    & (Join-Path $outDir 'bridge_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Bridge tests failed: $target" }
    & $compiler @common @sources (Join-Path $PSScriptRoot 'tests\preview_tests.cpp') '-o' (Join-Path $outDir 'preview_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Preview test build failed: $target" }
    & (Join-Path $outDir 'preview_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Preview tests failed: $target" }
    & $compiler @common (Join-Path $upstream 'src\renderer.cpp') (Join-Path $upstream 'tests\engine_tests.cpp') '-o' (Join-Path $outDir 'upstream_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Upstream test build failed: $target" }
    & (Join-Path $outDir 'upstream_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw "Upstream tests failed: $target" }
    Write-Host "Built and tested $target : $(Join-Path $outDir 'PedalFeel.Native.dll')"
}
