# Deploy.ps1 — Build and install WindCalc plugin to the chosen Revit year's addins folder.
#
# Usage:  .\Deploy.ps1 -Year 2027 -Config Debug
param(
    [ValidateSet("2024","2025","2026","2027")]
    [string]$Year = "2025",
    [ValidateSet("Debug","Release")]
    [string]$Config = "Debug"
)

$Msbuild     = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$ProjectDir  = "$PSScriptRoot\WindCalc"
$OutputDir   = "$ProjectDir\bin\$Config\$Year"
$AddinTarget = "$env:APPDATA\Autodesk\Revit\Addins\$Year"

$apiEnvVar = "REVIT_${Year}_API_PATH"
$apiPath   = [Environment]::GetEnvironmentVariable($apiEnvVar)
if (-not $apiPath -or -not (Test-Path "$apiPath\RevitAPI.dll")) {
    Write-Host "Env var $apiEnvVar not set or RevitAPI.dll missing at '$apiPath'." -ForegroundColor Red
    Write-Host "Set it to the Revit $Year install folder (e.g. 'C:\Program Files\Autodesk\Revit $Year')." -ForegroundColor Yellow
    exit 1
}

Write-Host "Building WindCalc for Revit $Year ($Config)..." -ForegroundColor Cyan
& $Msbuild "$ProjectDir\WindCalc.csproj" /p:Configuration=$Config /p:Platform=AnyCPU "/p:RevitYear=$Year" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { Write-Host "Build FAILED." -ForegroundColor Red; exit 1 }

Write-Host "Copying to $AddinTarget ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $AddinTarget | Out-Null
Copy-Item "$OutputDir\WindCalc.dll"    $AddinTarget -Force
Copy-Item "$ProjectDir\WindCalc.addin" $AddinTarget -Force

$resSource = "$OutputDir\Resources"
$resDest   = "$AddinTarget\Resources"
if (Test-Path $resSource) {
    New-Item -ItemType Directory -Force -Path $resDest | Out-Null
    Copy-Item "$resSource\*" $resDest -Force
}

$dep = "$OutputDir\Newtonsoft.Json.dll"
if (Test-Path $dep) { Copy-Item $dep $AddinTarget -Force }

Write-Host "Done. Restart Revit $Year to load the updated plugin." -ForegroundColor Green
