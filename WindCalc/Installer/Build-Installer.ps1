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

# ── Code signing ──────────────────────────────────────────────────────────
# Sign with the Construction Corps self-signed cert if the PFX is present.
# Target machines must have ConstructionCorps.cer imported into LocalMachine\Root
# (see WindCalc\Installer\signing\Trust-ConstructionCorpsCert.ps1).
$pfx = Join-Path $PSScriptRoot "signing\ConstructionCorps.pfx"
if (Test-Path $pfx) {
    $signtoolCandidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22000.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe"
    )
    $signtool = $signtoolCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $signtool) {
        $signtool = (Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                     Where-Object { $_.FullName -like '*x64*' } | Select-Object -First 1 -ExpandProperty FullName)
    }
    if ($signtool) {
        Write-Host "Signing $setupExe ..." -ForegroundColor Cyan
        & $signtool sign /f $pfx /p "changeme" /fd SHA256 /tr "http://timestamp.digicert.com" /td SHA256 /d "WindCalc" $setupExe
        if ($LASTEXITCODE -ne 0) { Write-Host "signtool FAILED." -ForegroundColor Red; exit 1 }
    } else {
        Write-Host "signtool.exe not found; installer will be unsigned. Install the Windows 10/11 SDK to enable signing." -ForegroundColor Yellow
    }
} else {
    Write-Host "No signing\\ConstructionCorps.pfx present; installer will be unsigned." -ForegroundColor Yellow
}

Write-Host "Installer built: $setupExe" -ForegroundColor Green
