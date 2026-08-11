[CmdletBinding()]
param([string]$ReleaseDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $repository 'artifacts\release\0.1.0'
}
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $release -PathType Container)) { throw "Release directory not found: $release" }

$secretPattern = '(sk-[A-Za-z0-9_-]{16,}|Bearer[ \t]+[A-Za-z0-9._~+/-]{16,})'
$sourceMatches = & rg -l -a -e $secretPattern `
    (Join-Path $repository 'src') `
    (Join-Path $repository 'packaging') `
    (Join-Path $repository 'docs') `
    (Join-Path $repository 'README.md') `
    (Join-Path $repository 'PRIVACY.md') `
    (Join-Path $repository 'SECURITY.md') `
    (Join-Path $repository 'SUPPORT.md') 2>$null
if ($LASTEXITCODE -notin @(0, 1)) { throw 'Source secret scan failed to execute.' }
if ($sourceMatches) { throw "Likely secret pattern found in source path(s): $($sourceMatches -join ', ')" }

$tracked = @(& git -C $repository ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked files.' }
$invalidDataFiles = @($tracked | Where-Object { $_ -match '(?i)\.(db|db-wal|db-shm|log)$' })
if ($invalidDataFiles) { throw "Tracked runtime data is not allowed: $($invalidDataFiles -join ', ')" }
$trackedPdfs = @($tracked | Where-Object { $_ -match '(?i)\.pdf$' })
$allowedPdf = 'output/pdf/pdfium-text-layer-sample.pdf'
$restrictedPdfs = @($trackedPdfs | Where-Object { $_.Replace('\', '/') -ne $allowedPdf })
if ($restrictedPdfs) { throw "Unexpected tracked PDF(s): $($restrictedPdfs -join ', ')" }

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('paperbridge-release-scan-' + [guid]::NewGuid().ToString('N'))
try {
    $zips = @(Get-ChildItem -LiteralPath $release -Filter 'PaperBridge-*-win-x64.zip' -File)
    if ($zips.Count -ne 1) { throw 'Exactly one release ZIP is required for safety scanning.' }
    $zip = $zips[0]
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $tempRoot
    $sentinelMatches = & rg -l -a -e 'unit-test-sensitive-value' $tempRoot 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { throw 'Release content scan failed to execute.' }
    if ($sentinelMatches) { throw "Sensitive test sentinel found in release file(s): $($sentinelMatches -join ', ')" }
    $secretCandidates = @(Get-ChildItem -LiteralPath $tempRoot -Recurse -File | Where-Object {
        $_.Name -like 'PaperBridge*' -or $_.Extension -in @('.ps1', '.md', '.json', '.txt', '.config', '.xml', '.yml', '.yaml')
    })
    foreach ($candidate in $secretCandidates) {
        $secretMatches = & rg -l -a -e $secretPattern $candidate.FullName 2>$null
        if ($LASTEXITCODE -notin @(0, 1)) { throw "Release secret scan failed: $($candidate.FullName)" }
        if ($secretMatches) { throw "Likely secret found in release file: $($candidate.FullName)" }
    }
    $releasePdfs = @(Get-ChildItem -LiteralPath $tempRoot -Recurse -File -Filter '*.pdf')
    if ($releasePdfs) { throw 'The release archive unexpectedly contains PDF documents.' }
    $releaseData = @(Get-ChildItem -LiteralPath $tempRoot -Recurse -File | Where-Object Extension -in @('.db', '.log'))
    if ($releaseData) { throw 'The release archive unexpectedly contains database or log files.' }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

$runtimeCandidates = @(Get-ChildItem -LiteralPath $repository -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike "$repository\.git\*" -and $_.FullName -notlike "$repository\artifacts\*" -and $_.Extension -in @('.db', '.log') })
foreach ($candidate in $runtimeCandidates) {
    $matches = & rg -l -a -e $secretPattern $candidate.FullName 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { throw "Runtime data scan failed: $($candidate.FullName)" }
    if ($matches) { throw "Likely secret found in runtime data file: $($candidate.FullName)" }
}

if (Test-Path -LiteralPath (Join-Path $repository '.git\refs\heads\main')) {
    $historyMatches = & git -C $repository grep -I -l -E $secretPattern HEAD -- ':!tests' 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { throw 'Git history secret scan failed.' }
    if ($historyMatches) { throw "Likely secret found in Git history path(s): $($historyMatches -join ', ')" }
}

Write-Host "Release safety scan passed. Runtime database/log files inspected: $($runtimeCandidates.Count); tracked PDFs: $($trackedPdfs.Count)."
