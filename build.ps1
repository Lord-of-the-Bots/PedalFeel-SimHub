param(
    [Parameter(Mandatory=$true)][string]$SimHubDirectory,
    [string]$Dotnet = 'dotnet',
    [string]$Toolchain,
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
$hostDirectory = (Resolve-Path -LiteralPath $SimHubDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $hostDirectory 'SimHub.Plugins.dll'))) { throw 'SimHub.Plugins.dll not found in the selected host directory.' }
if (!$SkipNative) {
    if ($Toolchain) { & (Join-Path $PSScriptRoot 'native/build.ps1') -Toolchain $Toolchain }
    else { & (Join-Path $PSScriptRoot 'native/build.ps1') }
}
& $Dotnet build (Join-Path $PSScriptRoot 'tests/PedalFeel.Tests.csproj') -c Release "-p:SimHubDir=$hostDirectory" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }
& (Join-Path $PSScriptRoot 'tests/bin/Release/net48/PedalFeel.Tests.exe') $hostDirectory
if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed.' }
Write-Host 'Build and all tests passed.'
