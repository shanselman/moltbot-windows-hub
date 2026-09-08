<#
.SYNOPSIS
    Runs a CI test project with optional push/tag coverage collection.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$TrxFileName,
    [string]$Runtime,
    [string]$Filter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$testArguments = [System.Collections.Generic.List[string]]::new()
@(
    "test",
    $Project,
    "--no-build",
    "-c",
    "Debug"
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $testArguments.Add("-r")
    $testArguments.Add($Runtime)
}

@(
    "--verbosity",
    "normal",
    "--results-directory",
    $ResultsDirectory,
    "--logger",
    "trx;LogFileName=$TrxFileName"
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArguments.Add("--filter")
    $testArguments.Add($Filter)
}

$collectCoverage = $env:OPENCLAW_CI_COLLECT_COVERAGE -eq "true"
if ($collectCoverage) {
    $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
    & dotnet-coverage collect `
        --output $coverageOutput `
        --output-format cobertura `
        dotnet @testArguments
} else {
    & dotnet @testArguments
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
