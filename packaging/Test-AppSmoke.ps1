[CmdletBinding()]
param([string]$ReleaseDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\release\0.1.0'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$zips = @(Get-ChildItem -LiteralPath $release -Filter 'PaperBridge-*-win-x64.zip' -File)
if ($zips.Count -ne 1) { throw 'Exactly one release ZIP is required.' }
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('paperbridge-smoke-' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $smokeRoot 'isolated-data'
$process = $null
try {
    Expand-Archive -LiteralPath $zips[0].FullName -DestinationPath $smokeRoot
    $env:PAPERBRIDGE_DATA_DIR = $dataRoot
    if (-not $env:windir) { $env:windir = $env:SystemRoot }
    $appDirectory = Join-Path $smokeRoot 'app'
    $process = Start-Process -FilePath (Join-Path $appDirectory 'PaperBridge.App.exe') `
        -WorkingDirectory $appDirectory -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 12
    $process.Refresh()
    if ($process.HasExited) { throw "App exited early with code $($process.ExitCode)" }
    $database = Join-Path $dataRoot 'Data\paperbridge.db'
    if (-not (Test-Path -LiteralPath $database)) { throw 'The isolated SQLite database was not created.' }
    $schema = & python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print(c.execute('pragma user_version').fetchone()[0]); c.close()" $database
    if ($LASTEXITCODE -ne 0 -or $schema -ne '7') { throw "Unexpected SQLite schema version: $schema" }
    Write-Host "Self-contained startup passed. PID=$($process.Id); title='$($process.MainWindowTitle)'; schema=$schema."
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    Remove-Item Env:PAPERBRIDGE_DATA_DIR -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $smokeRoot) { Remove-Item -LiteralPath $smokeRoot -Recurse -Force }
}
