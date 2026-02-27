param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$appId = '{6FD34B2B-6A5F-4F64-9282-78E5A65F85C9}_is1'
$paths = @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId",
    "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId",
    "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
)

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$programFiles = ${env:ProgramFiles}
$targets = @(
    (Join-Path $localAppData 'Malie'),
    (Join-Path $localAppData 'IsometricLiveWeatherDesktop'),
    (Join-Path $localAppData 'Programs\Malie'),
    (Join-Path $programFiles 'Malie')
)

$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Mâlie.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Mâlie.lnk'
$targets += @($startMenu, $desktop)

Write-Host 'Cleaning legacy Malie installer registrations and files...'

foreach ($path in $paths) {
    if (Test-Path $path) {
        Write-Host "Removing uninstall key: $path"
        if (-not $WhatIf) {
            Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

foreach ($target in $targets | Select-Object -Unique) {
    if (-not (Test-Path $target)) {
        continue
    }

    Write-Host "Removing: $target"
    if ($WhatIf) {
        continue
    }

    try {
        $item = Get-Item $target -ErrorAction Stop
        if ($item.PSIsContainer) {
            Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            Remove-Item $target -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Warning "Failed to remove '$target': $($_.Exception.Message)"
    }
}

Write-Host 'Legacy cleanup complete.'
