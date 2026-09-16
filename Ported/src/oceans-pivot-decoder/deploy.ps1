# Test, build, smoke, then drop the DLL where ATAS loads custom indicators.
# ATAS reads that folder only at startup, so restart the platform after running this.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $env:APPDATA 'ATAS\Indicators'

# Capture, check the exit code, THEN slice. Piping dotnet run straight into Select-Object cuts
# the pipeline short, kills the child and leaves $LASTEXITCODE reading as failure, so a passing
# test looks broken.
Write-Host 'Running math tests...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_test')
try { $testOutput = dotnet run -c Release }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    $testOutput | Select-Object -Last 40
    throw 'Math tests failed - not deploying.'
}
$testOutput | Select-Object -Last 1

Write-Host 'Building...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'OceansPivotDecoder.csproj') -c Release -p:SkipAtasDeploy=true
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host 'Constructing the indicator outside ATAS...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_smoke')
try { $smokeOutput = dotnet run -c Release }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    $smokeOutput | Select-Object -Last 40
    throw 'Smoke test failed - not deploying.'
}

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

$dll = Join-Path $root 'bin\Release\OceansPivotDecoder.dll'
try {
    Copy-Item $dll (Join-Path $target 'OceansPivotDecoder.dll') -Force
}
catch {
    throw "Could not write the DLL - ATAS has it loaded. Close ATAS and run this again.`n$_"
}

Write-Host "Deployed to $target" -ForegroundColor Green

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
