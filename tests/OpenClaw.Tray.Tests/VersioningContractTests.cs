using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class VersioningContractTests
{
    private static readonly Regex ProjectVersionElement = new(
        @"<Version>\s*\d+\.\d+\.\d+[^<]*</Version>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsGeneratedOrIgnoredPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("obj", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("node_modules", StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductProjects_DoNotHardcodeReleaseVersion()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var projectFiles = Directory.EnumerateFiles(
            Path.Combine(repoRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        var offenders = projectFiles
            .Where(path => ProjectVersionElement.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .OrderBy(path => path)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Product project files must not hardcode release versions. Offenders:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void GitVersion_MainBranchPreservesAlphaReleaseTags()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "GitVersion.yml");
        var config = File.ReadAllText(configPath);
        var mainBranchMatch = Regex.Match(
            config,
            @"(?ms)^  main:\s*$\r?\n(?<body>.*?)(?=^  \S|\z)",
            RegexOptions.CultureInvariant);

        Assert.True(mainBranchMatch.Success, "GitVersion.yml must configure the main branch.");
        Assert.Matches(
            @"(?m)^\s+label:\s*'?alpha'?\s*$",
            mainBranchMatch.Groups["body"].Value);
    }

    [Fact]
    public void DailyAlphaRelease_IsChangeGatedAndDispatchesTheValidatedReleasePipeline()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var dailyWorkflow = File.ReadAllText(Path.Combine(
            repoRoot,
            ".github",
            "workflows",
            "daily-alpha-release.yml"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(
            repoRoot,
            ".github",
            "workflows",
            "ci.yml"));

        Assert.Contains("cron: '0 21 * * *'", dailyWorkflow);
        Assert.Contains("cron: '0 22 * * *'", dailyWorkflow);
        Assert.DoesNotContain("timezone:", dailyWorkflow);
        Assert.Contains("TZ=America/Los_Angeles date +%z", dailyWorkflow);
        Assert.Contains("-0700) expected_schedule='0 21 * * *'", dailyWorkflow);
        Assert.Contains("-0800) expected_schedule='0 22 * * *'", dailyWorkflow);
        Assert.Contains("SCHEDULE: ${{ github.event.schedule }}", dailyWorkflow);
        Assert.Contains("steps.pacific_schedule.outputs.run == 'true'", dailyWorkflow);
        Assert.Contains("actions: write", dailyWorkflow);
        Assert.Contains("contents: write", dailyWorkflow);
        Assert.Contains(
            "uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1",
            dailyWorkflow);
        Assert.Contains(
            "uses: gittools/actions/gitversion/setup@7417b1089e2c7de93510f1901d656ddf60bb024f # v4.7.0",
            dailyWorkflow);
        Assert.Contains(
            "uses: gittools/actions/gitversion/execute@7417b1089e2c7de93510f1901d656ddf60bb024f # v4.7.0",
            dailyWorkflow);
        Assert.DoesNotContain("uses: actions/checkout@v", dailyWorkflow);
        Assert.DoesNotContain("uses: gittools/actions/gitversion/setup@v", dailyWorkflow);
        Assert.DoesNotContain("uses: gittools/actions/gitversion/execute@v", dailyWorkflow);
        Assert.Contains("ref: ${{ github.event.repository.default_branch }}", dailyWorkflow);
        Assert.DoesNotContain("workflow_dispatch:", dailyWorkflow);
        Assert.Contains("head_non_alpha_tag", dailyWorkflow);
        Assert.Contains("published_head_tag", dailyWorkflow);
        Assert.Contains("Deferring alpha release because main already has unpublished non-alpha tag", dailyWorkflow);
        Assert.Contains("steps.previous_release.outputs.changed == 'true'", dailyWorkflow);
        Assert.Contains("versionSpec: '6.8.x'", dailyWorkflow);
        Assert.Contains("-alpha\\.[0-9]+", dailyWorkflow);
        Assert.Contains("latest_alpha_tag", dailyWorkflow);
        Assert.Contains("git tag --merged HEAD", dailyWorkflow);
        Assert.Contains("sort -V", dailyWorkflow);
        Assert.Contains("which is not newer than", dailyWorkflow);
        Assert.Contains("git push origin \"refs/tags/$tag\"", dailyWorkflow);
        Assert.Contains("gh workflow run ci.yml --ref \"$ALPHA_TAG\"", dailyWorkflow);
        Assert.Contains("workflow_dispatch:", releaseWorkflow);
        Assert.Contains("uses: softprops/action-gh-release@v3", releaseWorkflow);
        Assert.Contains("prerelease: ${{ needs.metadata.outputs.isPrerelease }}", releaseWorkflow);
        Assert.Contains(
            "if: needs.metadata.outputs.isPrerelease == 'true' && contains(github.ref_name, '-alpha.')",
            releaseWorkflow);
        Assert.Contains("Retain alpha releases for 30 days", releaseWorkflow);
        Assert.Contains("now - (30 * 24 * 60 * 60)", releaseWorkflow);
        Assert.Contains("retaining its Git tag", releaseWorkflow);
        Assert.Contains("for attempt in 1 2 3", releaseWorkflow);
        Assert.Contains("if gh api --method DELETE", releaseWorkflow);
        Assert.Contains("will retry after the next alpha publication", releaseWorkflow);
        Assert.Contains("gh api --method DELETE", releaseWorkflow);
    }

    [Fact]
    public void ReleaseWorkflow_TreatsNumericCorrectionsAsStableExactVersions()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var workflow = File.ReadAllText(
            Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("- name: Resolve release version", workflow);
        Assert.Contains("versionSpec: '6.8.x'", workflow);
        Assert.DoesNotContain("versionSpec: '6.4.x'", workflow);
        Assert.Contains(
            "'^(?<base>(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*))-(?<revision>\\d+)$'",
            workflow);
        Assert.Contains("$semVer = $tagVersion", workflow);
        Assert.Contains("$isPrerelease = $false", workflow);
        Assert.Contains(
            ".\\scripts\\Test-OpenClawStableCorrectionRelease.ps1",
            workflow);
        Assert.Contains(
            "isStableCorrection: ${{ steps.release_version.outputs.isStableCorrection }}",
            workflow);
        Assert.Contains(
            "group: openclaw-windows-node-release",
            workflow);
        Assert.Contains(
            "- name: Revalidate stable correction release ordering",
            workflow);
        Assert.Contains(
            "if: needs.metadata.outputs.isStableCorrection == 'true'",
            workflow);
        Assert.Contains(
            "semVer: ${{ steps.release_version.outputs.semVer }}",
            workflow);
        Assert.Contains(
            "isPrerelease: ${{ steps.release_version.outputs.isPrerelease }}",
            workflow);
        Assert.Contains(
            "-p:Version=$env:OPENCLAW_BUILD_VERSION",
            workflow);
        Assert.Contains(
            "prerelease: ${{ needs.metadata.outputs.isPrerelease }}",
            workflow);
        Assert.DoesNotContain(
            "prerelease: ${{ contains(github.ref_name, '-') }}",
            workflow);
    }

    [Fact]
    public void BuildScript_PreflightsGitVersionRepositoryHistory()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(repoRoot, "build.ps1"));

        Assert.Contains("Git metadata not found. GitVersion requires a git clone with full history.", buildScript);
        Assert.Contains("rev-parse --is-shallow-repository", buildScript);
        Assert.Contains("GitVersion requires full git history", buildScript);
        Assert.Contains("git fetch --unshallow --tags origin", buildScript);
    }

    [Fact]
    public void LocalInstallerBuild_PreservesExplicitInformationalVersion()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var buildScript = File.ReadAllText(
            Path.Combine(repoRoot, "scripts", "build-inno-local.ps1"));

        Assert.Contains(
            "$trayPublishArgs += \"-p:Version=$PublishVersion\"",
            buildScript);
        Assert.Contains(
            "$trayPublishArgs += \"-p:InformationalVersion=$PublishVersion\"",
            buildScript);
    }

    [Fact]
    public void StableCorrectionValidator_HasNoUpstreamReleaseApiDependency()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var validator = File.ReadAllText(Path.Combine(
            repoRoot,
            "scripts",
            "Test-OpenClawStableCorrectionRelease.ps1"));

        Assert.DoesNotContain("repos/openclaw/openclaw/", validator);
        Assert.DoesNotContain("releases/tags/$Tag", validator);
        Assert.Contains(
            "repos/openclaw/openclaw-windows-node/releases/latest",
            validator);
        Assert.Contains("[string]$CurrentWindowsTag", validator);
    }

    [Fact]
    public void StableCorrectionValidator_RequiresSameLineMonotonicCorrection()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var validator = File.ReadAllText(Path.Combine(
            repoRoot,
            "scripts",
            "Test-OpenClawStableCorrectionRelease.ps1"));

        Assert.Contains(
            "'^v(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)-[1-9]\\d*$'",
            validator);
        Assert.Contains("function Assert-SameLineStableCorrection", validator);
        Assert.Contains("if ($candidateBase -ne $currentBase)", validator);
        Assert.Contains("if ($candidateParts[3] -eq $currentParts[3])", validator);
        Assert.Contains("if ($candidateParts[3] -lt $currentParts[3])", validator);
        Assert.Contains("must never be moved or reused", validator);
        Assert.Contains("function Assert-WindowsReleaseTagUnpublished", validator);
        Assert.Contains("is already a published Windows release", validator);
        Assert.Contains("is a prerelease; refusing to order a correction against it", validator);
        Assert.DoesNotContain("function Assert-NewerStableRelease", validator);
    }

    [Fact]
    public void ReleaseWorkflow_RevalidatesCorrectionOrderingAfterSigningBeforePublish()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var workflow = File.ReadAllText(
            Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));

        var signIndex = workflow.IndexOf("- name: Sign Installers", StringComparison.Ordinal);
        var revalidateIndex = workflow.IndexOf(
            "- name: Revalidate stable correction release ordering",
            StringComparison.Ordinal);
        var publishIndex = workflow.IndexOf("- name: Create Release", StringComparison.Ordinal);

        Assert.True(signIndex >= 0, "Release workflow must sign installers.");
        Assert.True(revalidateIndex > signIndex, "Correction ordering must be revalidated after signing.");
        Assert.True(publishIndex > revalidateIndex, "Correction ordering must be revalidated before publication.");

        Assert.Contains(
            "make_latest: ${{ needs.metadata.outputs.isPrerelease == 'true' && 'false' || 'true' }}",
            workflow);
        Assert.Contains("$majorMinorPatch = $validation.BaseVersion", workflow);
        Assert.Contains(
            "./scripts/test-stable-correction-release-validator.ps1",
            workflow);
    }

    [Fact]
    public void ActiveCodeAndTests_DoNotContainStaleReleaseVersion()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var staleBareVersion = string.Concat("0.", "4.7");
        var staleDisplayVersion = "v" + staleBareVersion;
        var searchableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".csproj",
            ".props",
            ".targets",
            ".ps1",
            ".iss",
            ".yml"
        };

        var offenders = new[] { "src", "tests", "scripts", ".github" }
            .Select(root => Path.Combine(repoRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !IsGeneratedOrIgnoredPath(Path.GetRelativePath(repoRoot, path)))
            .Where(path => searchableExtensions.Contains(Path.GetExtension(path)))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains(staleBareVersion, StringComparison.Ordinal) ||
                       text.Contains(staleDisplayVersion, StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .OrderBy(path => path)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Active code/tests must not contain the stale release literal. Offenders:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }
}
