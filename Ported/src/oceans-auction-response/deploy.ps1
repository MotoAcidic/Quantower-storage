# Build Auction Response Monitor and drop it where ATAS loads custom indicators.
# ATAS only reads that folder at startup, so restart the platform after running this.
#
# Nothing is copied unless the test harness AND the smoke test both pass: a constructor that
# throws is invisible inside ATAS, and a silent math regression is worse than a failed build.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:APPDATA 'ATAS\Indicators'

Write-Host 'Running the test harness...' -ForegroundColor Cyan
Push-Location (Join-Path $root 'tests\AuctionResponse.Tests')
try { $tests = dotnet run -c Release }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    $tests | Write-Host
    throw 'Tests failed - not deploying.'
}
$tests | Select-Object -Last 1 | Write-Host

Write-Host 'Building the indicator...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'src\AuctionResponse.ATAS\AuctionResponse.ATAS.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# The output is captured whole rather than piped: cutting a pipeline short with Select-Object
# stops the child process and leaves $LASTEXITCODE reading like a failure.
Write-Host 'Smoke test...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_smoke')
try { $smoke = dotnet run -c Release }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    $smoke | Write-Host
    throw 'Indicator would not construct - not deploying.'
}
$smoke | Select-Object -First 1 | Write-Host

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

# Core, Replay and Research travel with the indicator; the ATAS assemblies never do.
$binDir = Join-Path $root 'src\AuctionResponse.ATAS\bin\Release'
$payload = @(
    'AuctionResponseMonitor.dll',
    'AuctionResponse.Core.dll',
    'AuctionResponse.Ui.dll',
    'AuctionResponse.Replay.dll',
    'AuctionResponse.Research.dll'
)

# Back up whatever is already installed, so a rollback is a file copy and not a rebuild.
$backup = Join-Path $target ('_backup_AuctionResponse_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$existing = $payload | Where-Object { Test-Path (Join-Path $target $_) }
if ($existing) {
    New-Item -ItemType Directory -Path $backup | Out-Null
    foreach ($f in $existing) { Copy-Item (Join-Path $target $f) (Join-Path $backup $f) -Force }
    Write-Host "Previous build backed up to $backup" -ForegroundColor DarkGray
}

foreach ($f in $payload) {
    $src = Join-Path $binDir $f
    if (-not (Test-Path $src)) { throw "Expected build output is missing: $src" }
    try {
        Copy-Item $src (Join-Path $target $f) -Force
    }
    catch {
        throw "Could not write $f - ATAS has it loaded. Close ATAS and run this again.`n$_"
    }
}

Write-Host "Deployed to $target" -ForegroundColor Green
Write-Host 'Reminder: the indicator is READ-ONLY and has no calibrated edge.' -ForegroundColor DarkGray
Write-Host 'Draw horizontal lines where you want levels watched - it picks them up.' -ForegroundColor DarkGray
Write-Host 'It observes its own reference over the first ~10 sessions and arms nothing before then.' -ForegroundColor DarkGray

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
