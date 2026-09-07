<#
.SYNOPSIS
    Classifies changed paths into conservative CI validation lanes.

.DESCRIPTION
    Pull requests receive explicit lane decisions from recognized project
    boundaries. Unknown paths, build or workflow infrastructure, project files,
    classifier contracts, invalid revisions, empty diffs, and non-PR events
    select every lane fail-closed.
#>

[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha = $env:GITHUB_SHA,
    [string]$RepoRoot,
    [string[]]$ChangedPaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)

function New-Impact([string]$Classification) {
    [ordered]@{
        classification = $Classification
        core_tests = $false
        tray_tests = $false
        ui_tests = $false
        setup_e2e = $false
        revocation_e2e = $false
        network_e2e = $false
        x64_release = $false
        arm64_release = $false
        full = $false
    }
}

function New-FullImpact {
    param(
        [switch]$IncludeArm64
    )

    $impact = New-Impact "full"
    foreach ($lane in @(
            "core_tests",
            "tray_tests",
            "ui_tests",
            "setup_e2e",
            "revocation_e2e",
            "network_e2e",
            "x64_release",
            "full")) {
        $impact[$lane] = $true
    }
    if ($IncludeArm64) {
        $impact.arm64_release = $true
    }
    $impact
}

function Complete-Impact([System.Collections.IDictionary]$Impact) {
    $global:LASTEXITCODE = 0
    $Impact | ConvertTo-Json -Compress
}

function Add-Lanes {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Impact,
        [switch]$Core,
        [switch]$Tray,
        [switch]$Ui,
        [switch]$SetupE2e,
        [switch]$RevocationE2e,
        [switch]$NetworkE2e
    )

    if ($Core) { $Impact.core_tests = $true }
    if ($Tray) { $Impact.tray_tests = $true }
    if ($Ui) { $Impact.ui_tests = $true }
    if ($SetupE2e) { $Impact.setup_e2e = $true }
    if ($RevocationE2e) { $Impact.revocation_e2e = $true }
    if ($NetworkE2e) { $Impact.network_e2e = $true }
}

function ConvertTo-NormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $normalizedPath = $Path.Trim().Replace("\", "/")
    if ($normalizedPath.StartsWith("/", [StringComparison]::Ordinal) -or
        $normalizedPath.Contains("..", [StringComparison]::Ordinal) -or
        $normalizedPath.Contains([char]0)) {
        return $null
    }
    $normalizedPath
}

function Test-IsSafeDocumentationPath([string]$Path) {
    $rootDocumentation = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    @(
        "AGENTS.md",
        "DEVELOPMENT.md",
        "README.md",
        "SECURITY.md",
        ".github/pull_request_template.md"
    ) | ForEach-Object { [void]$rootDocumentation.Add($_) }
    if ($rootDocumentation.Contains($Path)) {
        return $true
    }

    $extension = [System.IO.Path]::GetExtension($Path)
    $safeExtensions = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    @(
        ".md",
        ".excalidraw",
        ".svg",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp"
    ) | ForEach-Object { [void]$safeExtensions.Add($_) }
    if (-not $safeExtensions.Contains($extension)) {
        return $false
    }

    return $Path.StartsWith("docs/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith(".agents/skills/", [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsBuildInfrastructurePath([string]$Path) {
    if ($Path -match '^(?i:\.github/(?:workflows|actions)/|scripts/|installer/|packaging/)' -or
        $Path -match '^(?i:build\.(?:ps1|cmd|bat)|global\.json|NuGet\.Config|Directory\.(?:Build|Packages)\.(?:props|targets)|installer\.iss)$') {
        return $true
    }

    return $Path -match '(?i)(?:^|/)(?:Directory\.(?:Build|Packages)\.(?:props|targets)|[^/]+\.(?:csproj|sln|slnx|wapproj|props|targets|appxmanifest|wxs|iss)|packages\.lock\.json|package(?:-lock)?\.json)$'
}

function Add-PathImpact {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Impact,
        [Parameter(Mandatory)][string]$Path
    )

    if ($Path.StartsWith("src/OpenClaw.Cli/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("src/OpenClaw.WinNode.Cli/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Chat/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("src/OpenClawTray.FunctionalUI/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -Ui
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.SetupEngine.UI/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -Ui -SetupE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.SetupEngine/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -SetupE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Connection/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core -Tray -SetupE2e -RevocationE2e -NetworkE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Shared/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core -Tray -Ui -SetupE2e -RevocationE2e -NetworkE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Tray.WinUI/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -Ui
        if ($Path -match '(?i)(?:setup|onboarding|pairing)') {
            Add-Lanes -Impact $Impact -SetupE2e
        }
        if ($Path -match '(?i)(?:connection|gateway|mcp|node|wsl)') {
            Add-Lanes -Impact $Impact -SetupE2e -RevocationE2e -NetworkE2e
        }
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.Shared.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.Connection.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.WinNode.Cli.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.Shared.TestHost/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.Tray.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.SetupEngine.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.Tray.IntegrationTests/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.Tray.UITests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClawTray.FunctionalUI.Tests/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Ui
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.TestSupport/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core -Tray
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.E2ETests/", [StringComparison]::OrdinalIgnoreCase)) {
        if ($Path -match '(?i)RevocationAndRecovery') {
            Add-Lanes -Impact $Impact -RevocationE2e
        } elseif ($Path -match '(?i)NetworkRecovery') {
            Add-Lanes -Impact $Impact -NetworkE2e
        } elseif ($Path -match '(?i)(?:SetupAndConnect|MxcSetupAndConnect|MirroredWslPortLease|SshOwnershipAdversarialProof|TrayExecutableResolution)') {
            Add-Lanes -Impact $Impact -SetupE2e
        } else {
            Add-Lanes -Impact $Impact -SetupE2e -RevocationE2e -NetworkE2e
        }
        return $true
    }

    return $false
}

if ($EventName -ne "pull_request") {
    Complete-Impact (New-FullImpact -IncludeArm64)
    return
}

$paths = @()
if ($PSBoundParameters.ContainsKey("ChangedPaths")) {
    $paths = @($ChangedPaths)
} else {
    $shaPattern = "^[0-9a-fA-F]{40}$"
    if ([string]::IsNullOrWhiteSpace($BaseSha) -or
        [string]::IsNullOrWhiteSpace($HeadSha) -or
        $BaseSha -notmatch $shaPattern -or
        $HeadSha -notmatch $shaPattern) {
        Write-Warning "CI diff revisions are missing or invalid. Selecting full validation."
        Complete-Impact (New-FullImpact)
        return
    }

    foreach ($sha in @($BaseSha, $HeadSha)) {
        & git -C $repoRootPath cat-file -e "$sha^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            $global:LASTEXITCODE = 0
            Write-Warning "CI diff revision '$sha' is unavailable. Selecting full validation."
            Complete-Impact (New-FullImpact)
            return
        }
    }

    try {
        $diffOutput = @(
            & git -C $repoRootPath diff --name-only --no-renames "$BaseSha...$HeadSha" -- 2>&1
        )
        $gitExitCode = $LASTEXITCODE
        $global:LASTEXITCODE = 0
    } catch {
        Write-Warning "CI diff failed. Selecting full validation: $($_.Exception.Message)"
        Complete-Impact (New-FullImpact)
        return
    }

    if ($gitExitCode -ne 0) {
        $detail = ($diffOutput | Out-String).Trim()
        Write-Warning "CI diff exited with code $gitExitCode. Selecting full validation: $detail"
        Complete-Impact (New-FullImpact)
        return
    }
    $paths = @($diffOutput)
}

if ($paths.Count -eq 0) {
    Write-Warning "CI diff contained no changed paths. Selecting full validation."
    Complete-Impact (New-FullImpact)
    return
}

$impact = New-Impact "targeted"
$hasProductChange = $false
foreach ($changedPath in $paths) {
    $normalizedPath = ConvertTo-NormalizedPath ([string]$changedPath)
    if ($null -eq $normalizedPath) {
        Complete-Impact (New-FullImpact)
        return
    }
    if ($normalizedPath.Equals(
            ".agents/skills/winnode/SKILL.md",
            [StringComparison]::OrdinalIgnoreCase)) {
        $hasProductChange = $true
        Add-Lanes -Impact $impact -Core
        continue
    }
    if (Test-IsSafeDocumentationPath $normalizedPath) {
        continue
    }

    $hasProductChange = $true
    if (Test-IsBuildInfrastructurePath $normalizedPath) {
        Complete-Impact (New-FullImpact)
        return
    }
    if (-not (Add-PathImpact -Impact $impact -Path $normalizedPath)) {
        Complete-Impact (New-FullImpact)
        return
    }
}

if (-not $hasProductChange) {
    Complete-Impact (New-Impact "docs_only")
    return
}

Complete-Impact $impact
