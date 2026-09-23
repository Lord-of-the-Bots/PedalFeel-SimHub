param([string]$SimHubDirectory)
$ErrorActionPreference = 'Stop'
try {
    if (Get-Process -Name SimHubWPF -ErrorAction SilentlyContinue) { throw 'Close SimHub before installing these files, then run this installer again.' }
    if (!$SimHubDirectory) {
        $defaultDirectory = Join-Path ${env:ProgramFiles(x86)} 'SimHub'
        if (Test-Path -LiteralPath (Join-Path $defaultDirectory 'SimHubWPF.exe')) { $SimHubDirectory = $defaultDirectory }
        else {
            Add-Type -AssemblyName System.Windows.Forms
            $dialog = New-Object System.Windows.Forms.OpenFileDialog
            $dialog.Title = 'Select SimHubWPF.exe in the SimHub installation folder'
            $dialog.Filter = 'SimHub|SimHubWPF.exe'
            if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
            $SimHubDirectory = Split-Path -Parent $dialog.FileName
        }
    }
    $destination = (Resolve-Path -LiteralPath $SimHubDirectory).Path
    if (!(Test-Path -LiteralPath (Join-Path $destination 'SimHubWPF.exe'))) { throw 'The selected folder does not contain SimHubWPF.exe.' }
    $source = Join-Path $PSScriptRoot 'plugin'
    $files = Get-ChildItem -LiteralPath $source -File -Recurse
    $backup = Join-Path $destination ('PedalFeel/Backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\','/')
        $target = Join-Path $destination $relative
        if (Test-Path -LiteralPath $target) {
            $old = Join-Path $backup $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $old) | Out-Null
            Copy-Item -LiteralPath $target -Destination $old
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        Unblock-File -LiteralPath $target
    }
    Write-Host 'Installed. Start SimHub and open Devices > Simagic Haptic Pedals Reactor > PedalFeel.' -ForegroundColor Green
    Write-Host 'Enable automatic activation for iRacing in the PedalFeel tab when ready. Existing settings are retained.'
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
