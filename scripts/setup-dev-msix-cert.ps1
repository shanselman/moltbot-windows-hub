<#
.SYNOPSIS
    Provisions or removes the local development MSIX signing certificate.

.DESCRIPTION
    Creates a non-exportable current-user code-signing certificate whose
    subject matches the generated development manifest, trusts its public
    certificate for local package installation, and stores only its thumbprint
    under %LOCALAPPDATA%\OpenClawDevelopment\MSIX.

    The certificate is development-only. Microsoft Store submissions use the
    Partner Center identity and signing process instead.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Remove,
    [switch]$SkipTrust
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj"
$certificateDirectory = Join-Path $env:LOCALAPPDATA "OpenClawDevelopment\MSIX"
$thumbprintPath = Join-Path $certificateDirectory "dev-msix-thumbprint.txt"
$legacyPfxPath = Join-Path $env:LOCALAPPDATA "OpenClawTray\dev-msix.pfx"
$friendlyName = "OpenClaw Development MSIX Signing"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"

[xml]$project = Get-Content -LiteralPath $projectPath
$publisherNode = $project.SelectSingleNode("/Project/PropertyGroup/OpenClawDevMsixPublisher")
$publisher = if ($null -ne $publisherNode) { $publisherNode.InnerText } else { $null }
if ([string]::IsNullOrWhiteSpace($publisher)) {
    throw "OpenClawDevMsixPublisher is missing from $projectPath"
}

function Assert-CanModifyMachineTrust {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell to modify LocalMachine\TrustedPeople, or pass -SkipTrust."
    }
}

function Remove-CertificateAndTrust($certificate) {
    if (-not $SkipTrust) {
        Assert-CanModifyMachineTrust
        $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
            Where-Object Thumbprint -eq $certificate.Thumbprint
        foreach ($trustedCertificate in $trusted) {
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$($trustedCertificate.Thumbprint)" -Force
        }
    }

    Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force
}

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.FriendlyName -eq $friendlyName -and
        $_.HasPrivateKey -and
        ($_.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId }) -contains $codeSigningOid
    }

$legacyThumbprint = $null
if (Test-Path -LiteralPath $legacyPfxPath) {
    try {
        $legacyCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $legacyPfxPath,
            "openclaw-dev")
        $legacyThumbprint = $legacyCertificate.Thumbprint
        $legacyCertificate.Dispose()
    } catch {
        Write-Warning "Could not inspect the legacy development PFX: $($_.Exception.Message)"
    }
}

if ($Remove) {
    foreach ($certificate in $existing) {
        Remove-CertificateAndTrust $certificate
    }
    if ($legacyThumbprint) {
        Remove-Item "Cert:\CurrentUser\My\$legacyThumbprint" -Force -ErrorAction SilentlyContinue
        if (-not $SkipTrust) {
            Assert-CanModifyMachineTrust
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$legacyThumbprint" -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item -LiteralPath $thumbprintPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $legacyPfxPath -Force -ErrorAction SilentlyContinue
    Write-Host "Development MSIX certificate and local trust removed."
    exit 0
}

if ($Force) {
    foreach ($certificate in $existing) {
        Remove-CertificateAndTrust $certificate
    }
    if ($legacyThumbprint) {
        Remove-Item "Cert:\CurrentUser\My\$legacyThumbprint" -Force -ErrorAction SilentlyContinue
        if (-not $SkipTrust) {
            Assert-CanModifyMachineTrust
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$legacyThumbprint" -Force -ErrorAction SilentlyContinue
        }
    }
    $existing = @()
}

$certificate = $existing |
    Where-Object {
        $_.Subject -eq $publisher -and
        $_.NotAfter -gt (Get-Date)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $publisher `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -KeyExportPolicy NonExportable `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(3) `
        -FriendlyName $friendlyName `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}$codeSigningOid", "2.5.29.19={text}")
}

if (-not $SkipTrust) {
    Assert-CanModifyMachineTrust
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $certificate.Thumbprint
    if (-not $trusted) {
        $cerPath = Join-Path $env:TEMP "openclaw-dev-msix-$($certificate.Thumbprint).cer"
        try {
            Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
            Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
        } finally {
            Remove-Item -LiteralPath $cerPath -Force -ErrorAction SilentlyContinue
        }
    }
}

New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null
Set-Content -LiteralPath $thumbprintPath -Value $certificate.Thumbprint -Encoding ASCII
Remove-Item -LiteralPath $legacyPfxPath -Force -ErrorAction SilentlyContinue

Write-Host "Development MSIX certificate ready."
Write-Host "Subject:    $($certificate.Subject)"
Write-Host "Thumbprint: $($certificate.Thumbprint)"
Write-Host "Reference:  $thumbprintPath"
Write-Host ""
Write-Host "Build with:"
Write-Host "  .\build.ps1 -Project WinUI -Msix Dev"
