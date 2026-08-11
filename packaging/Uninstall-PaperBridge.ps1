[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\PaperBridge'),
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'PaperBridge'),
    [switch]$DeleteUserData,
    [switch]$DeleteCredentials,
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

$install = Resolve-SafeTarget $InstallDirectory 'Install'
$data = Resolve-SafeTarget $DataDirectory 'Data'

$running = Get-Process -Name 'PaperBridge.App' -ErrorAction SilentlyContinue
if ($running) { throw 'Close PaperBridge before uninstalling it.' }

if (Test-Path -LiteralPath $install) {
    if ($PSCmdlet.ShouldProcess($install, 'Remove PaperBridge program files')) {
        Remove-Item -LiteralPath $install -Recurse -Force
    }
}

if (-not $NoStartMenuShortcut) {
    $shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\PaperBridge.lnk'
    if ((Test-Path -LiteralPath $shortcut) -and $PSCmdlet.ShouldProcess($shortcut, 'Remove Start menu shortcut')) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}

if ($DeleteUserData -and (Test-Path -LiteralPath $data)) {
    if ($PSCmdlet.ShouldProcess($data, 'Permanently remove PaperBridge user data')) {
        Remove-Item -LiteralPath $data -Recurse -Force
    }
}

if ($DeleteCredentials) {
    foreach ($target in @(
        'PaperBridge/translation/openai',
        'PaperBridge/translation/deepseek',
        'PaperBridge/translation/openai-compatible')) {
        if ($PSCmdlet.ShouldProcess($target, 'Delete Windows Credential Manager entry')) {
            & "$env:SystemRoot\System32\cmdkey.exe" "/delete:$target" 2>$null | Out-Null
        }
    }
}

Write-Host 'PaperBridge program files were removed.'
if ($DeleteUserData) { Write-Host 'User data was permanently removed.' }
else { Write-Host "User data was preserved at: $data" }
if ($DeleteCredentials) { Write-Host 'Known PaperBridge API credential entries were requested for deletion.' }
else { Write-Host 'Windows Credential Manager entries were preserved.' }
