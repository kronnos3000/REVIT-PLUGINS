# Build-All.ps1 — Release-build RevitBridge for every supported Revit year
# (2025 and 2027 only) whose REVIT_<year>_API_PATH env var is defined,
# and stage the outputs into RevitBridge/dist/<year>/ for the installer.
#
# Layout per year:
#   dist\<year>\RevitBridge.addin          (manifest; points into the subfolder)
#   dist\<year>\RevitBridge\*.dll          (RevitBridge + Newtonsoft + Microsoft.CodeAnalysis*)
#   dist\<year>\RevitBridge\Resources\*.png

param(
    [string[]]$Years = @("2025","2027"),
    [string]$Config  = "Release"
)

$Msbuild    = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$ProjectDir = "$PSScriptRoot\RevitBridge"
$DistRoot   = "$PSScriptRoot\dist"

if (Test-Path $DistRoot) { Remove-Item $DistRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null

$built = @()
foreach ($year in $Years) {
    $apiEnvVar = "REVIT_${year}_API_PATH"
    $apiPath   = [Environment]::GetEnvironmentVariable($apiEnvVar)
    if (-not $apiPath) { $apiPath = [Environment]::GetEnvironmentVariable($apiEnvVar, "User") }
    if (-not $apiPath -or -not (Test-Path "$apiPath\RevitAPI.dll")) {
        Write-Host "[skip $year] $apiEnvVar not set or RevitAPI.dll missing." -ForegroundColor DarkYellow
        continue
    }
    Set-Item -Path "Env:$apiEnvVar" -Value $apiPath

    Write-Host "=== Building Revit $year ===" -ForegroundColor Cyan
    & $Msbuild "$ProjectDir\RevitBridge.csproj" /restore /p:Configuration=$Config /p:Platform=AnyCPU "/p:RevitYear=$year" /t:Rebuild /nologo /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build FAILED for Revit $year." -ForegroundColor Red
        exit 1
    }

    $outputDir  = "$ProjectDir\bin\$Config\$year"
    $stageDir   = "$DistRoot\$year"
    $payloadDir = "$stageDir\RevitBridge"
    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

    Copy-Item "$outputDir\*.dll" $payloadDir -Force
    Copy-Item "$outputDir\RevitBridge.deps.json" $payloadDir -Force -ErrorAction SilentlyContinue
    if (Test-Path "$outputDir\Resources") {
        Copy-Item "$outputDir\Resources" $payloadDir -Recurse -Force
    }
    (Get-Content "$ProjectDir\RevitBridge.addin" -Raw) -replace '<Assembly>RevitBridge.dll</Assembly>', '<Assembly>RevitBridge\RevitBridge.dll</Assembly>' |
        Set-Content "$stageDir\RevitBridge.addin" -Encoding UTF8 -NoNewline

    $built += $year
}

if ($built.Count -eq 0) {
    Write-Host "No Revit years were built. Set REVIT_2025_API_PATH and/or REVIT_2027_API_PATH." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Built: $($built -join ', ')" -ForegroundColor Green
Write-Host "Staged to: $DistRoot" -ForegroundColor Green
