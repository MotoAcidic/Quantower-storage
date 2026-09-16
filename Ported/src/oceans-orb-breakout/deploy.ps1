# Build Ocean ORB Breakout and drop it where ATAS loads custom indicators.
# ATAS only reads the folder at startup, so restart the platform after running this.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:APPDATA 'ATAS\Indicators'

Write-Host 'Running model tests...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_test')
try {
    # Capture, THEN slice. Piping dotnet run straight into Select-Object cuts the pipeline
    # short and leaves $LASTEXITCODE reading as failure, so a passing suite looks broken.
    $testOutput = dotnet run -c Release
    $testExit = $LASTEXITCODE
}
finally { Pop-Location }

if ($testExit -ne 0) {
    $testOutput | Where-Object { $_ -match 'FAIL' } | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw 'Model tests failed - not deploying.'
}
$testOutput | Select-Object -Last 1

Write-Host 'Building...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'OceansOrbBreakout.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

$dll = Join-Path $root 'bin\Release\OceansOrbBreakout.dll'
try {
    Copy-Item $dll (Join-Path $target 'OceansOrbBreakout.dll') -Force
}
catch {
    throw "Could not write the DLL - ATAS has it loaded. Close ATAS and run this again.`n$_"
}

Write-Host "Deployed to $target" -ForegroundColor Green

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
