param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$version = '0.5.0'
$stage = Join-Path $PSScriptRoot ('build/package-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$binary = Join-Path $stage "PedalFeel-SimHub-$version"
$source = Join-Path $stage "PedalFeel-SimHub-$version-source"
New-Item -ItemType Directory -Force -Path $OutputDirectory,$binary,$source | Out-Null
function Copy-File([string]$From, [string]$To) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $To) | Out-Null
    Copy-Item -LiteralPath $From -Destination $To -Force
}
Copy-File (Join-Path $PSScriptRoot 'managed/bin/Release/net48/PedalFeel.SimHub.dll') (Join-Path $binary 'plugin/PedalFeel.SimHub.dll')
foreach ($architecture in @('x86','x64')) {
    Copy-File (Join-Path $PSScriptRoot "native/build/$architecture/PedalFeel.Native.dll") (Join-Path $binary "plugin/PedalFeel/$architecture/PedalFeel.Native.dll")
}
Copy-File (Join-Path $PSScriptRoot 'README.md') (Join-Path $binary 'README.md')
Copy-File (Join-Path $PSScriptRoot 'README.md') (Join-Path $binary 'plugin/PedalFeel/README.md')
Copy-File (Join-Path $PSScriptRoot 'CONTRIBUTING.md') (Join-Path $binary 'CONTRIBUTING.md')
Copy-File (Join-Path $PSScriptRoot 'PRESETS.md') (Join-Path $binary 'PRESETS.md')
Copy-File (Join-Path $PSScriptRoot 'PRESETS.md') (Join-Path $binary 'plugin/PedalFeel/PRESETS.md')
Copy-File (Join-Path $PSScriptRoot 'distribution/Install.ps1') (Join-Path $binary 'Install.ps1')
Copy-File (Join-Path $PSScriptRoot 'distribution/Install.cmd') (Join-Path $binary 'Install.cmd')
$licenses = @{
    'native/LICENSE' = 'PedalFeel-SimHub-LICENSE.txt'
    'native/vendor/pedalfeel/LICENSE' = 'PedalFeel-original-LICENSE.txt'
    'native/vendor/pedalfeel/THIRD_PARTY_NOTICES.md' = 'iRacing-SDK-NOTICES.md'
    'native/licenses/LLVM-LICENSE.TXT' = 'LLVM-LICENSE.TXT'
    'native/licenses/MinGW-w64-runtime.txt' = 'MinGW-w64-runtime.txt'
}
foreach ($entry in $licenses.GetEnumerator()) { Copy-File (Join-Path $PSScriptRoot $entry.Key) (Join-Path $binary ('plugin/PedalFeel/licenses/' + $entry.Value)) }
foreach ($directory in @('managed','tests','distribution')) {
    $extensions = if ($directory -eq 'distribution') { @('.ps1','.cmd') } else { @('.cs','.csproj','.json') }
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $directory) -File | Where-Object { $_.Extension -in $extensions -and ($directory -ne 'distribution' -or $_.Name -in @('Install.ps1','Install.cmd')) } | ForEach-Object { Copy-File $_.FullName (Join-Path $source ($directory + '/' + $_.Name)) }
}
foreach ($directory in @('managed/Locales','native/include','native/src','native/tests','native/vendor','native/licenses','docs','.github')) {
    $from = Join-Path $PSScriptRoot $directory
    Get-ChildItem -LiteralPath $from -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')
        Copy-File $_.FullName (Join-Path $source $relative)
    }
}
foreach ($relative in @('README.md','CONTRIBUTING.md','PRESETS.md','build.ps1','package.ps1','.gitignore','native/build.ps1','native/README.md','native/LICENSE','native/.gitignore')) {
    Copy-File (Join-Path $PSScriptRoot $relative) (Join-Path $source $relative)
}
Copy-File (Join-Path $PSScriptRoot 'native/LICENSE') (Join-Path $source 'LICENSE')
Copy-File (Join-Path $PSScriptRoot 'native/LICENSE') (Join-Path $binary 'LICENSE')
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')
    Copy-File $_.FullName (Join-Path $binary $relative)
}
$manifest = Get-ChildItem -LiteralPath (Join-Path $binary 'plugin') -File -Recurse | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    $relative = $_.FullName.Substring($binary.Length).TrimStart('\','/')
    $hash.Hash.ToLowerInvariant() + '  ' + $relative
}
$manifest | Set-Content -LiteralPath (Join-Path $binary 'SHA256SUMS.txt') -Encoding utf8
Compress-Archive -Path $binary -DestinationPath (Join-Path $OutputDirectory "PedalFeel-SimHub-$version.zip") -Force
Compress-Archive -Path $source -DestinationPath (Join-Path $OutputDirectory "PedalFeel-SimHub-$version-source.zip") -Force
Copy-File (Join-Path $PSScriptRoot 'README.md') (Join-Path $OutputDirectory 'PedalFeel-Guide.md')
Write-Output "PackageStage=$binary"
Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Name -Match '^PedalFeel' | Select-Object Name,Length
