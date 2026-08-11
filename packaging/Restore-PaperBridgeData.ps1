[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$BackupPath,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'PaperBridge')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$archive = [IO.Path]::GetFullPath($BackupPath)
$target = [IO.Path]::GetFullPath($DataDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$root = [IO.Path]::GetPathRoot($target).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($target -eq $root -or -not [IO.Path]::GetDirectoryName($target)) { throw 'Data directory must not be a drive root.' }
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Backup not found: $archive" }
if (Get-Process -Name 'PaperBridge.App' -ErrorAction SilentlyContinue) {
    throw 'Close PaperBridge before restoring a data backup.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('paperbridge-data-restore-' + [guid]::NewGuid().ToString('N'))
$expanded = Join-Path $tempRoot 'expanded'
$parent = [IO.Path]::GetDirectoryName($target)
$stage = Join-Path $parent ('.PaperBridge.restore-' + [guid]::NewGuid().ToString('N'))
$rollback = Join-Path $parent ('PaperBridge.rollback-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    $payload = Join-Path $expanded 'PaperBridgeData'
    $manifestPath = Join-Path $payload 'backup-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'The archive is not a PaperBridge data backup.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.format -ne 'paperbridge-data-backup-v1') { throw 'Unsupported PaperBridge backup format.' }
    foreach ($entry in $manifest.files) {
        $relative = [string]$entry.path
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') {
            throw "Unsafe path in backup manifest: $relative"
        }
        $file = Join-Path $payload $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Backup file is missing: $relative" }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne [string]$entry.sha256) { throw "Backup checksum mismatch: $relative" }
    }

    Copy-Item -LiteralPath $payload -Destination $stage -Recurse
    Remove-Item -LiteralPath (Join-Path $stage 'backup-manifest.json') -Force
    [IO.Directory]::CreateDirectory((Join-Path $stage 'Backups')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $stage 'Logs')) | Out-Null
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $rollback }
    try { Move-Item -LiteralPath $stage -Destination $target }
    catch {
        if ((Test-Path -LiteralPath $rollback) -and -not (Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $rollback -Destination $target
        }
        throw
    }
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Write-Host "PaperBridge data restored to: $target"
if (Test-Path -LiteralPath $rollback) { Write-Host "Previous data retained for rollback at: $rollback" }
