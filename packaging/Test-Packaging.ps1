[CmdletBinding()]
param([string]$ReleaseDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $repository 'artifacts\release\0.1.0'
}
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$zips = @(Get-ChildItem -LiteralPath $release -Filter 'PaperBridge-*-win-x64.zip' -File)
if ($zips.Count -ne 1) { throw 'Exactly one PaperBridge release ZIP is required.' }
$zip = $zips[0]

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('paperbridge-package-test-' + [guid]::NewGuid().ToString('N'))
$expanded = Join-Path $tempRoot 'expanded'
$install = Join-Path $tempRoot 'install\PaperBridge'
$data = Join-Path $tempRoot 'data\PaperBridge'
$backup = Join-Path $tempRoot 'data-backup.zip'
try {
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $expanded
    & (Join-Path $expanded 'Install-PaperBridge.ps1') -PackageDirectory (Join-Path $expanded 'app') -InstallDirectory $install -NoStartMenuShortcut
    if (-not (Test-Path -LiteralPath (Join-Path $install 'PaperBridge.App.exe'))) { throw 'Fresh install did not create the executable.' }

    $upgradeMarker = Join-Path $install 'upgrade-marker.txt'
    Set-Content -LiteralPath $upgradeMarker -Value 'old'
    & (Join-Path $expanded 'Install-PaperBridge.ps1') -PackageDirectory (Join-Path $expanded 'app') -InstallDirectory $install -NoStartMenuShortcut
    if (Test-Path -LiteralPath $upgradeMarker) { throw 'Upgrade did not atomically replace old program files.' }

    [IO.Directory]::CreateDirectory((Join-Path $data 'Data')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $data 'Library')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $data 'Settings')) | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'Data\paperbridge.db') -Value 'isolated-test-data'
    Set-Content -LiteralPath (Join-Path $data 'Library\fixture.pdf') -Value 'self-generated-test-fixture'
    Set-Content -LiteralPath (Join-Path $data 'Settings\translation.json') -Value '{}'
    & (Join-Path $expanded 'Backup-PaperBridgeData.ps1') -DataDirectory $data -OutputPath $backup
    Set-Content -LiteralPath (Join-Path $data 'Data\paperbridge.db') -Value 'changed'
    & (Join-Path $expanded 'Restore-PaperBridgeData.ps1') -BackupPath $backup -DataDirectory $data
    if ((Get-Content -LiteralPath (Join-Path $data 'Data\paperbridge.db') -Raw).Trim() -ne 'isolated-test-data') {
        throw 'Data restore did not recover the original isolated test data.'
    }

    & (Join-Path $expanded 'Uninstall-PaperBridge.ps1') -InstallDirectory $install -DataDirectory $data -NoStartMenuShortcut -Confirm:$false
    if (Test-Path -LiteralPath $install) { throw 'Program-only uninstall left install files behind.' }
    if (-not (Test-Path -LiteralPath $data)) { throw 'Program-only uninstall removed user data.' }

    & (Join-Path $expanded 'Install-PaperBridge.ps1') -PackageDirectory (Join-Path $expanded 'app') -InstallDirectory $install -NoStartMenuShortcut
    & (Join-Path $expanded 'Uninstall-PaperBridge.ps1') -InstallDirectory $install -DataDirectory $data -DeleteUserData -NoStartMenuShortcut -Confirm:$false
    if ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $data)) {
        throw 'Explicit destructive uninstall did not remove isolated install and data directories.'
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Write-Host 'Fresh install, upgrade, data backup/restore, data-preserving uninstall, and explicit data deletion passed.'
