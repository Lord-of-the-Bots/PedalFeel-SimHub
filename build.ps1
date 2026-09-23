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
& $Dotnet build (Join-Path $PSScriptRoot 'managed/PedalFeel.SimHub.csproj') -c Release "-p:SimHubDir=$hostDirectory" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }
Write-Host 'Built PedalFeel.SimHub.dll.'
