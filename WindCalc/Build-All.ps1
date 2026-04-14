# Build-All.ps1 — Release-build WindCalc for every supported Revit year
# whose REVIT_<year>_API_PATH env var is defined, and stage the outputs
# into WindCalc/dist/<year>/ for the installer to consume.

param(
    [string[]]$Years = @("2024","2025","2026","2027"),
    [string]$Config  = "Release"
)

$Msbuild    = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$ProjectDir = "$PSScriptRoot\WindCalc"
$DistRoot   = "$PSScriptRoot\dist"

if (Test-Path $DistRoot) { Remove-Item $DistRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null

$built = @()
foreach ($year in $Years) {
    $apiEnvVar = "REVIT_${year}_API_PATH"
    $apiPath   = [Environment]::GetEnvironmentVariable($apiEnvVar)
    if (-not $apiPath -or -not (Test-Path "$apiPath\RevitAPI.dll")) {
        Write-Host "[skip $year] $apiEnvVar not set or RevitAPI.dll missing." -ForegroundColor DarkYellow
        continue
    }

    Write-Host "=== Building Revit $year ===" -ForegroundColor Cyan
    & $Msbuild "$ProjectDir\WindCalc.csproj" /p:Configuration=$Config /p:Platform=AnyCPU "/p:RevitYear=$year" /t:Rebuild /nologo /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build FAILED for Revit $year." -ForegroundColor Red
        exit 1
    }

    $outputDir = "$ProjectDir\bin\$Config\$year"
    $stageDir  = "$DistRoot\$year"
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    Copy-Item "$outputDir\WindCalc.dll"        $stageDir -Force
    Copy-Item "$outputDir\Newtonsoft.Json.dll" $stageDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$ProjectDir\WindCalc.addin"     $stageDir -Force

    if (Test-Path "$outputDir\Resources") {
        Copy-Item "$outputDir\Resources" $stageDir -Recurse -Force
    }

    $built += $year
}

if ($built.Count -eq 0) {
    Write-Host "No Revit years were built. Set at least one REVIT_<year>_API_PATH env var." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Built: $($built -join ', ')" -ForegroundColor Green
Write-Host "Staged to: $DistRoot" -ForegroundColor Green
