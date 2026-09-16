# Build Ocean Developed Profile and drop it where ATAS loads custom indicators.
# ATAS only reads that folder at startup, so restart the platform after running this.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:APPDATA 'ATAS\Indicators'

Write-Host 'Running the math tests...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_test')
try { $tests = dotnet run -c Release }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    $tests | Write-Host
    throw 'Tests failed - not deploying.'
}
$tests | Select-Object -Last 1 | Write-Host

Write-Host 'Building...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'OceansDeveloped.csproj') -c Release
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

$dll = Join-Path $root 'bin\Release\OceansDeveloped.dll'
try {
    Copy-Item $dll (Join-Path $target 'OceansDeveloped.dll') -Force
}
catch {
    throw "Could not write the DLL - ATAS has it loaded. Close ATAS and run this again.`n$_"
}

Write-Host "Deployed to $target" -ForegroundColor Green

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
