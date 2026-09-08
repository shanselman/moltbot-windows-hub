<#
.SYNOPSIS
    Runs one existing CI E2E shard and enforces its TRX proof contract.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("setup-connect", "revocation-recovery", "network-recovery")]
    [string]$Name,
    [Parameter(Mandatory)][string]$Filter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resultsDirectory = "TestResults\E2E"
$trxPath = Join-Path $resultsDirectory "OpenClaw.E2ETests.$Name.trx"
dotnet test tests/OpenClaw.E2ETests `
    --no-build `
    -c Debug `
    -r win-x64 `
    --verbosity normal `
    --results-directory $resultsDirectory `
    --logger "trx;LogFileName=OpenClaw.E2ETests.$Name.trx" `
    --logger "console;verbosity=detailed" `
    --filter $Filter
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

[xml]$trx = Get-Content $trxPath
$executed = [int]$trx.TestRun.ResultSummary.Counters.executed
if ($executed -lt 1) {
    throw "E2E shard '$Name' executed zero tests. Check OPENCLAW_RUN_E2E gating/filter before merging."
}

if ($Name -ne "setup-connect") {
    return
}

$mxcProofNames = @(
    "RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox",
    "RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox"
)
foreach ($mxcProofName in $mxcProofNames) {
    $mxcProof = @(
        $trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.testName -like "*$mxcProofName*" }
    ) | Select-Object -First 1
    if ($null -eq $mxcProof) {
        throw "E2E shard '$Name' did not report the MXC proof test '$mxcProofName'. Check the setup-connect filter before merging."
    }

    $mxcOutcome = [string]$mxcProof.outcome
    if ($mxcOutcome -eq "Passed") {
        Write-Host "MXC E2E proof passed: $mxcProofName"
    } elseif ($mxcOutcome -eq "NotExecuted" -or $mxcOutcome -eq "Skipped") {
        $mxcSkipReason = @(
            $mxcProof.SelectSingleNode("Output/ErrorInfo/Message")
            $mxcProof.SelectSingleNode("Output/StdOut")
        ) |
            Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace($_.InnerText) } |
            ForEach-Object { $_.InnerText.Trim() } |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($mxcSkipReason)) {
            $mxcSkipReason = "skip reason was not present in the trx output"
        }
        Write-Warning "MXC E2E proof skipped: $mxcProofName; $mxcSkipReason"
    } else {
        throw "MXC E2E proof '$mxcProofName' had unexpected outcome '$mxcOutcome'."
    }
}

$sshOwnershipProofNames = @(
    "UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing",
    "InitialHandshakeListenerReplacementWithholdsCredentialFrame",
    "InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame"
)
foreach ($sshOwnershipProofName in $sshOwnershipProofNames) {
    $sshOwnershipProof = @(
        $trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.testName -like "*$sshOwnershipProofName*" }
    ) | Select-Object -First 1
    if ($null -eq $sshOwnershipProof) {
        throw "E2E shard '$Name' did not report the SSH ownership proof test '$sshOwnershipProofName'. Check the setup-connect filter before merging."
    }

    $sshOwnershipOutcome = [string]$sshOwnershipProof.outcome
    if ($sshOwnershipOutcome -ne "Passed") {
        throw "SSH ownership E2E proof '$sshOwnershipProofName' had outcome '$sshOwnershipOutcome'; this proof must pass and may not skip."
    }
    Write-Host "SSH ownership E2E proof passed: $sshOwnershipProofName"
}
