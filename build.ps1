<#
.SYNOPSIS
    Build script for OpenClaw Windows Hub

.DESCRIPTION
    Builds all projects, checks prerequisites, and provides clear guidance.

.PARAMETER Project
    Which project to build: All, Tray, WinUI, Shared, Cli, WinNodeCli, SetupEngine
    Default: All

.PARAMETER Configuration
    Build configuration: Debug, Release
    Default: Debug

.PARAMETER CheckOnly
    Only check prerequisites, don't build

.PARAMETER DevBuild
    Build the WinUI app with the side-by-side dev identity. Defaults off so
    release identity remains the default for every configuration.

.PARAMETER Msix
    Produce an MSIX package. The two modes are mutually exclusive because they
    build different applications, not two flavors of one:

      Dev   - locally signed, self-contained package using the side-by-side dev
              identity. Implies -DevBuild and uses the certificate created by
              scripts\setup-dev-msix-cert.ps1. The manifest revision is the
              installed development package revision plus one, so repeated
              Add-AppxPackage sideloads upgrade cleanly.

      Store - unsigned, self-contained packages for x64 and ARM64 using the
              release identity. Partner Center signs them, so no local
              certificate is used, and the manifest revision is pinned to 0
              because the Store reserves that field. Forces -Configuration
              Release and cannot be combined with -DevBuild.

.PARAMETER NoTrustRepository
    Do not automatically add this checkout to git safe.directory when GitVersion
    cannot read a repo owned by a different Windows account/group. The script
    will print the manual command instead.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Project WinUI -Configuration Release
    .\build.ps1 -CheckOnly
    .\build.ps1 -Project WinUI -Msix Dev
    .\build.ps1 -Project WinUI -Msix Store
#>

# CmdletBinding makes unrecognized parameters a hard error. Without it a removed
# or misspelled switch such as -PackageMsix lands in $args and is silently
# ignored, producing an unpackaged build with no indication anything was wrong.
[CmdletBinding()]
param(
    [ValidateSet("All", "Tray", "WinUI", "Shared", "Cli", "WinNodeCli", "SetupEngine")]
    [string]$Project = "All",
    
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    
    [switch]$CheckOnly,

    [switch]$DevBuild,

    [ValidateSet("Dev", "Store")]
    [string]$Msix,

    [switch]$NoTrustRepository
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$buildDevMsix = ($Msix -eq "Dev")
$buildStoreMsix = ($Msix -eq "Store")

if ($buildDevMsix) {
    $DevBuild = $true
}

# Microsoft Store packages ship the release identity and are signed by Partner
# Center, so they are incompatible with the dev identity and the local dev cert.
$storeMsixRuntimeIdentifiers = @("win-x64", "win-arm64")
if ($buildStoreMsix) {
    if ($DevBuild) {
        throw "-Msix Store cannot be combined with -DevBuild. Store packages must use the release identity."
    }
    if ($PSBoundParameters.ContainsKey("Configuration") -and $Configuration -ne "Release") {
        throw "-Msix Store requires -Configuration Release. Debug binaries are not accepted by Store certification."
    }
    $Configuration = "Release"
}

# Colors for output
function Write-Header($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Success($text) { Write-Host "✅ $text" -ForegroundColor Green }
function Write-Warning($text) { Write-Host "⚠️  $text" -ForegroundColor Yellow }
function Write-Error($text) { Write-Host "❌ $text" -ForegroundColor Red }
function Write-Info($text) { Write-Host "   $text" -ForegroundColor Gray }

# Track issues
$issues = @()

function Test-WindowsHost {
    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($isWindowsVariable) {
        return [bool]$isWindowsVariable.Value
    }

    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function ConvertTo-GitSafeDirectoryPath($path) {
    return ([System.IO.Path]::GetFullPath($path).TrimEnd("\") -replace "\\", "/")
}

function Test-GitSafeDirectoryContains($path) {
    $expected = (ConvertTo-GitSafeDirectoryPath $path).ToLowerInvariant()
    $safeDirectories = & git config --global --get-all safe.directory 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $safeDirectories) {
        return $false
    }

    foreach ($safeDirectory in $safeDirectories) {
        if ($safeDirectory -eq "*") {
            return $true
        }

        $normalized = ($safeDirectory.Trim().TrimEnd("\", "/") -replace "\\", "/").ToLowerInvariant()
        if ($normalized -eq $expected) {
            return $true
        }
    }

    return $false
}

function Get-SecurityIdentifierValue($accountName) {
    try {
        return ([System.Security.Principal.NTAccount]$accountName).
            Translate([System.Security.Principal.SecurityIdentifier]).
            Value
    } catch {
        return $null
    }
}

function Ensure-GitVersionRepositoryTrust {
    if (-not (Test-Path (Join-Path $repoRoot ".git"))) {
        return
    }

    $owner = (Get-Acl -LiteralPath $repoRoot).Owner
    $currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $ownerSid = Get-SecurityIdentifierValue $owner
    if ($ownerSid -and $ownerSid -eq $currentIdentity.User.Value) {
        return
    }

    if (Test-GitSafeDirectoryContains $repoRoot) {
        return
    }

    $safeDirectory = ConvertTo-GitSafeDirectoryPath $repoRoot
    Write-Warning "Repository owner is '$owner' but the current user is '$($currentIdentity.Name)'. GitVersion/LibGit2Sharp may reject this checkout unless it is trusted."

    if ($NoTrustRepository -or $CheckOnly) {
        Write-Info "Run this once, then retry:"
        Write-Info "git config --global --add safe.directory `"$safeDirectory`""
        $script:issues += "Repository is not trusted for GitVersion"
        return
    }

    Write-Info "Adding git safe.directory entry: $safeDirectory"

    & git config --global --add safe.directory $safeDirectory
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Unable to add git safe.directory entry."
        Write-Info "Run this once, then retry the build:"
        Write-Info "git config --global --add safe.directory `"$safeDirectory`""
        $script:issues += "Repository is not trusted for GitVersion"
        return
    }

    Write-Success "Repository trusted for GitVersion"
}

function Ensure-GitVersionRepositoryHistory {
    $insideWorkTree = & git -C $repoRoot rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne "true") {
        Write-Error "Git metadata not found. GitVersion requires a git clone with full history."
        Write-Info "Clone the repository with git, then rerun .\build.ps1."
        $script:issues += "Repository is missing git metadata required by GitVersion"
        return
    }

    $isShallow = & git -C $repoRoot rev-parse --is-shallow-repository 2>$null
    if ($LASTEXITCODE -eq 0 -and $isShallow -eq "true") {
        Write-Error "Repository is a shallow clone. GitVersion requires full git history."
        Write-Info "Run this once, then retry the build:"
        Write-Info "git fetch --unshallow --tags origin"
        $script:issues += "Repository is shallow; GitVersion requires full history"
    }
}

Write-Host @"

  🦞 OpenClaw Windows Hub - Build Script
  =======================================

"@ -ForegroundColor Magenta

# =============================================================================
# PREREQUISITE CHECKS
# =============================================================================

Write-Header "Checking Prerequisites"

# Check OS
if (-not (Test-WindowsHost)) {
    Write-Error "This project requires Windows"
    exit 1
}
Write-Success "Windows detected"

# Check .NET SDK
$dotnetVersion = $null
try {
    $dotnetVersion = & dotnet --version 2>$null
} catch {}

if (-not $dotnetVersion) {
    Write-Error ".NET SDK not found"
    Write-Info "Download from: https://dotnet.microsoft.com/download"
    $issues += "Missing .NET SDK"
} else {
    Write-Success ".NET SDK: $dotnetVersion"
    
    # Check for .NET 10 (needed for all projects)
    $sdks = & dotnet --list-sdks 2>$null
    $hasNet10 = $sdks | Where-Object { $_ -match "^10\." }
    
    if (-not $hasNet10) {
        Write-Error ".NET 10 SDK not found (required for all projects)"
        Write-Info "Download preview from: https://dotnet.microsoft.com/download/dotnet/10.0"
        $issues += "Missing .NET 10 SDK"
    } else {
        Write-Success ".NET 10 SDK available"
    }
}

# Check Git (GitVersion reads repository metadata during .NET builds)
$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Error "Git not found (required by GitVersion during builds)"
    Write-Info "Install via: winget install Git.Git"
    Write-Info "Or download from: https://git-scm.com/download/win"
    $issues += "Missing Git"
} else {
    $gitVersion = & git --version 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitVersion) {
        Write-Success "$gitVersion"
    } else {
        Write-Success "Git detected"
    }

    Ensure-GitVersionRepositoryTrust
    Ensure-GitVersionRepositoryHistory
}

# Check Node.js + npm (WinUI build runs `npm ci` to restore @microsoft/mxc-sdk
# so it can copy wxc-exec.exe into the build output)
$nodeVersion = $null
try { $nodeVersion = & node --version 2>$null } catch {}
if (-not $nodeVersion) {
    Write-Error "Node.js not found (required by WinUI build to restore @microsoft/mxc-sdk)"
    Write-Info "Install via: winget install OpenJS.NodeJS.LTS"
    Write-Info "Or download from: https://nodejs.org/"
    $issues += "Missing Node.js"
} else {
    Write-Success "Node.js: $nodeVersion"

    $npmVersion = $null
    try { $npmVersion = & npm --version 2>$null } catch {}
    if (-not $npmVersion) {
        Write-Error "npm not found on PATH (WinUI build invokes `npm ci`)"
        Write-Info "npm normally ships with Node.js - reinstall Node.js or repair the install"
        $issues += "Missing npm"
    } else {
        Write-Success "npm: $npmVersion"
    }
}

# Check Windows SDK (for WinUI)
$windowsSdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
if (Test-Path $windowsSdkPath) {
    $sdkVersions = @(
        Get-ChildItem $windowsSdkPath -Directory |
            Where-Object { $_.Name -match "^\d+\.\d+\.\d+\.\d+$" } |
            Sort-Object { [version]$_.Name } -Descending |
            Select-Object -ExpandProperty Name
    )

    if ($sdkVersions.Count -gt 0) {
        Write-Success "Windows SDK: $($sdkVersions[0])"
    } else {
        Write-Warning "Windows 10 SDK not found (needed for WinUI build)"
        Write-Info "Install via Visual Studio Installer, standalone SDK, or: winget install --id Microsoft.WindowsSDK.10.0.26100 -e"
        $issues += "Windows 10 SDK not detected"
    }
} else {
    Write-Warning "Windows 10 SDK not found (needed for WinUI build)"
    Write-Info "Install via Visual Studio Installer, standalone SDK, or: winget install --id Microsoft.WindowsSDK.10.0.26100 -e"
    $issues += "Windows 10 SDK not detected"
}

# Check WebView2 Runtime (for WinUI chat window)
$webView2Key = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
$webView2KeyAlt = "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
$webView2Version = $null

if (Test-Path $webView2Key) {
    $webView2Version = (Get-ItemProperty $webView2Key -ErrorAction SilentlyContinue).pv
} elseif (Test-Path $webView2KeyAlt) {
    $webView2Version = (Get-ItemProperty $webView2KeyAlt -ErrorAction SilentlyContinue).pv
}

if ($webView2Version) {
    Write-Success "WebView2 Runtime: $webView2Version"
} else {
    Write-Warning "WebView2 Runtime not detected (needed for WinUI chat window)"
    Write-Info "Usually pre-installed on Windows 10/11. Get from: https://developer.microsoft.com/microsoft-edge/webview2"
    # Not a hard failure - app will fall back to browser
}

# Check architecture
$arch = $env:PROCESSOR_ARCHITECTURE
Write-Success "Architecture: $arch"
if ($arch -eq "ARM64") {
    Write-Info "ARM64 detected - builds will target ARM64 by default"
}

if ($buildStoreMsix) {
    if ($Project -notin @("All", "Tray", "WinUI")) {
        Write-Error "-Msix Store requires -Project All, Tray, or WinUI."
        $issues += "Store MSIX packaging requires the WinUI project"
    }

    Write-Success "Store MSIX target architectures: $($storeMsixRuntimeIdentifiers -join ', ')"
    Write-Info "Packages are left unsigned; Partner Center signs Store submissions."
}

if ($buildDevMsix) {
    if ($Project -notin @("All", "Tray", "WinUI")) {
        Write-Error "-Msix Dev requires -Project All, Tray, or WinUI."
        $issues += "MSIX packaging requires the WinUI project"
    }

    $devMsixCertificateDirectory = Join-Path $env:LOCALAPPDATA "OpenClawDevelopment\MSIX"
    $devMsixThumbprintFile = Join-Path $devMsixCertificateDirectory "dev-msix-thumbprint.txt"
    $devMsixThumbprint = if (Test-Path $devMsixThumbprintFile) {
        (Get-Content -LiteralPath $devMsixThumbprintFile -Raw).Trim()
    } else {
        ""
    }
    $devMsixCertificate = if ($devMsixThumbprint) {
        Get-Item "Cert:\CurrentUser\My\$devMsixThumbprint" -ErrorAction SilentlyContinue
    } else {
        $null
    }

    if ($devMsixCertificate) {
        Write-Success "Development MSIX signing certificate: $($devMsixCertificate.Thumbprint)"
    } else {
        Write-Error "Development MSIX signing certificate not found."
        Write-Info "Run .\scripts\setup-dev-msix-cert.ps1 from an elevated PowerShell, then retry."
        $issues += "Missing development MSIX signing certificate"
    }
}

# Summary
Write-Header "Prerequisite Summary"

if ($issues.Count -eq 0) {
    Write-Success "All prerequisites met!"
} else {
    Write-Warning "$($issues.Count) issue(s) found:"
    foreach ($issue in $issues) {
        Write-Info "- $issue"
    }
}

Write-Header "Validating Documentation"
& (Join-Path $repoRoot "scripts\validate-docs.ps1") -RepoRoot $repoRoot
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($CheckOnly) {
    Write-Host "`nRun without -CheckOnly to build.`n"
    exit 0
}

if ($issues.Count -gt 0) {
    Write-Host "`nFix the prerequisite issue(s) above, then rerun .\build.ps1.`n" -ForegroundColor Yellow
    exit 1
}

# =============================================================================
# BUILD
# =============================================================================

Write-Header "Building Projects ($Configuration)"

# Detect runtime identifier based on architecture
$rid = switch ($arch) {
    "ARM64" { "win-arm64" }
    default { "win-x64" }
}
Write-Info "Runtime identifier: $rid"

$buildResults = @{}

function Invoke-DotNetCaptured($arguments) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        return & dotnet @arguments 2>&1
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Build-Project($name, $path, $useRid = $false, $packageMsix = $false) {
    Write-Host "`nBuilding $name..." -ForegroundColor White
    
    if (-not (Test-Path $path)) {
        Write-Error "Project not found: $path"
        return $false
    }
    
    if ($packageMsix) {
        $installedDevPackage = Get-AppxPackage -Name "OpenClawFoundation.OpenClaw.Dev" -ErrorAction SilentlyContinue |
            Where-Object Publisher -eq "CN=OpenClaw Local Development" |
            Sort-Object { [version]$_.Version.ToString() } -Descending |
            Select-Object -First 1
        $msixRevision = if ($installedDevPackage) {
            ([version]$installedDevPackage.Version.ToString()).Revision + 1
        } else {
            1
        }
        if ($msixRevision -gt 65535) {
            Write-Error "The installed development MSIX revision is already 65535. Remove the installed OpenClawFoundation.OpenClaw.Dev package before rebuilding."
            return $false
        }

        $dotnetArgs = @(
            "publish", $path,
            "-c", $Configuration,
            "-r", $rid,
            "--self-contained",
            "-p:MsixRevision=$msixRevision"
        )
    } else {
        $dotnetArgs = @("build", $path, "-c", $Configuration)
        # WinUI requires runtime identifier for self-contained WebView2 support
        if ($useRid) {
            $dotnetArgs += @("-r", $rid)
        }
    }
    if ($DevBuild -and ($name -eq "WinUI" -or $name -eq "Tray")) {
        $dotnetArgs += "-p:DevBuild=true"
    }
    if ($packageMsix) {
        $platform = if ($rid -eq "win-arm64") { "ARM64" } else { "x64" }
        $dotnetArgs += @(
            "-p:Platform=$platform",
            "-p:PackageMsix=true",
            "-p:GenerateAppxPackageOnBuild=true",
            "-p:AppxBundle=Never",
            "-p:UapAppxPackageBuildMode=SideloadOnly",
            "-p:AppxPackageDir=AppPackages\"
        )
    }
    $result = Invoke-DotNetCaptured $dotnetArgs
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -eq 0) {
        Write-Success "$name built successfully"
        return $true
    } else {
        Write-Error "$name build failed"
        # Show relevant error lines
        $result | Select-String "error" | Select-Object -First 5 | ForEach-Object {
            Write-Info $_.Line
        }

        $lockingProcesses = $result |
            Select-String 'file is locked by: "(.+) \((\d+)\)"' |
            ForEach-Object {
                [PSCustomObject]@{
                    Name = $_.Matches[0].Groups[1].Value
                    Id = $_.Matches[0].Groups[2].Value
                }
            } |
            Sort-Object Id -Unique

        foreach ($lockingProcess in $lockingProcesses) {
            Write-Warning "Build output is locked by $($lockingProcess.Name) (PID $($lockingProcess.Id)). Close the app, or run: Stop-Process -Id $($lockingProcess.Id)"
        }

        return $false
    }
}

function Get-ProjectTargetFramework($path) {
    if (-not (Test-Path $path)) {
        return $null
    }

    [xml]$projectXml = Get-Content $path -Raw
    return $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.TargetFramework } |
        Where-Object { $_ } |
        Select-Object -First 1
}

$projects = @{
    "Shared" = @{ Path = "src/OpenClaw.Shared/OpenClaw.Shared.csproj"; UseRid = $false }
    "Cli" = @{ Path = "src/OpenClaw.Cli/OpenClaw.Cli.csproj"; UseRid = $false }
    "WinNodeCli" = @{ Path = "src/OpenClaw.WinNode.Cli/OpenClaw.WinNode.Cli.csproj"; UseRid = $false }
    "Tray" = @{ Path = "src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj"; UseRid = $true }
    "WinUI" = @{ Path = "src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj"; UseRid = $true }
    "SetupEngine" = @{ Path = "src/OpenClaw.SetupEngine/OpenClaw.SetupEngine.csproj"; UseRid = $false }
}

if ($buildStoreMsix) {
    # scripts\Build-StoreMsix.ps1 owns packaging, identity verification, and the
    # provenance sidecar. build.ps1 only drives it once per architecture.
    $storeArchitectures = $storeMsixRuntimeIdentifiers | ForEach-Object { $_ -replace "^win-", "" }
    $storePackages = @()
    foreach ($storeArchitecture in $storeArchitectures) {
        Write-Host "`nBuilding Store MSIX ($storeArchitecture)..." -ForegroundColor White
        try {
            & (Join-Path $repoRoot "scripts\Build-StoreMsix.ps1") `
                -Architecture $storeArchitecture `
                -Configuration $Configuration
        } catch {
            Write-Error "Store MSIX ($storeArchitecture) packaging failed: $($_.Exception.Message)"
            exit 1
        }
        $storePackages += Join-Path $repoRoot "artifacts\msix\$storeArchitecture\OpenClawCompanion-$storeArchitecture.msix"
    }

    Write-Header "Store MSIX Packages"
    foreach ($package in $storePackages) {
        if (-not (Test-Path -LiteralPath $package)) {
            Write-Error "Expected package was not produced: $package"
            exit 1
        }
        Write-Success $package
    }
    Write-Host "`nUpload both packages to the same Partner Center submission." -ForegroundColor Cyan
    Write-Info "Identity is taken from src\OpenClaw.Tray.WinUI\Package.appxmanifest and must keep"
    Write-Info "matching Partner Center > Product management > Product identity. The Store re-signs"
    Write-Info "these packages, so they are intentionally left unsigned here."
    Write-Host ""
    exit 0
}

$toBuild = if ($Project -eq "All") { @("Shared", "Cli", "WinNodeCli", "SetupEngine", "WinUI") } else { @($Project) }

# Always build Shared first if building other projects
if ($Project -ne "Shared" -and $Project -ne "All" -and $toBuild -notcontains "Shared") {
    $toBuild = @("Shared") + $toBuild
}

for ($i = 0; $i -lt $toBuild.Count; $i++) {
    $proj = $toBuild[$i]
    if ($projects.ContainsKey($proj)) {
        $projInfo = $projects[$proj]
        $shouldPackageMsix = $buildDevMsix -and ($proj -eq "WinUI" -or $proj -eq "Tray")
        $buildResults[$proj] = Build-Project $proj $projInfo.Path $projInfo.UseRid $shouldPackageMsix
        if ($proj -eq "Shared" -and -not $buildResults[$proj] -and $i -lt ($toBuild.Count - 1)) {
            Write-Warning "Skipping remaining projects because Shared failed."
            break
        }
    }
}

Write-Header "Build Summary"

$successCount = ($buildResults.Values | Where-Object { $_ -eq $true }).Count
$failCount = ($buildResults.Values | Where-Object { $_ -eq $false }).Count

foreach ($proj in $buildResults.Keys) {
    if ($buildResults[$proj]) {
        Write-Success "$proj"
    } else {
        Write-Error "$proj"
    }
}

Write-Host ""
if ($failCount -eq 0) {
    Write-Host "🦞 All builds succeeded!" -ForegroundColor Green
    
    Write-Host "`nTo run:" -ForegroundColor Cyan
    if (($buildResults.ContainsKey("WinUI") -and $buildResults["WinUI"]) -or ($buildResults.ContainsKey("Tray") -and $buildResults["Tray"])) {
        $winUIProjectPath = $projects["WinUI"].Path
        $winUITargetFramework = Get-ProjectTargetFramework $winUIProjectPath
        $winUIProjectDirectory = (Split-Path -Parent $winUIProjectPath).Replace("/", "\")

        if ($buildDevMsix) {
            $devMsixPackage = Get-ChildItem (Join-Path $repoRoot "$winUIProjectDirectory\AppPackages") -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if ($devMsixPackage) {
                Write-Host "  MSIX:     $($devMsixPackage.FullName)" -ForegroundColor White
                Write-Host "  Install:  Add-AppxPackage -Path `"$($devMsixPackage.FullName)`" -ForceApplicationShutdown" -ForegroundColor White
            } else {
                Write-Warning "MSIX packaging succeeded but no .msix was found under $winUIProjectDirectory\AppPackages."
            }
        }

        if ($winUITargetFramework) {
            $winUIOutputDirectory = ".\$winUIProjectDirectory\bin\$Configuration\$winUITargetFramework\$rid"
            $winUIManifestPath = ".\$winUIProjectDirectory\Package.appxmanifest"
            $runIdentitySwitch = if ($DevBuild) { " -Dev" } else { "" }
            Write-Host "  WinUI:    .\run-app-local.ps1 -NoBuild$runIdentitySwitch" -ForegroundColor White
            Write-Host "  Isolated: .\run-app-local.ps1 -NoBuild -Isolated$runIdentitySwitch" -ForegroundColor White
            Write-Host "  Dev:      .\run-app-local.ps1 -Dev" -ForegroundColor White
            Write-Host "  WinApp:   .\run-app-local.ps1 -NoBuild -UseWinApp$runIdentitySwitch" -ForegroundColor White
            Write-Host "            Direct launch is default. -UseWinApp runs: winapp run `"$winUIOutputDirectory`" --manifest `"$winUIManifestPath`" --executable `"OpenClaw.Tray.WinUI.exe`" --debug-output" -ForegroundColor DarkGray
        } else {
            Write-Warning "Unable to determine WinUI target framework from $winUIProjectPath"
        }
    }
} else {
    Write-Host "❌ $failCount build(s) failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
