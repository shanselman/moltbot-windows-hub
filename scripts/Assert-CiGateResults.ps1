<#
.SYNOPSIS
    Enforces the stable CI Gate result contract for classifier-selected lanes.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClassificationResult,
    [Parameter(Mandatory)][string]$Classification,
    [Parameter(Mandatory)][string]$FullRequired,
    [Parameter(Mandatory)][string]$FastValidationResult,
    [Parameter(Mandatory)][string]$ProofPoolContractsResult,
    [Parameter(Mandatory)][string]$CoreRequired,
    [Parameter(Mandatory)][string]$CoreResult,
    [Parameter(Mandatory)][string]$TrayRequired,
    [Parameter(Mandatory)][string]$TrayResult,
    [Parameter(Mandatory)][string]$UiRequired,
    [Parameter(Mandatory)][string]$UiResult,
    [Parameter(Mandatory)][string]$SetupE2eRequired,
    [Parameter(Mandatory)][string]$SetupE2eResult,
    [Parameter(Mandatory)][string]$RevocationE2eRequired,
    [Parameter(Mandatory)][string]$RevocationE2eResult,
    [Parameter(Mandatory)][string]$NetworkE2eRequired,
    [Parameter(Mandatory)][string]$NetworkE2eResult,
    [Parameter(Mandatory)][string]$X64ReleaseRequired,
    [Parameter(Mandatory)][string]$X64ReleaseResult,
    [Parameter(Mandatory)][string]$Arm64ReleaseRequired,
    [Parameter(Mandatory)][string]$Arm64ReleaseResult,
    [Parameter(Mandatory)][string]$MetadataResult
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-RequiredBoolean {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    if ($Value -eq "true") { return $true }
    if ($Value -eq "false") { return $false }
    throw "Classifier output '$Name' must be 'true' or 'false', but it was '$Value'."
}

function Assert-LaneResult {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Required,
        [Parameter(Mandatory)][string]$Result
    )

    $expected = if ($Required) { "success" } else { "skipped" }
    if ($Result -ne $expected) {
        throw "CI lane '$Name' required=$Required expected '$expected', but it was '$Result'."
    }
}

if ($ClassificationResult -ne "success") {
    throw "Change classification did not succeed: $ClassificationResult"
}
if ($FastValidationResult -ne "success") {
    throw "Fast validation did not succeed: $FastValidationResult"
}
if ($ProofPoolContractsResult -ne "success") {
    throw "Proof-pool contract selection or validation did not succeed: $ProofPoolContractsResult"
}

$required = [ordered]@{
    core = ConvertTo-RequiredBoolean "core_tests" $CoreRequired
    tray = ConvertTo-RequiredBoolean "tray_tests" $TrayRequired
    ui = ConvertTo-RequiredBoolean "ui_tests" $UiRequired
    setup_e2e = ConvertTo-RequiredBoolean "setup_e2e" $SetupE2eRequired
    revocation_e2e = ConvertTo-RequiredBoolean "revocation_e2e" $RevocationE2eRequired
    network_e2e = ConvertTo-RequiredBoolean "network_e2e" $NetworkE2eRequired
    x64_release = ConvertTo-RequiredBoolean "x64_release" $X64ReleaseRequired
    arm64_release = ConvertTo-RequiredBoolean "arm64_release" $Arm64ReleaseRequired
}
$full = ConvertTo-RequiredBoolean "full" $FullRequired

switch ($Classification) {
    "docs_only" {
        if ($full -or @($required.Values | Where-Object { $_ }).Count -ne 0) {
            throw "Docs-only classification may not require product lanes."
        }
    }
    "targeted" {
        if ($full) {
            throw "Targeted classification may not set full=true."
        }
        if (@($required.Values | Where-Object { $_ }).Count -eq 0) {
            throw "Targeted classification must require at least one lane."
        }
    }
    "full" {
        if (-not $full) {
            throw "Full classification must set full=true."
        }
        foreach ($laneName in @(
                "core",
                "tray",
                "ui",
                "setup_e2e",
                "revocation_e2e",
                "network_e2e",
                "x64_release")) {
            if (-not $required[$laneName]) {
                throw "Full classification must require lane '$laneName'."
            }
        }
    }
    default {
        throw "Unknown or missing change classification '$Classification'."
    }
}

Assert-LaneResult "core tests" $required.core $CoreResult
Assert-LaneResult "tray tests" $required.tray $TrayResult
Assert-LaneResult "UI tests" $required.ui $UiResult
Assert-LaneResult "setup E2E" $required.setup_e2e $SetupE2eResult
Assert-LaneResult "revocation E2E" $required.revocation_e2e $RevocationE2eResult
Assert-LaneResult "network E2E" $required.network_e2e $NetworkE2eResult
Assert-LaneResult "x64 release publish" $required.x64_release $X64ReleaseResult
Assert-LaneResult "ARM64 release publish" $required.arm64_release $Arm64ReleaseResult

$metadataRequired = $required.x64_release -or $required.arm64_release
Assert-LaneResult "release metadata" $metadataRequired $MetadataResult

$Classification
