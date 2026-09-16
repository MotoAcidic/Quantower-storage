# Build Ocean Delta Cross and drop it where ATAS loads custom indicators.
# ATAS only reads that folder at startup, so restart the platform after running this.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:APPDATA 'ATAS\Indicators'

# Captured whole rather than piped: cutting a pipeline short with Select-Object stops the child
# process and leaves $LASTEXITCODE reading like a failure. Failures print every line, because the
# name of the check that broke is the whole point of running them.
Write-Host 'Running delta tests...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_test')
try { $tests = dotnet run -c Release }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    $tests | Write-Host
    throw 'Delta tests failed - not deploying.'
}
$tests | Select-Object -Last 1 | Write-Host

Write-Host 'Building...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'OceansDelta.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# A constructor that throws is invisible inside ATAS, so it is caught out here instead.
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

$dll = Join-Path $root 'bin\Release\OceansDelta.dll'
try {
    Copy-Item $dll (Join-Path $target 'OceansDelta.dll') -Force
}
catch {
    throw "Could not write the DLL - ATAS has it loaded. Close ATAS and run this again.`n$_"
}

Write-Host "Deployed to $target" -ForegroundColor Green

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
