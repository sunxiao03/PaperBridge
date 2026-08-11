[CmdletBinding()]
param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot 'app'),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\PaperBridge'),
    [switch]$NoStartMenuShortcut
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-SafeTarget([string]$Path, [string]$Purpose) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Purpose path is empty." }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = [IO.Path]::GetPathRoot($full).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($full -eq $root -or -not [IO.Path]::GetDirectoryName($full)) {
        throw "$Purpose path must not be a drive root: $full"
    }
    return $full
}

$source = Resolve-SafeTarget $PackageDirectory 'Package'
$target = Resolve-SafeTarget $InstallDirectory 'Install'
if (-not (Test-Path -LiteralPath (Join-Path $source 'PaperBridge.App.exe') -PathType Leaf)) {
    throw "PaperBridge.App.exe is missing from package directory: $source"
}

$parent = [IO.Path]::GetDirectoryName($target)
[IO.Directory]::CreateDirectory($parent) | Out-Null
$stage = Join-Path $parent ('.PaperBridge.install-' + [guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ('.PaperBridge.previous-' + [guid]::NewGuid().ToString('N'))

try {
    Copy-Item -LiteralPath $source -Destination $stage -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-PaperBridge.ps1') -Destination $stage
    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination $previous
    }
    Move-Item -LiteralPath $stage -Destination $target
    if (Test-Path -LiteralPath $previous) {
        Remove-Item -LiteralPath $previous -Recurse -Force
    }
}
catch {
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $target)) {
        Move-Item -LiteralPath $previous -Destination $target
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

if (-not $NoStartMenuShortcut) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    [IO.Directory]::CreateDirectory($startMenu) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenu 'PaperBridge.lnk'))
    $shortcut.TargetPath = Join-Path $target 'PaperBridge.App.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.Description = 'PaperBridge local academic PDF reader'
    $shortcut.Save()
}

Write-Host "PaperBridge installed to: $target"
Write-Host 'Existing user data was preserved.'
