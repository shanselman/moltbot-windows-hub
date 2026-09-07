<#
.SYNOPSIS
    Selects whether CI should run the full proof-pool regression matrix.
#>

[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha = $env:GITHUB_SHA,
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)

function Complete-Decision([bool]$ShouldRun) {
    $global:LASTEXITCODE = 0
    $ShouldRun.ToString().ToLowerInvariant()
}

if ($EventName -ne "pull_request") {
    Complete-Decision $true
    return
}

if ([string]::IsNullOrWhiteSpace($BaseSha) -or
    [string]::IsNullOrWhiteSpace($HeadSha)) {
    Write-Warning "Proof-pool diff inputs are incomplete. Running the regression fail-closed."
    Complete-Decision $true
    return
}

$triggerPaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
@(
    ".github/workflows/ci.yml",
    ".github/proof-pools.json",
    ".github/proof-pools.schema.json",
    "scripts/validate-proof-pools.ps1",
    "scripts/test-proof-pool-validator.ps1",
    "scripts/test-validate-docs-proof-pool-flow.ps1",
    "scripts/validate-docs.ps1",
    "scripts/Get-CiProofPoolRegressionDecision.ps1",
    "scripts/Get-CiChangeClassification.ps1",
    "scripts/test-ci-change-classifier.ps1",
    "scripts/Assert-CiGateResults.ps1",
    "scripts/test-ci-gate-results.ps1",
    "scripts/validate-agent-skills.ps1",
    "scripts/test-agent-skills-validator.ps1",
    "scripts/test-ci-workflow-contract.ps1"
) | ForEach-Object { [void]$triggerPaths.Add($_) }

try {
    $diffOutput = @(
        & git -C $repoRootPath diff --name-only --no-renames "$BaseSha...$HeadSha" 2>&1
    )
    $gitExitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
} catch {
    Write-Warning "Proof-pool diff failed. Running the regression fail-closed: $($_.Exception.Message)"
    Complete-Decision $true
    return
}

if ($gitExitCode -ne 0) {
    $detail = ($diffOutput | Out-String).Trim()
    Write-Warning "Proof-pool diff exited with code $gitExitCode. Running the regression fail-closed: $detail"
    Complete-Decision $true
    return
}

$shouldRun = $false
foreach ($changedPath in $diffOutput) {
    $normalizedPath = ([string]$changedPath).Trim().Replace("\", "/")
    if ($triggerPaths.Contains($normalizedPath)) {
        $shouldRun = $true
        break
    }
}

Complete-Decision $shouldRun
