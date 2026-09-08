<#
.SYNOPSIS
    Exercises stable CI Gate pass, failure, cancellation, and skip contracts.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$gatePath = Join-Path $RepoRoot "scripts\Assert-CiGateResults.ps1"

function New-GateArguments {
    @{
        ClassificationResult = "success"
        Classification = "targeted"
        FullRequired = "false"
        FastValidationResult = "success"
        ProofPoolContractsResult = "success"
        CoreRequired = "true"
        CoreResult = "success"
        TrayRequired = "false"
        TrayResult = "skipped"
        UiRequired = "false"
        UiResult = "skipped"
        SetupE2eRequired = "false"
        SetupE2eResult = "skipped"
        RevocationE2eRequired = "false"
        RevocationE2eResult = "skipped"
        NetworkE2eRequired = "false"
        NetworkE2eResult = "skipped"
        X64ReleaseRequired = "false"
        X64ReleaseResult = "skipped"
        Arm64ReleaseRequired = "false"
        Arm64ReleaseResult = "skipped"
        MetadataResult = "skipped"
    }
}

function Invoke-Gate([hashtable]$Arguments) {
    & $gatePath @Arguments
}

function Assert-GateFails {
    param(
        [Parameter(Mandatory)][hashtable]$Overrides,
        [Parameter(Mandatory)][string]$Scenario
    )

    $arguments = New-GateArguments
    foreach ($entry in $Overrides.GetEnumerator()) {
        $arguments[$entry.Key] = $entry.Value
    }
    try {
        $result = Invoke-Gate $arguments
        throw "$Scenario unexpectedly passed with result '$result'."
    } catch {
        if ($_.Exception.Message.StartsWith(
                "$Scenario unexpectedly passed",
                [StringComparison]::Ordinal)) {
            throw
        }
    }
}

$targeted = Invoke-Gate (New-GateArguments)
if ($targeted -ne "targeted") {
    throw "Expected a core-only targeted gate to pass."
}

$docsArguments = New-GateArguments
$docsArguments.Classification = "docs_only"
$docsArguments.CoreRequired = "false"
$docsArguments.CoreResult = "skipped"
$docsOnly = Invoke-Gate $docsArguments
if ($docsOnly -ne "docs_only") {
    throw "Expected the docs-only gate to pass."
}

$uiArguments = New-GateArguments
$uiArguments.CoreRequired = "false"
$uiArguments.CoreResult = "skipped"
$uiArguments.TrayRequired = "true"
$uiArguments.TrayResult = "success"
$uiArguments.UiRequired = "true"
$uiArguments.UiResult = "success"
$uiTargeted = Invoke-Gate $uiArguments
if ($uiTargeted -ne "targeted") {
    throw "Expected the UI-targeted gate to pass."
}

$fullArguments = New-GateArguments
$fullArguments.Classification = "full"
$fullArguments.FullRequired = "true"
foreach ($prefix in @(
        "Core",
        "Tray",
        "Ui",
        "SetupE2e",
        "RevocationE2e",
        "NetworkE2e",
        "X64Release",
        "Arm64Release")) {
    $fullArguments["${prefix}Required"] = "true"
    $fullArguments["${prefix}Result"] = "success"
}
$fullArguments.MetadataResult = "success"
$full = Invoke-Gate $fullArguments
if ($full -ne "full") {
    throw "Expected the full gate to pass."
}

$fullPrArguments = New-GateArguments
$fullPrArguments.Classification = "full"
$fullPrArguments.FullRequired = "true"
foreach ($prefix in @(
        "Core",
        "Tray",
        "Ui",
        "SetupE2e",
        "RevocationE2e",
        "NetworkE2e",
        "X64Release")) {
    $fullPrArguments["${prefix}Required"] = "true"
    $fullPrArguments["${prefix}Result"] = "success"
}
$fullPrArguments.MetadataResult = "success"
$fullPr = Invoke-Gate $fullPrArguments
if ($fullPr -ne "full") {
    throw "Expected full pull request validation without ARM64 publish to pass."
}

Assert-GateFails -Overrides @{ ClassificationResult = "failure" } -Scenario "Failed classification"
Assert-GateFails -Overrides @{ FastValidationResult = "cancelled" } -Scenario "Cancelled fast validation"
Assert-GateFails -Overrides @{ ProofPoolContractsResult = "failure" } -Scenario "Failed proof contracts"
Assert-GateFails -Overrides @{ CoreRequired = "" } -Scenario "Missing classifier output"
Assert-GateFails -Overrides @{ CoreResult = "skipped" } -Scenario "Required lane skipped"
Assert-GateFails -Overrides @{ CoreResult = "cancelled" } -Scenario "Required lane cancelled"
Assert-GateFails -Overrides @{ CoreResult = "failure" } -Scenario "Required lane failed"
Assert-GateFails `
    -Overrides @{ CoreRequired = "false"; CoreResult = "success" } `
    -Scenario "Unrequired lane ran"
Assert-GateFails -Overrides @{ Classification = "unexpected" } -Scenario "Unknown classification"
Assert-GateFails `
    -Overrides @{ Classification = "docs_only"; CoreRequired = "false" } `
    -Scenario "Docs lane unexpectedly succeeded"
Assert-GateFails `
    -Overrides @{ Classification = "full"; FullRequired = "true" } `
    -Scenario "Full classification omitted lanes"
Assert-GateFails `
    -Overrides @{
        X64ReleaseRequired = "true"
        X64ReleaseResult = "success"
        MetadataResult = "skipped"
    } `
    -Scenario "Release omitted metadata"

Write-Host "CI Gate regressions passed: selected lanes require success, intentional skips pass, and malformed or unexpected results fail closed." -ForegroundColor Green
