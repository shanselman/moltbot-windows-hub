<#
.SYNOPSIS
    Validates CI lane routing, stable gate, release, and proof contracts.
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
$workflowPath = Join-Path $repoRootPath ".github\workflows\ci.yml"
$codeQlWorkflowPath = Join-Path $repoRootPath ".github\workflows\codeql.yml"
$selectorPath = Join-Path $repoRootPath "scripts\Get-CiProofPoolRegressionDecision.ps1"
$runnerPath = Join-Path $repoRootPath "scripts\Invoke-CiTest.ps1"
$e2eRunnerPath = Join-Path $repoRootPath "scripts\Invoke-CiE2e.ps1"
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$codeQlWorkflow = Get-Content -LiteralPath $codeQlWorkflowPath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw
$e2eRunner = Get-Content -LiteralPath $e2eRunnerPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Text.Contains($Expected, [StringComparison]::Ordinal)) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Unexpected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Text.Contains($Unexpected, [StringComparison]::Ordinal)) {
        throw $Message
    }
}

function Get-JobBlock {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Text = $workflow
    )

    $escapedName = [regex]::Escape($Name)
    $startMatch = [regex]::Match($Text, "(?m)^  ${escapedName}:\r?$")
    if (-not $startMatch.Success) {
        throw "Could not find CI job '$Name'."
    }
    $jobHeadingPattern = [regex]::new("(?m)^  [a-zA-Z0-9_-]+:\r?$")
    $nextMatch = $jobHeadingPattern.Match(
        $Text,
        $startMatch.Index + $startMatch.Length)
    if (-not $nextMatch.Success) {
        return $Text.Substring($startMatch.Index)
    }
    return $Text.Substring(
        $startMatch.Index,
        $nextMatch.Index - $startMatch.Index)
}

function Get-StepBlock {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $startMatch = [regex]::Match($Text, "(?m)^    - name: ${escapedName}\r?$")
    if (-not $startMatch.Success) {
        throw "Could not find CI step '$Name'."
    }
    $stepHeadingPattern = [regex]::new("(?m)^    - (?:name:|uses:)")
    $nextMatch = $stepHeadingPattern.Match(
        $Text,
        $startMatch.Index + $startMatch.Length)
    if (-not $nextMatch.Success) {
        return $Text.Substring($startMatch.Index)
    }
    return $Text.Substring(
        $startMatch.Index,
        $nextMatch.Index - $startMatch.Index)
}

function Get-WorkflowTriggerBlock {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $startMatch = [regex]::Match($Text, "(?m)^  ${escapedName}:\r?$")
    if (-not $startMatch.Success) {
        throw "Could not find workflow trigger '$Name'."
    }

    $nextMatch = [regex]::Match(
        $Text.Substring($startMatch.Index + $startMatch.Length),
        "(?m)^(?:  [a-zA-Z0-9_-]+:|[a-zA-Z0-9_-]+:)\r?$")
    if (-not $nextMatch.Success) {
        return $Text.Substring($startMatch.Index)
    }

    return $Text.Substring(
        $startMatch.Index,
        $startMatch.Length + $nextMatch.Index)
}

function Get-InlineYamlList {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $match = [regex]::Match(
        $Text,
        "(?m)^\s+${escapedName}:\s*\[(?<values>[^\]]*)\]\s*$")
    if (-not $match.Success) {
        throw "Could not find inline YAML list '$Name'."
    }

    return @($match.Groups["values"].Value.Split(",") |
        ForEach-Object { $_.Trim().Trim('"', "'") })
}

function Get-YamlBlockList {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $match = [regex]::Match(
        $Text,
        "(?ms)^\s+${escapedName}:\s*\r?\n(?<items>(?:\s+-\s+.+\r?\n?)+)")
    if (-not $match.Success) {
        throw "Could not find YAML block list '$Name'."
    }

    return @([regex]::Matches($match.Groups["items"].Value, "(?m)^\s+-\s+['`"]?(?<value>.+?)['`"]?\s*$") |
        ForEach-Object { $_.Groups["value"].Value })
}

function Test-PathMatches {
    param(
        [Parameter(Mandatory)][string[]]$Patterns,
        [Parameter(Mandatory)][string]$Path
    )

    foreach ($pattern in $Patterns) {
        if ([System.Management.Automation.WildcardPattern]::new(
                $pattern,
                [System.Management.Automation.WildcardOptions]::None).IsMatch($Path)) {
            return $true
        }
    }

    return $false
}

function Test-CodeQlTrigger {
    param(
        [Parameter(Mandatory)][ValidateSet("push", "pull_request", "schedule", "workflow_dispatch")]
        [string]$EventName,
        [string]$RefName,
        [string]$Path
    )

    if ($EventName -in @("schedule", "workflow_dispatch")) {
        return $true
    }

    if ($EventName -eq "push") {
        if ($RefName.StartsWith("refs/tags/", [StringComparison]::Ordinal)) {
            return $codeQlPushTags.Count -gt 0
        }
        if ($RefName -notin $codeQlPushBranches) {
            return $false
        }
        return Test-PathMatches -Patterns $codeQlPushPaths -Path $Path
    }

    if ($RefName -notin $codeQlPullRequestBranches) {
        return $false
    }
    return Test-PathMatches -Patterns $codeQlPullRequestPaths -Path $Path
}

$codeQlPush = Get-WorkflowTriggerBlock -Text $codeQlWorkflow -Name "push"
$codeQlPullRequest = Get-WorkflowTriggerBlock -Text $codeQlWorkflow -Name "pull_request"
$codeQlPushBranches = @(Get-InlineYamlList -Text $codeQlPush -Name "branches")
$codeQlPushTags = @(Get-InlineYamlList -Text $codeQlPush -Name "tags")
$codeQlPushPaths = @(Get-YamlBlockList -Text $codeQlPush -Name "paths")
$codeQlPullRequestBranches = @(Get-InlineYamlList -Text $codeQlPullRequest -Name "branches")
$codeQlPullRequestPaths = @(Get-YamlBlockList -Text $codeQlPullRequest -Name "paths")

$expectedCodeQlPaths = @(
    ".github/codeql/**",
    ".github/workflows/**",
    "src/**",
    "tests/**",
    "*.sln",
    "*.slnx",
    "*.props",
    "*.targets",
    "global.json",
    "NuGet.Config",
    "package.json",
    "package-lock.json"
)
if (($codeQlPushBranches -join ",") -ne "main,master") {
    throw "CodeQL branch pushes must be limited to main and master."
}
if (($codeQlPushTags -join ",") -ne "**") {
    throw "CodeQL must preserve scanning for all tag pushes."
}
if (($codeQlPullRequestBranches -join ",") -ne "main,master") {
    throw "CodeQL pull requests must continue to target main and master."
}
if (($codeQlPushPaths -join "`n") -cne ($expectedCodeQlPaths -join "`n")) {
    throw "CodeQL push path filters changed unexpectedly."
}
if (($codeQlPullRequestPaths -join "`n") -cne ($expectedCodeQlPaths -join "`n")) {
    throw "CodeQL pull request path filters changed unexpectedly."
}
$codeQlTriggerCases = @(
    @{ Name = "feature branch code push"; Event = "push"; Ref = "feature/example"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $false },
    @{ Name = "main code push"; Event = "push"; Ref = "main"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $true },
    @{ Name = "master code push"; Event = "push"; Ref = "master"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $true },
    @{ Name = "release tag push"; Event = "push"; Ref = "refs/tags/v2026.9.3"; Path = "docs/release.md"; Expected = $true },
    @{ Name = "relevant pull request"; Event = "pull_request"; Ref = "main"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $true },
    @{ Name = "docs-only pull request"; Event = "pull_request"; Ref = "main"; Path = "docs/example.md"; Expected = $false },
    @{ Name = "agent-skill-only pull request"; Event = "pull_request"; Ref = "main"; Path = ".agents/skills/example/SKILL.md"; Expected = $false },
    @{ Name = "scheduled scan"; Event = "schedule"; Ref = ""; Path = ""; Expected = $true },
    @{ Name = "manual scan"; Event = "workflow_dispatch"; Ref = ""; Path = ""; Expected = $true }
)
foreach ($case in $codeQlTriggerCases) {
    $actual = Test-CodeQlTrigger `
        -EventName $case.Event `
        -RefName $case.Ref `
        -Path $case.Path
    if ($actual -ne $case.Expected) {
        throw "CodeQL trigger case '$($case.Name)' expected '$($case.Expected)' but got '$actual'."
    }
}
foreach ($requiredToken in @(
        "workflow_dispatch:",
        "schedule:",
        'cron: "29 6 * * 1"',
        "profile:",
        "default: all",
        "type: choice",
        'group: codeql-${{ github.workflow }}-',
        "cancel-in-progress: `${{ github.event_name == 'pull_request' }}",
        "actions: read",
        "contents: read",
        "security-events: write",
        'if [ "${{ vars.OPENCLAW_CODEQL_ADVANCED_SETUP }}" = "true" ]; then',
        "inputs.profile == 'csharp'",
        "languages: csharp",
        "build-mode: manual",
        "config-file: ./.github/codeql/codeql-csharp-security.yml",
        'category: "/codeql/csharp"',
        "inputs.profile == 'actions'",
        "languages: actions",
        "config-file: ./.github/codeql/codeql-actions-security.yml",
        'category: "/codeql/actions"'
    )) {
    Assert-Contains `
        -Text $codeQlWorkflow `
        -Expected $requiredToken `
        -Message "CodeQL workflow is missing required contract '$requiredToken'."
}
Assert-NotContains `
    -Text $codeQlWorkflow `
    -Unexpected "inputs.advanced_setup" `
    -Message "CodeQL advanced setup must not bypass the repository-variable gate."
foreach ($jobName in @("csharp", "actions")) {
    $codeQlJob = Get-JobBlock -Name $jobName -Text $codeQlWorkflow
    Assert-Contains `
        -Text $codeQlJob `
        -Expected "needs: advanced-setup" `
        -Message "CodeQL job '$jobName' must depend on the advanced-setup gate."
    Assert-Contains `
        -Text $codeQlJob `
        -Expected "needs.advanced-setup.outputs.enabled == 'true'" `
        -Message "CodeQL job '$jobName' must remain disabled until the repository variable enables advanced setup."
}
$codeQlGateJob = Get-JobBlock -Name "advanced-setup" -Text $codeQlWorkflow
Assert-Contains `
    -Text $codeQlGateJob `
    -Expected 'echo "enabled=false" >> "$GITHUB_OUTPUT"' `
    -Message "CodeQL gate must explicitly disable advanced analysis when the repository variable is not true."

Assert-Contains `
    -Text $workflow `
    -Expected "group: `${{ github.workflow }}-`${{ github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || format('{0}-{1}', github.ref, github.sha) }}" `
    -Message "CI concurrency must group PR runs by PR number and push/tag runs by ref and SHA."
Assert-Contains `
    -Text $workflow `
    -Expected "cancel-in-progress: `${{ github.event_name == 'pull_request' }}" `
    -Message "CI cancellation must apply only to pull_request runs."

$classificationJob = Get-JobBlock "change-classification"
foreach ($token in @(
        "fetch-depth: 0",
        "./scripts/Get-CiChangeClassification.ps1",
        "classification: `${{ steps.classify.outputs.classification }}",
        "core_tests: `${{ steps.classify.outputs.core_tests }}",
        "tray_tests: `${{ steps.classify.outputs.tray_tests }}",
        "ui_tests: `${{ steps.classify.outputs.ui_tests }}",
        "setup_e2e: `${{ steps.classify.outputs.setup_e2e }}",
        "revocation_e2e: `${{ steps.classify.outputs.revocation_e2e }}",
        "network_e2e: `${{ steps.classify.outputs.network_e2e }}",
        "x64_release: `${{ steps.classify.outputs.x64_release }}",
        "arm64_release: `${{ steps.classify.outputs.arm64_release }}",
        "full: `${{ steps.classify.outputs.full }}"
    )) {
    Assert-Contains `
        -Text $classificationJob `
        -Expected $token `
        -Message "Change classification job is missing '$token'."
}

$fastValidationJob = Get-JobBlock "fast-validation"
foreach ($token in @(
        "Ensure .squad stays untracked",
        "node --test .github/scripts/repository-triage.test.cjs",
        "./scripts/validate-docs.ps1",
        "./scripts/validate-agent-skills.ps1",
        "./scripts/test-agent-skills-validator.ps1",
        "./scripts/test-ci-change-classifier.ps1",
        "./scripts/test-ci-gate-results.ps1",
        "./scripts/test-ci-workflow-contract.ps1",
        "./scripts/test-stable-correction-release-validator.ps1"
    )) {
    Assert-Contains `
        -Text $fastValidationJob `
        -Expected $token `
        -Message "Always-running fast validation job is missing '$token'."
}

$proofJob = Get-JobBlock "proof-pool-contracts"
foreach ($token in @(
        "./scripts/Get-CiProofPoolRegressionDecision.ps1",
        "steps.proof_pool_regression.outcome != 'success' || steps.proof_pool_regression.outputs.run != 'false'",
        "./scripts/test-proof-pool-validator.ps1"
    )) {
    Assert-Contains `
        -Text $proofJob `
        -Expected $token `
        -Message "Dedicated proof-pool contract job is missing '$token'."
}
Assert-NotContains `
    -Text $fastValidationJob `
    -Unexpected "./scripts/test-proof-pool-validator.ps1" `
    -Message "The heavyweight malformed proof-contract matrix must not block fast validation."

$testLanes = [ordered]@{
    "core-tests" = @{
        Output = "core_tests"
        Projects = @(
            "tests/OpenClaw.Shared.Tests",
            "tests/OpenClaw.Connection.Tests",
            "tests/OpenClaw.WinNode.Cli.Tests"
        )
        Artifact = "test-results-core"
    }
    "tray-tests" = @{
        Output = "tray_tests"
        Projects = @(
            "tests/OpenClaw.Tray.Tests",
            "tests/OpenClaw.SetupEngine.Tests",
            "tests/OpenClaw.Tray.IntegrationTests"
        )
        Artifact = "test-results-tray"
    }
    "ui-tests" = @{
        Output = "ui_tests"
        Projects = @(
            "tests/OpenClaw.Tray.UITests",
            "tests/OpenClawTray.FunctionalUI.Tests"
        )
        Artifact = "test-results-ui"
    }
}
foreach ($lane in $testLanes.GetEnumerator()) {
    $job = Get-JobBlock $lane.Key
    Assert-Contains `
        -Text $job `
        -Expected "needs.change-classification.outputs.$($lane.Value.Output) == 'true'" `
        -Message "Test lane '$($lane.Key)' does not consume its classifier output."
    Assert-Contains `
        -Text $job `
        -Expected "fetch-depth: 0" `
        -Message "Test lane '$($lane.Key)' must fetch full history for GitVersion.MsBuild."
    Assert-Contains `
        -Text $job `
        -Expected "OPENCLAW_CI_COLLECT_COVERAGE: `${{ github.event_name != 'pull_request' }}" `
        -Message "Test lane '$($lane.Key)' does not preserve push/tag coverage."
    Assert-Contains `
        -Text $job `
        -Expected "name: $($lane.Value.Artifact)" `
        -Message "Test lane '$($lane.Key)' is missing its TRX artifact."
    foreach ($project in $lane.Value.Projects) {
        Assert-Contains `
            -Text $job `
            -Expected $project `
            -Message "Test lane '$($lane.Key)' lost project '$project'."
    }
}

$coreJob = Get-JobBlock "core-tests"
$coreCacheStep = Get-StepBlock -Text $coreJob -Name "Cache NuGet packages"
foreach ($token in @(
        "Configure core NuGet cache",
        "shell: pwsh",
        '"NUGET_PACKAGES=$env:RUNNER_TEMP\openclaw-core-nuget-packages" >> $env:GITHUB_ENV',
        'path: ${{ env.NUGET_PACKAGES }}',
        'nuget-core-${{ runner.os }}-${{ hashFiles(',
        "'global.json'",
        "'NuGet.Config'",
        "'Directory.Build.props'",
        "'Directory.Build.targets'",
        "'src/Directory.Build.props'",
        "'src/Directory.Build.targets'",
        "'tests/Directory.Build.props'",
        "'tests/OpenClaw.TestSupport/Directory.Build.props'",
        "'tests/OpenClaw.Shared.TestHost/Directory.Build.props'",
        "'src/OpenClaw.Shared/OpenClaw.Shared.csproj'",
        "'src/OpenClaw.Connection/OpenClaw.Connection.csproj'",
        "'src/OpenClaw.Cli/OpenClaw.Cli.csproj'",
        "'src/OpenClaw.WinNode.Cli/OpenClaw.WinNode.Cli.csproj'",
        "'tests/OpenClaw.TestSupport/OpenClaw.TestSupport.csproj'",
        "'tests/OpenClaw.Shared.TestHost/OpenClaw.Shared.TestHost.csproj'",
        "'tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj'",
        "'tests/OpenClaw.Connection.Tests/OpenClaw.Connection.Tests.csproj'",
        "'tests/OpenClaw.WinNode.Cli.Tests/OpenClaw.WinNode.Cli.Tests.csproj'"
    )) {
    Assert-Contains -Text $coreJob -Expected $token -Message "Core NuGet cache is missing '$token'."
}
foreach ($token in @(
        "path: ~/.nuget/packages",
        'key: nuget-${{ runner.os }}-',
        'restore-keys: nuget-${{ runner.os }}-',
        "'**/*.csproj'",
        "'**/Directory.Packages.props'",
        "bin/",
        "obj/",
        "TestResults",
        "node_modules",
        ".dotnet"
    )) {
    Assert-NotContains -Text $coreCacheStep -Unexpected $token -Message "Core NuGet cache must not contain '$token'."
}

$coreJobIndex = $workflow.IndexOf($coreJob, [StringComparison]::Ordinal)
if ($coreJobIndex -lt 0) {
    throw "Could not isolate the core job from the workflow."
}
$nonCoreWorkflow = $workflow.Remove($coreJobIndex, $coreJob.Length)
foreach ($token in @(
        "path: ~/.nuget/packages",
        'key: nuget-${{ runner.os }}-${{ hashFiles(''**/*.csproj'', ''**/Directory.Packages.props'') }}',
        'restore-keys: nuget-${{ runner.os }}-'
    )) {
    $count = ([regex]::Matches($nonCoreWorkflow, [regex]::Escape($token))).Count
    if ($count -ne 8) {
        throw "Expected 8 unchanged non-core cache entries for '$token', found $count."
    }
}
Assert-NotContains `
    -Text $nonCoreWorkflow `
    -Unexpected "NUGET_PACKAGES" `
    -Message "Only the core job may override NUGET_PACKAGES."
Assert-NotContains `
    -Text $nonCoreWorkflow `
    -Unexpected "nuget-core-" `
    -Message "Only the core job may use the isolated NuGet cache key."

$trayJob = Get-JobBlock "tray-tests"
foreach ($token in @(
        "OPENCLAW_TRAY_DATA_DIR:",
        "OPENCLAW_RUN_INTEGRATION: 1",
        "-Project tests/OpenClaw.Tray.IntegrationTests",
        "dotnet restore src/OpenClaw.Tray.WinUI -r win-x64",
        "dotnet build src/OpenClaw.Tray.WinUI -c Debug -r win-x64 --no-restore"
    )) {
    Assert-Contains -Text $trayJob -Expected $token -Message "Tray lane is missing '$token'."
}

$uiJob = Get-JobBlock "ui-tests"
foreach ($token in @(
        "Install WindowsAppRuntime",
        "-Filter Category!=Accessibility",
        "--filter Category=Accessibility",
        "Verify DevBuild identity marker"
    )) {
    Assert-Contains -Text $uiJob -Expected $token -Message "UI lane is missing '$token'."
}

$runnerUses = [regex]::Matches(
    $workflow,
    "(?m)^\s+\./scripts/Invoke-CiTest\.ps1\s*$").Count
if ($runnerUses -ne 8) {
    throw "Expected all 8 non-E2E test projects to use Invoke-CiTest.ps1, found $runnerUses."
}
if ($workflow -match "(?m)^\s+dotnet-coverage collect\s*$") {
    throw "Workflow test steps must not invoke dotnet-coverage directly."
}
foreach ($requiredRunnerToken in @(
        '"--logger"',
        '"trx;LogFileName=$TrxFileName"',
        'dotnet-coverage collect',
        '--output-format cobertura',
        '& dotnet @testArguments'
    )) {
    Assert-Contains `
        -Text $runner `
        -Expected $requiredRunnerToken `
        -Message "CI test runner is missing required token '$requiredRunnerToken'."
}

$e2eLanes = [ordered]@{
    "setup-e2e" = @{
        Output = "setup_e2e"
        Name = "setup-connect"
        Filter = "OpenClaw.E2ETests.Setup.SetupAndConnectTests"
    }
    "revocation-e2e" = @{
        Output = "revocation_e2e"
        Name = "revocation-recovery"
        Filter = "OpenClaw.E2ETests.Setup.RevocationAndRecoveryTests"
    }
    "network-e2e" = @{
        Output = "network_e2e"
        Name = "network-recovery"
        Filter = "OpenClaw.E2ETests.Setup.NetworkRecoveryTests"
    }
}
foreach ($lane in $e2eLanes.GetEnumerator()) {
    $job = Get-JobBlock $lane.Key
    foreach ($token in @(
            "fetch-depth: 0",
            "needs.change-classification.outputs.$($lane.Value.Output) == 'true'",
            "OPENCLAW_RUN_E2E: 1",
            "./scripts/Invoke-CiE2e.ps1",
            "-Name $($lane.Value.Name)",
            $lane.Value.Filter,
            "TestResults/E2E/"
        )) {
        Assert-Contains `
            -Text $job `
            -Expected $token `
            -Message "E2E lane '$($lane.Key)' is missing '$token'."
    }
}
foreach ($proofName in @(
        "RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox",
        "RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox",
        "UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing",
        "InitialHandshakeListenerReplacementWithholdsCredentialFrame",
        "InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame"
    )) {
    Assert-Contains `
        -Text $e2eRunner `
        -Expected $proofName `
        -Message "E2E runner lost proof assertion '$proofName'."
}
foreach ($skipReasonToken in @(
        '$mxcProof.SelectSingleNode("Output/ErrorInfo/Message")',
        '$mxcProof.SelectSingleNode("Output/StdOut")',
        '$null -ne $_'
    )) {
    Assert-Contains `
        -Text $e2eRunner `
        -Expected $skipReasonToken `
        -Message "E2E runner lost null-safe MXC skip-reason handling '$skipReasonToken'."
}

$metadataJob = Get-JobBlock "metadata"
foreach ($token in @(
        "needs: change-classification",
        "fetch-depth: 0",
        "outputs.x64_release == 'true' || needs.change-classification.outputs.arm64_release == 'true'",
        "gittools/actions/gitversion/setup@v4",
        "gittools/actions/gitversion/execute@v4",
        "semVer: `${{ steps.release_version.outputs.semVer }}",
        "majorMinorPatch: `${{ steps.release_version.outputs.majorMinorPatch }}",
        "isPrerelease: `${{ steps.release_version.outputs.isPrerelease }}",
        "isStableCorrection: `${{ steps.release_version.outputs.isStableCorrection }}",
        "Test-OpenClawStableCorrectionRelease.ps1"
    )) {
    Assert-Contains -Text $metadataJob -Expected $token -Message "Metadata job is missing '$token'."
}
Assert-NotContains `
    -Text $metadataJob `
    -Unexpected "fast-validation" `
    -Message "Release metadata must start independently of validation lanes."

$releaseBuilds = [ordered]@{
    "build-x64" = @{
        Output = "x64_release"
        Runtime = "win-x64"
        Runner = "windows-latest"
        Native = "Test-ReleaseNativeDependencies.ps1 -PayloadPath publish -RequireAppLocalVCRuntime"
    }
    "build-arm64" = @{
        Output = "arm64_release"
        Runtime = "win-arm64"
        Runner = "windows-11-arm"
        Native = "Test-ReleaseNativeDependencies.ps1 -PayloadPath publish -RequireAppLocalVCRuntime -SkipNativeLoadProbe"
    }
}
foreach ($build in $releaseBuilds.GetEnumerator()) {
    $job = Get-JobBlock $build.Key
    foreach ($token in @(
            "needs: [change-classification, metadata]",
            "fetch-depth: 0",
            "needs.change-classification.outputs.$($build.Value.Output) == 'true'",
            "runs-on: $($build.Value.Runner)",
            "OPENCLAW_BUILD_VERSION: `${{ needs.metadata.outputs.semVer }}",
            "dotnet publish src/OpenClaw.Tray.WinUI -c Release -r $($build.Value.Runtime) --self-contained --no-restore",
            $build.Value.Native,
            'Verify GitVersion assembly metadata'
        )) {
        Assert-Contains -Text $job -Expected $token -Message "Release build '$($build.Key)' is missing '$token'."
    }
    Assert-NotContains `
        -Text $job `
        -Unexpected "core-tests" `
        -Message "Release builds must start in parallel with tests and E2E."
}

$buildMsixJob = Get-JobBlock "build-msix"
Assert-Contains `
    -Text $buildMsixJob `
    -Expected "fetch-depth: 0" `
    -Message "The paused MSIX build must retain full history before it can be re-enabled."

$ciGateJob = Get-JobBlock "ci-gate"
foreach ($token in @(
        "name: CI Gate",
        "if: `${{ always() }}",
        "needs: [change-classification, fast-validation, proof-pool-contracts, metadata, core-tests, tray-tests, ui-tests, setup-e2e, revocation-e2e, network-e2e, build-x64, build-arm64]",
        "./scripts/Assert-CiGateResults.ps1",
        "-FullRequired `$env:FULL_REQUIRED",
        "-CoreRequired `$env:CORE_REQUIRED",
        "-TrayRequired `$env:TRAY_REQUIRED",
        "-UiRequired `$env:UI_REQUIRED",
        "-SetupE2eRequired `$env:SETUP_E2E_REQUIRED",
        "-RevocationE2eRequired `$env:REVOCATION_E2E_REQUIRED",
        "-NetworkE2eRequired `$env:NETWORK_E2E_REQUIRED",
        "-X64ReleaseRequired `$env:X64_RELEASE_REQUIRED",
        "-Arm64ReleaseRequired `$env:ARM64_RELEASE_REQUIRED",
        "-MetadataResult `$env:METADATA_RESULT"
    )) {
    Assert-Contains -Text $ciGateJob -Expected $token -Message "Stable CI Gate is missing '$token'."
}

$releaseJob = Get-JobBlock "release"
foreach ($token in @(
        "needs: [change-classification, metadata, build-x64, build-arm64, ci-gate]",
        "needs.ci-gate.result == 'success'",
        "needs.metadata.outputs.semVer",
        "needs.metadata.outputs.isPrerelease",
        "needs.metadata.outputs.isStableCorrection"
    )) {
    Assert-Contains -Text $releaseJob -Expected $token -Message "Tag release is missing '$token'."
}

$triggerPaths = @(
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
)

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "openclaw-ci-contract-" + [guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    & git -C $tempRoot init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Could not initialize temporary git repository."
    }
    & git -C $tempRoot config user.email "ci-contract@example.invalid"
    & git -C $tempRoot config user.name "CI Contract"

    foreach ($triggerPath in $triggerPaths) {
        $fullPath = Join-Path $tempRoot ($triggerPath.Replace("/", "\"))
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
        Set-Content -LiteralPath $fullPath -Value "baseline"
    }
    $unrelatedPath = Join-Path $tempRoot "docs\unrelated.md"
    New-Item -ItemType Directory -Path (Split-Path -Parent $unrelatedPath) -Force | Out-Null
    Set-Content -LiteralPath $unrelatedPath -Value "baseline"

    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "baseline"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not commit temporary baseline."
    }

    foreach ($triggerPath in $triggerPaths) {
        $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        Add-Content `
            -LiteralPath (Join-Path $tempRoot ($triggerPath.Replace("/", "\"))) `
            -Value "changed"
        & git -C $tempRoot add .
        & git -C $tempRoot commit --quiet -m "change $triggerPath"
        $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        $decision = & $selectorPath `
            -EventName pull_request `
            -BaseSha $baseSha `
            -HeadSha $headSha `
            -RepoRoot $tempRoot
        if ($decision -ne "true") {
            throw "Proof-pool trigger '$triggerPath' produced decision '$decision'."
        }
    }

    $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()
    Add-Content -LiteralPath $unrelatedPath -Value "changed"
    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "unrelated change"
    $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
    $unrelatedDecision = & $selectorPath `
        -EventName pull_request `
        -BaseSha $baseSha `
        -HeadSha $headSha `
        -RepoRoot $tempRoot
    if ($unrelatedDecision -ne "false") {
        throw "Unrelated PR change produced decision '$unrelatedDecision'."
    }

    $missingDiffDecision = & $selectorPath `
        -EventName pull_request `
        -BaseSha "missing-base" `
        -HeadSha $headSha `
        -RepoRoot $tempRoot
    if ($missingDiffDecision -ne "true") {
        throw "Undetermined PR diff must run the regression fail-closed."
    }

    $pushDecision = & $selectorPath `
        -EventName push `
        -RepoRoot $tempRoot
    if ($pushDecision -ne "true") {
        throw "Push and tag workflow runs must run the proof-pool regression."
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "CI workflow contracts passed: CodeQL branch/path gating, conservative lane routing, isolated core NuGet caching, stable gate enforcement, decoupled metadata/release builds, preserved test inventory, and fail-closed proof routing." -ForegroundColor Green
