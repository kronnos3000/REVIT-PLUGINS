# WindCalc Code Signing

Self-signed code-signing setup for WindCalc installers. The signing key (`*.pfx`) is kept out of git; only the public cert (`ConstructionCorps.cer`) and the trust helper are committed.

## On target machines (one-time per PC)

Right-click `Trust-ConstructionCorpsCert.ps1` → **Run with PowerShell** as Administrator. That imports `ConstructionCorps.cer` into `LocalMachine\Root`, making every user on that PC trust installers signed with this cert — no more SmartScreen "unknown publisher" prompt.

Alternatively, from an elevated PowerShell:

```powershell
Import-Certificate -FilePath .\ConstructionCorps.cer -CertStoreLocation Cert:\LocalMachine\Root
```

## On a new build machine

If you need to rebuild on a PC that doesn't have the PFX, either:

- Copy `ConstructionCorps.pfx` from your existing build machine over a secure channel, **or**
- Regenerate a fresh cert (this invalidates trust on all target PCs — they'll need the new `.cer` re-imported):

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=Construction Corps' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 `
    -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)

Export-PfxCertificate -Cert $cert -FilePath .\ConstructionCorps.pfx `
    -Password (ConvertTo-SecureString -String 'changeme' -AsPlainText -Force)

Export-Certificate -Cert $cert -FilePath .\ConstructionCorps.cer
```

The PFX password is `changeme` — change it if you like, but you must update `Build-Installer.ps1` to match.

## How signing happens

`Build-Installer.ps1` runs `signtool.exe sign /f ConstructionCorps.pfx …` on the compiled `WindCalc-Setup-<ver>.exe` after Inno Setup finishes. The timestamp URL `http://timestamp.digicert.com` is used so signatures remain valid after the cert expires.
