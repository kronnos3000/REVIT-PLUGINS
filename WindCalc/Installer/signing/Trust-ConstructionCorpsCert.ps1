# Trust-ConstructionCorpsCert.ps1
# Installs the Construction Corps code-signing cert into LocalMachine\Root so
# WindCalc installers signed with it are trusted on this PC for all users.
# Run once per machine as Administrator.

#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$cer = Join-Path $PSScriptRoot "ConstructionCorps.cer"
if (-not (Test-Path $cer)) {
    Write-Host "ConstructionCorps.cer not found next to this script." -ForegroundColor Red
    exit 1
}
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Write-Host "Construction Corps code-signing cert installed into LocalMachine\Root." -ForegroundColor Green
Write-Host "WindCalc installers signed with this cert will now run without SmartScreen 'unknown publisher' warnings on this machine." -ForegroundColor Green
