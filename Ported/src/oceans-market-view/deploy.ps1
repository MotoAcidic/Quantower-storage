# Build Ocean Market View and drop it where ATAS loads custom indicators.
# ATAS only reads the folder at startup, so restart the platform after running this.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:APPDATA 'ATAS\Indicators'

Write-Host 'Running model tests...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_test')
try { dotnet run -c Release | Select-Object -Last 1 }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Model tests failed - not deploying.' }

Write-Host 'Building...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'OceansMarketView.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

$dll = Join-Path $root 'bin\Release\OceansMarketView.dll'
try {
    Copy-Item $dll (Join-Path $target 'OceansMarketView.dll') -Force
}
catch {
    throw "Could not write the DLL - ATAS has it loaded. Close ATAS and run this again.`n$_"
}

Write-Host "Deployed to $target" -ForegroundColor Green

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
