<#
.SYNOPSIS
    Exercises agent skill validator regressions.
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
$validatorPath = Join-Path $repoRootPath "scripts\validate-agent-skills.ps1"
$childPowerShell = (Get-Command pwsh -ErrorAction SilentlyContinue)
if ($null -eq $childPowerShell) {
    $childPowerShell = Get-Command powershell.exe -ErrorAction Stop
}

function Invoke-Validator([string]$Root) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = (& $childPowerShell.Source `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $validatorPath `
            -RepoRoot $Root 2>&1 | Out-String)
        return @{
            ExitCode = $LASTEXITCODE
            Output = $output
        }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "openclaw-skill-validator-" + [guid]::NewGuid().ToString("N"))
try {
    $skillRoot = Join-Path $tempRoot ".agents\skills\example"
    New-Item -ItemType Directory -Path $skillRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $skillRoot "reference.md") -Value "# Reference"
    $skillPath = Join-Path $skillRoot "SKILL.md"
    Set-Content -LiteralPath $skillPath -Value @"
---
name: example
description: Example validator fixture.
---

# Example

See [reference](reference.md).
"@

    $validResult = Invoke-Validator $tempRoot
    if ($validResult.ExitCode -ne 0) {
        throw "Valid skill fixture failed validation: $($validResult.Output)"
    }

    (Get-Content -LiteralPath $skillPath -Raw).Replace(
        "name: example",
        "name: wrong") | Set-Content -LiteralPath $skillPath
    $nameResult = Invoke-Validator $tempRoot
    if ($nameResult.ExitCode -eq 0 -or
        $nameResult.Output -notmatch "must match the directory name") {
        throw "Skill name mismatch was not rejected: $($nameResult.Output)"
    }

    (Get-Content -LiteralPath $skillPath -Raw).Replace(
        "name: wrong",
        "name: example").Replace(
        "reference.md",
        "missing.md") | Set-Content -LiteralPath $skillPath
    $linkResult = Invoke-Validator $tempRoot
    if ($linkResult.ExitCode -eq 0 -or
        $linkResult.Output -notmatch "local link target does not exist") {
        throw "Missing skill link was not rejected: $($linkResult.Output)"
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "Agent skill validator regressions passed: valid metadata and broken name/link cases." -ForegroundColor Green
$global:LASTEXITCODE = 0
exit 0
