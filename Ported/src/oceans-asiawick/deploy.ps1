# Build AsiaWick and drop both DLLs where ATAS loads them.
# ATAS only reads those folders at startup, so restart the platform after running this.
#
# Order is deliberate: tests, then build, then smoke, then copy. Nothing reaches
# %APPDATA% unless the math and the constructors are both good.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$indicatorTarget = Join-Path $env:APPDATA 'ATAS\Indicators'
$strategyTarget = Join-Path $env:APPDATA 'ATAS\Strategies'

# --- 1. math harness -------------------------------------------------------
# Capture the whole stream before slicing it. Piping dotnet run straight into
# Select-Object -First cuts the pipeline short, kills the child and leaves
# $LASTEXITCODE reading as failure, so a passing suite looks broken.
Write-Host 'Running math harness...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_test')
try { $testOut = & dotnet run -c Release 2>&1 }
finally { Pop-Location }

if ($LASTEXITCODE -ne 0) {
    $testOut | Select-Object -Last 25
    throw 'Math harness failed - not deploying.'
}
$testOut | Select-Object -Last 1

# --- 2. build both assemblies ---------------------------------------------
Write-Host 'Building indicator...' -ForegroundColor Cyan
& dotnet build (Join-Path $root 'OceansAsiaWick.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Indicator build failed.' }

Write-Host 'Building strategy...' -ForegroundColor Cyan
& dotnet build (Join-Path $root '_strategy\OceansAsiaWickStrategy.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Strategy build failed.' }

# --- 3. smoke: construct both outside ATAS --------------------------------
Write-Host 'Smoke test...' -ForegroundColor Cyan
Push-Location (Join-Path $root '_smoke')
try { $smokeOut = & dotnet run -c Release 2>&1 }
finally { Pop-Location }

if ($LASTEXITCODE -ne 0) {
    $smokeOut | Select-Object -Last 25
    throw 'Smoke test failed - not deploying.'
}
$smokeOut | Select-Object -Last 1

# --- 4. copy ---------------------------------------------------------------
foreach ($dir in @($indicatorTarget, $strategyTarget)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

$pairs = @(
    @{ From = Join-Path $root 'bin\Release\OceansAsiaWick.dll'; To = Join-Path $indicatorTarget 'OceansAsiaWick.dll' },
    @{ From = Join-Path $root '_strategy\bin\Release\OceansAsiaWickStrategy.dll'; To = Join-Path $strategyTarget 'OceansAsiaWickStrategy.dll' }
)

foreach ($p in $pairs) {
    if (-not (Test-Path $p.From)) { throw "Built DLL missing: $($p.From)" }

    try {
        Copy-Item $p.From $p.To -Force
        Write-Host "  -> $($p.To)" -ForegroundColor Green
    }
    catch {
        throw "Could not write $($p.To) - ATAS has it loaded. Close ATAS and run this again.`n$_"
    }
}

Write-Host 'Deployed.' -ForegroundColor Green
Write-Host 'The strategy ships with "Signal only" ON. Leave it on until the backtest gate passes.' -ForegroundColor Yellow

if (Get-Process -Name 'OFT.Platform' -ErrorAction SilentlyContinue) {
    Write-Host 'ATAS is running - restart it to pick up this build.' -ForegroundColor Yellow
}
