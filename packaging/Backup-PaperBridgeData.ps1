[CmdletBinding()]
param(
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'PaperBridge'),
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) ('PaperBridge-data-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip'))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [IO.Path]::GetFullPath($DataDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Data directory not found: $source" }
if ([IO.Path]::GetExtension($destination) -ne '.zip') { throw 'Backup output must use the .zip extension.' }
if (Test-Path -LiteralPath $destination) { throw "Backup output already exists: $destination" }
if (Get-Process -Name 'PaperBridge.App' -ErrorAction SilentlyContinue) {
    throw 'Close PaperBridge before creating a complete data backup.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('paperbridge-data-backup-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $tempRoot 'PaperBridgeData'
try {
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    foreach ($name in @('Data', 'Library', 'Settings')) {
        $item = Join-Path $source $name
        if (Test-Path -LiteralPath $item) { Copy-Item -LiteralPath $item -Destination $payload -Recurse }
    }
    $files = Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($payload.Length).TrimStart('\', '/').Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    [ordered]@{
        format = 'paperbridge-data-backup-v1'
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
        credentialsIncluded = $false
        files = @($files)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'backup-manifest.json') -Encoding UTF8
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Compress-Archive -LiteralPath $payload -DestinationPath $destination -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Write-Host "PaperBridge data backup created: $destination"
Write-Host 'Windows Credential Manager secrets are intentionally not included.'
