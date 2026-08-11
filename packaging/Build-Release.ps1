[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\release')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path $PSScriptRoot -Parent
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if (-not $outputRootFull.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output must remain under $artifactRoot"
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid semantic version: $Version" }

$releaseDirectory = Join-Path $outputRootFull $Version
$packageStage = Join-Path $releaseDirectory 'package'
$appDirectory = Join-Path $packageStage 'app'
if (Test-Path -LiteralPath $releaseDirectory) { Remove-Item -LiteralPath $releaseDirectory -Recurse -Force }
[IO.Directory]::CreateDirectory($appDirectory) | Out-Null

Push-Location $repository
try {
    & dotnet publish 'src/PaperBridge.App/PaperBridge.App.csproj' `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $appDirectory `
        -p:Version=$Version `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

foreach ($script in @(
    'Install-PaperBridge.ps1',
    'Uninstall-PaperBridge.ps1',
    'Backup-PaperBridgeData.ps1',
    'Restore-PaperBridgeData.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $packageStage
}
foreach ($item in @(
    @{ Source = 'LICENSE'; Destination = 'LICENSE.txt' },
    @{ Source = 'THIRD_PARTY_NOTICES.md'; Destination = 'THIRD_PARTY_NOTICES.md' },
    @{ Source = 'PRIVACY.md'; Destination = 'PRIVACY.md' },
    @{ Source = 'SECURITY.md'; Destination = 'SECURITY.md' },
    @{ Source = 'SUPPORT.md'; Destination = 'SUPPORT.md' },
    @{ Source = 'docs/KNOWN_LIMITATIONS.md'; Destination = 'KNOWN_LIMITATIONS.md' },
    @{ Source = 'docs/RELEASE_NOTES_0.1.0.md'; Destination = 'RELEASE_NOTES.md' },
    @{ Source = 'docs/RELEASE_CHECKLIST.md'; Destination = 'RELEASE_CHECKLIST.md' },
    @{ Source = 'docs/INSTALLATION_AND_UNINSTALL.md'; Destination = 'INSTALLATION_AND_UNINSTALL.md' },
    @{ Source = 'docs/BACKUP_AND_RECOVERY.md'; Destination = 'BACKUP_AND_RECOVERY.md' })) {
    Copy-Item -LiteralPath (Join-Path $repository $item.Source) -Destination (Join-Path $packageStage $item.Destination)
}

$files = Get-ChildItem -LiteralPath $packageStage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($packageStage.Length).TrimStart('\', '/').Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    name = 'PaperBridge'
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageStage 'manifest.json') -Encoding UTF8

[xml]$packagesXml = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props') -Raw
$packages = @($packagesXml.Project.ItemGroup.PackageVersion | ForEach-Object {
    [ordered]@{ name = [string]$_.Include; versionInfo = [string]$_.Version }
})
$spdx = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "PaperBridge-$Version-win-x64"
    documentNamespace = "https://paperbridge.local/spdx/$Version/" + [guid]::NewGuid().ToString('N')
    creationInfo = [ordered]@{ created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); creators = @('Tool: packaging/Build-Release.ps1') }
    packages = @([ordered]@{
        name = 'PaperBridge'
        SPDXID = 'SPDXRef-Package-PaperBridge'
        versionInfo = $Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'MIT'
        licenseDeclared = 'MIT'
        externalRefs = @($packages | ForEach-Object {
            [ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$($_.name)@$($_.versionInfo)"
            }
        })
    })
}
$spdx | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageStage 'sbom.spdx.json') -Encoding UTF8

$zipName = "PaperBridge-$Version-win-x64.zip"
$zipPath = Join-Path $releaseDirectory $zipName
Compress-Archive -Path (Join-Path $packageStage '*') -DestinationPath $zipPath -CompressionLevel Optimal

foreach ($fileName in @('Install-PaperBridge.ps1', 'Uninstall-PaperBridge.ps1', 'Backup-PaperBridgeData.ps1', 'Restore-PaperBridgeData.ps1')) {
    Copy-Item -LiteralPath (Join-Path $packageStage $fileName) -Destination $releaseDirectory
}
foreach ($fileName in @('manifest.json', 'sbom.spdx.json', 'RELEASE_NOTES.md', 'PRIVACY.md', 'KNOWN_LIMITATIONS.md')) {
    Copy-Item -LiteralPath (Join-Path $packageStage $fileName) -Destination $releaseDirectory
}

$checksumTargets = Get-ChildItem -LiteralPath $releaseDirectory -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name
$checksumLines = $checksumTargets | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ascii
Remove-Item -LiteralPath $packageStage -Recurse -Force

Write-Host "Release candidate created: $releaseDirectory"
Write-Host "Archive: $zipPath"
