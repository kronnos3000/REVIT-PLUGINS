# Deploy.ps1 — Build and install RevitBridge to the chosen Revit year's addins folder.
#
# Usage:  .\Deploy.ps1 -Year 2027 -Config Debug
#
# RevitBridge targets net8.0-windows (Revit 2025) / net10.0-windows (Revit 2027), and ships
# Roslyn (Microsoft.CodeAnalysis*.dll) for execute_csharp. Everything goes into a
# RevitBridge\ subfolder next to the .addin so its dependencies don't collide with other add-ins.
param(
    [ValidateSet("2025","2027")]
    [string]$Year = "2025",
    [ValidateSet("Debug","Release")]
    [string]$Config = "Debug",
    # By default the add-in is pre-approved in Revit's "Always Load" list so the unsigned-add-in
    # prompt doesn't block startup (same effect as clicking "Always Load" once). -NoTrust skips that.
    [switch]$NoTrust
)

$Msbuild     = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$ProjectDir  = "$PSScriptRoot\RevitBridge"
$OutputDir   = "$ProjectDir\bin\$Config\$Year"
$AddinTarget = "$env:APPDATA\Autodesk\Revit\Addins\$Year"
$PayloadDir  = "$AddinTarget\RevitBridge"

$apiEnvVar = "REVIT_${Year}_API_PATH"
$apiPath   = [Environment]::GetEnvironmentVariable($apiEnvVar)
if (-not $apiPath) { $apiPath = [Environment]::GetEnvironmentVariable($apiEnvVar, "User") }
if (-not $apiPath -or -not (Test-Path "$apiPath\RevitAPI.dll")) {
    Write-Host "Env var $apiEnvVar not set or RevitAPI.dll missing at '$apiPath'." -ForegroundColor Red
    Write-Host "Set it to the Revit $Year install folder (e.g. 'C:\Program Files\Autodesk\Revit $Year')." -ForegroundColor Yellow
    exit 1
}
Set-Item -Path "Env:$apiEnvVar" -Value $apiPath

Write-Host "Building RevitBridge for Revit $Year ($Config)..." -ForegroundColor Cyan
& $Msbuild "$ProjectDir\RevitBridge.csproj" /restore /p:Configuration=$Config /p:Platform=AnyCPU "/p:RevitYear=$Year" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { Write-Host "Build FAILED." -ForegroundColor Red; exit 1 }

$running = Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*Revit $Year*" }
Write-Host "Copying to $PayloadDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null
# Revit 2027 hot-loads new add-ins, so a running session may hold the DLLs open.
# Loaded DLLs can't be overwritten but can be renamed: move locked ones aside, then copy.
Get-ChildItem $PayloadDir -Filter "*.old-*" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$stamp = Get-Date -Format "yyyyMMddHHmmss"
Get-ChildItem (Join-Path $PayloadDir "*") -File -Include *.dll, *.pdb -ErrorAction SilentlyContinue | ForEach-Object {
    $file = $_
    try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
    catch { Rename-Item $file.FullName "$($file.Name).old-$stamp" -ErrorAction SilentlyContinue }
}
try {
    Copy-Item "$OutputDir\*.dll" $PayloadDir -Force -ErrorAction Stop
    if ($Config -eq "Debug") { Copy-Item "$OutputDir\RevitBridge.pdb" $PayloadDir -Force -ErrorAction SilentlyContinue }
    Copy-Item "$OutputDir\RevitBridge.deps.json" $PayloadDir -Force -ErrorAction SilentlyContinue
    if (Test-Path "$OutputDir\Resources") {
        New-Item -ItemType Directory -Force -Path "$PayloadDir\Resources" | Out-Null
        Copy-Item "$OutputDir\Resources\*" "$PayloadDir\Resources" -Force
    }
} catch {
    Write-Host "Copy failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($running) { Write-Host "A Revit $Year session (pid $($running.Id -join ', ')) has RevitBridge loaded; close it and re-run." -ForegroundColor Yellow }
    exit 1
}

# The manifest points into the payload subfolder.
# Only rewrite it when it changed: Revit 2027 hot-loads touched .addin files.
$manifest = (Get-Content "$ProjectDir\RevitBridge.addin" -Raw) -replace '<Assembly>RevitBridge.dll</Assembly>', '<Assembly>RevitBridge\RevitBridge.dll</Assembly>'
$addinPath = "$AddinTarget\RevitBridge.addin"
if (-not (Test-Path $addinPath) -or (Get-Content $addinPath -Raw) -ne $manifest) {
    Set-Content $addinPath $manifest -Encoding UTF8 -NoNewline
}

if (-not $NoTrust) {
    $key = "HKCU:\SOFTWARE\Autodesk\Revit\Autodesk Revit $Year\CodeSigning"
    # NB: never `New-Item -Force` an existing registry key — that recreates it and wipes the
    # other add-ins' "Always Load" approvals. Create it only if missing; add values in place.
    if (-not (Test-Path $key)) { New-Item -Path $key | Out-Null }
    ([xml](Get-Content "$ProjectDir\RevitBridge.addin")).RevitAddIns.AddIn | ForEach-Object {
        New-ItemProperty -Path $key -Name $_.ClientId.ToLower() -Value 1 -PropertyType DWord -Force | Out-Null
    }
}

Write-Host "Done. Restart Revit $Year to load the updated plugin." -ForegroundColor Green
