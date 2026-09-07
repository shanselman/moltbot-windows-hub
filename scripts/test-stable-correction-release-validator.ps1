<#
.SYNOPSIS
    Deterministic offline regressions for the stable correction release
    validator.

.DESCRIPTION
    Every case runs the real validator with an explicit -CurrentWindowsTag so
    ordering is proven without any network call. The harness also asserts that
    the validator source has no dependency on another repository's release API.
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
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$validatorPath = Join-Path $repoRootPath "scripts\Test-OpenClawStableCorrectionRelease.ps1"
if (-not (Test-Path -LiteralPath $validatorPath)) {
    throw "Correction release validator not found at '$validatorPath'."
}

$script:acceptedCount = 0
$script:rejectedCount = 0
$script:classificationCount = 0
$script:workflowCorrectionPattern = $null

function Assert-CorrectionAccepted {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$CurrentWindowsTag,
        [Parameter(Mandatory)][string]$ExpectedBaseVersion,
        [Parameter(Mandatory)][long]$ExpectedCorrection
    )

    $result = & $validatorPath -Tag $Tag -CurrentWindowsTag $CurrentWindowsTag
    if ($null -eq $result) {
        throw "Case '$Name': validator produced no result for '$Tag'."
    }
    if ($result.Tag -cne $Tag) {
        throw "Case '$Name': expected Tag '$Tag' but got '$($result.Tag)'."
    }
    if ($result.BaseVersion -cne $ExpectedBaseVersion) {
        throw "Case '$Name': expected BaseVersion '$ExpectedBaseVersion' but got '$($result.BaseVersion)'."
    }
    if ([long]$result.Correction -ne $ExpectedCorrection) {
        throw "Case '$Name': expected Correction '$ExpectedCorrection' but got '$($result.Correction)'."
    }
    if ($result.CurrentWindowsTag -cne $CurrentWindowsTag) {
        throw "Case '$Name': expected CurrentWindowsTag '$CurrentWindowsTag' but got '$($result.CurrentWindowsTag)'."
    }

    $script:acceptedCount++
    Write-Host "  accepted: $Name ($CurrentWindowsTag -> $Tag)"
}

function Assert-CorrectionRejected {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$CurrentWindowsTag,
        [Parameter(Mandatory)][string]$ExpectedMessage
    )

    $failure = $null
    try {
        & $validatorPath -Tag $Tag -CurrentWindowsTag $CurrentWindowsTag | Out-Null
    } catch {
        $failure = $_.Exception.Message
    }

    if ($null -eq $failure) {
        throw "Case '$Name': validator accepted '$Tag' against current '$CurrentWindowsTag'."
    }
    if ($failure -notlike "*$ExpectedMessage*") {
        throw "Case '$Name': expected rejection containing '$ExpectedMessage' but got '$failure'."
    }

    $script:rejectedCount++
    Write-Host "  rejected: $Name ($CurrentWindowsTag -> $Tag)"
}

function Assert-InvalidTagRejected {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$CurrentWindowsTag
    )

    $failure = $null
    try {
        & $validatorPath -Tag $Tag -CurrentWindowsTag $CurrentWindowsTag | Out-Null
    } catch {
        $failure = $_.Exception.Message
    }

    if ($null -eq $failure) {
        throw "Case '$Name': validator accepted malformed tag '$Tag'."
    }

    $script:rejectedCount++
    Write-Host "  rejected: $Name ($Tag)"
}

function Assert-WorkflowClassification {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][bool]$ExpectedRoutedToValidator
    )

    $tagVersion = $Tag -replace '^v', ''
    $routed = [regex]::IsMatch($tagVersion, $script:workflowCorrectionPattern)
    if ($routed -ne $ExpectedRoutedToValidator) {
        throw ("Case '$Name': tag '$Tag' routed-to-validator was '$routed' " +
               "but expected '$ExpectedRoutedToValidator'.")
    }

    $script:classificationCount++
    $decision = if ($routed) { "validator" } else { "gitversion" }
    Write-Host "  classified: $Name ($Tag -> $decision)"
}

Write-Host "Validating correction release source contract..."
$validatorSource = Get-Content -LiteralPath $validatorPath -Raw
if ($validatorSource -match 'repos/openclaw/openclaw/') {
    throw "Validator must not depend on the openclaw/openclaw release API."
}
if ($validatorSource -notmatch 'repos/openclaw/openclaw-windows-node/releases/latest') {
    throw "Validator must resolve the current Windows latest release tag."
}

Write-Host "Validating release workflow correction classification..."
$workflowPath = Join-Path $repoRootPath ".github\workflows\ci.yml"
$workflowSource = Get-Content -LiteralPath $workflowPath -Raw
$patternMatch = [regex]::Match(
    $workflowSource,
    "(?m)^\s*'(?<pattern>\^\(\?<base>.*?\)-\(\?<revision>.*?\)\`$)'\s*\)")
if (-not $patternMatch.Success) {
    throw "Could not read the release workflow correction classification pattern from '$workflowPath'."
}
$script:workflowCorrectionPattern = $patternMatch.Groups["pattern"].Value

Assert-WorkflowClassification `
    -Name "valid correction reaches the validator" `
    -Tag "v2026.7.1-3" `
    -ExpectedRoutedToValidator $true
Assert-WorkflowClassification `
    -Name "zero correction cannot bypass the validator" `
    -Tag "v2026.7.1-0" `
    -ExpectedRoutedToValidator $true
Assert-WorkflowClassification `
    -Name "leading-zero correction cannot bypass the validator" `
    -Tag "v2026.7.1-03" `
    -ExpectedRoutedToValidator $true
Assert-WorkflowClassification `
    -Name "unsuffixed stable tag stays on GitVersion" `
    -Tag "v2026.7.1" `
    -ExpectedRoutedToValidator $false
Assert-WorkflowClassification `
    -Name "alpha prerelease stays on GitVersion" `
    -Tag "v2026.7.2-alpha.19" `
    -ExpectedRoutedToValidator $false

Write-Host "Validating same-line monotonic correction acceptance..."
Assert-CorrectionAccepted `
    -Name "next correction on current release line" `
    -Tag "v2026.7.1-3" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedBaseVersion "2026.7.1" `
    -ExpectedCorrection 3
Assert-CorrectionAccepted `
    -Name "first correction over unsuffixed base" `
    -Tag "v2026.7.1-1" `
    -CurrentWindowsTag "v2026.7.1" `
    -ExpectedBaseVersion "2026.7.1" `
    -ExpectedCorrection 1
Assert-CorrectionAccepted `
    -Name "skipped correction number still advances" `
    -Tag "v2026.7.1-9" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedBaseVersion "2026.7.1" `
    -ExpectedCorrection 9
Assert-CorrectionAccepted `
    -Name "multi-digit correction ordering is numeric" `
    -Tag "v2026.7.1-10" `
    -CurrentWindowsTag "v2026.7.1-9" `
    -ExpectedBaseVersion "2026.7.1" `
    -ExpectedCorrection 10

Write-Host "Validating same, older, and different-base rejection..."
Assert-CorrectionRejected `
    -Name "reuse of the published latest correction" `
    -Tag "v2026.7.1-2" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedMessage "must never be moved or reused"
Assert-CorrectionRejected `
    -Name "older correction on the same line" `
    -Tag "v2026.7.1-1" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedMessage "does not advance current Windows release"
Assert-CorrectionRejected `
    -Name "correction for an older release line" `
    -Tag "v2026.6.34-1" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedMessage "must correct the current Windows latest release line"
Assert-CorrectionRejected `
    -Name "correction for a newer release line" `
    -Tag "v2026.8.0-1" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedMessage "must correct the current Windows latest release line"
Assert-CorrectionRejected `
    -Name "correction for an unreleased patch line" `
    -Tag "v2026.7.2-1" `
    -CurrentWindowsTag "v2026.7.1-2" `
    -ExpectedMessage "must correct the current Windows latest release line"
Assert-CorrectionRejected `
    -Name "unparsable current latest release" `
    -Tag "v2026.7.1-3" `
    -CurrentWindowsTag "v2026.7.1-alpha.4" `
    -ExpectedMessage "has an unsupported format"

Write-Host "Validating correction tag format rejection..."
Assert-InvalidTagRejected `
    -Name "unsuffixed stable tag" `
    -Tag "v2026.7.1" `
    -CurrentWindowsTag "v2026.7.1"
Assert-InvalidTagRejected `
    -Name "alpha prerelease tag" `
    -Tag "v2026.7.1-alpha.4" `
    -CurrentWindowsTag "v2026.7.1"
Assert-InvalidTagRejected `
    -Name "zero correction suffix" `
    -Tag "v2026.7.1-0" `
    -CurrentWindowsTag "v2026.7.1"
Assert-InvalidTagRejected `
    -Name "leading-zero correction suffix" `
    -Tag "v2026.7.1-03" `
    -CurrentWindowsTag "v2026.7.1"
Assert-InvalidTagRejected `
    -Name "missing v prefix" `
    -Tag "2026.7.1-3" `
    -CurrentWindowsTag "v2026.7.1-2"

Write-Host (
    "Stable correction release validator regressions passed: " +
    "$script:acceptedCount accepted, $script:rejectedCount rejected, " +
    "$script:classificationCount workflow classifications."
) -ForegroundColor Green
$global:LASTEXITCODE = 0
