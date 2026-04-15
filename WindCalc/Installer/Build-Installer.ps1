# Build-Installer.ps1 — Stage per-year builds and compile the Inno Setup installer.
#
# Requires Inno Setup 6 installed; iscc.exe is located at
# "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" by default.

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Iss       = Join-Path $PSScriptRoot "WindCalc.iss"
$DistRoot  = Join-Path $RepoRoot "dist"
$BuildAll  = Join-Path $RepoRoot "Build-All.ps1"

$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Path }
}
if (-not $iscc -or -not (Test-Path $iscc)) {
    Write-Host "Inno Setup (iscc.exe) not found. Install it from https://jrsoftware.org/isdl.php" -ForegroundColor Red
    exit 1
}

# If no version was given, read it from the csproj so the installer name matches.
if (-not $Version) {
    $csproj = Join-Path $RepoRoot "WindCalc\WindCalc.csproj"
    $xml = [xml](Get-Content $csproj)
    $Version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = "1.0.0" }
}

Write-Host "Staging per-year builds..." -ForegroundColor Cyan
& $BuildAll
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "Compiling installer v$Version ..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" $Iss
if ($LASTEXITCODE -ne 0) { Write-Host "Installer compile FAILED." -ForegroundColor Red; exit 1 }

$setupExe = Join-Path $DistRoot "installer\WindCalc-Setup-$Version.exe"
Write-Host "Installer built: $setupExe" -ForegroundColor Green
