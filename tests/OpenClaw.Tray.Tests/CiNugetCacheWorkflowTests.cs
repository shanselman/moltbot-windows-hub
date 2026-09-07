using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class CiNugetCacheWorkflowTests
{
    private static readonly string[] CoreEntryProjects =
    [
        "tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj",
        "tests/OpenClaw.Connection.Tests/OpenClaw.Connection.Tests.csproj",
        "tests/OpenClaw.WinNode.Cli.Tests/OpenClaw.WinNode.Cli.Tests.csproj",
    ];

    [Fact]
    public void CoreLane_UsesDedicatedPackageCacheWithoutChangingOtherLanes()
    {
        var workflow = ReadWorkflow();
        var coreJob = ExtractJob(workflow, "core-tests", "tray-tests");
        var otherJobs = workflow.Remove(workflow.IndexOf(coreJob, StringComparison.Ordinal), coreJob.Length);

        Assert.Contains(
            @"""NUGET_PACKAGES=$env:RUNNER_TEMP\openclaw-core-nuget-packages"" >> $env:GITHUB_ENV",
            coreJob,
            StringComparison.Ordinal);
        Assert.True(
            coreJob.IndexOf("- name: Configure core NuGet cache", StringComparison.Ordinal) <
            coreJob.IndexOf("- name: Cache NuGet packages", StringComparison.Ordinal));
        Assert.Contains("shell: pwsh", coreJob, StringComparison.Ordinal);
        Assert.Contains(@"path: ${{ env.NUGET_PACKAGES }}", coreJob, StringComparison.Ordinal);
        Assert.Contains("nuget-core-${{ runner.os }}-${{ hashFiles(", coreJob, StringComparison.Ordinal);
        Assert.DoesNotContain("restore-keys:", coreJob, StringComparison.Ordinal);

        Assert.DoesNotContain("NUGET_PACKAGES:", otherJobs, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget-core-", otherJobs, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(otherJobs, "path: ~/.nuget/packages"));
        Assert.Equal(
            8,
            CountOccurrences(
                otherJobs,
                "key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj', '**/Directory.Packages.props') }}"));
        Assert.Equal(8, CountOccurrences(otherJobs, "restore-keys: nuget-${{ runner.os }}-"));
    }

    [Fact]
    public void CoreLane_CachesOnlyTheDedicatedPackageDirectory()
    {
        var coreJob = ExtractJob(ReadWorkflow(), "core-tests", "tray-tests");
        var cacheStep = ExtractStep(coreJob, "Cache NuGet packages", "Restore core test projects");

        Assert.Equal(1, CountOccurrences(cacheStep, "path:"));
        Assert.Contains(@"path: ${{ env.NUGET_PACKAGES }}", cacheStep, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ github.workspace }}", cacheStep, StringComparison.Ordinal);

        foreach (var forbiddenPath in new[] { "bin/", "obj/", "TestResults", "node_modules", ".dotnet" })
            Assert.DoesNotContain(forbiddenPath, cacheStep, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoreCacheKey_HashesEveryRestoreGraphManifest()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var coreJob = ExtractJob(ReadWorkflow(), "core-tests", "tray-tests");
        var cacheStep = ExtractStep(coreJob, "Cache NuGet packages", "Restore core test projects");

        var projectGraph = GetProjectGraph(root);
        Assert.Equal(9, projectGraph.Count);

        foreach (var buildInput in GetBuildInputs(root, projectGraph))
            Assert.Contains($"'{buildInput}'", cacheStep, StringComparison.Ordinal);

        foreach (var project in projectGraph)
            Assert.Contains($"'{project}'", cacheStep, StringComparison.Ordinal);

        Assert.DoesNotContain("'**/*.csproj'", cacheStep, StringComparison.Ordinal);
        Assert.DoesNotContain("'**/Directory.Packages.props'", cacheStep, StringComparison.Ordinal);
    }

    private static SortedSet<string> GetProjectGraph(string root)
    {
        var pending = new Stack<string>(CoreEntryProjects);
        var graph = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryPop(out var project))
        {
            var normalizedProject = project.Replace('\\', '/');
            if (!graph.Add(normalizedProject))
                continue;

            var projectPath = Path.Combine(root, normalizedProject.Replace('/', Path.DirectorySeparatorChar));
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Project has no directory: {projectPath}");
            var document = XDocument.Load(projectPath);

            foreach (var reference in document
                         .Descendants()
                         .Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                Assert.False(string.IsNullOrWhiteSpace(include), $"ProjectReference has no Include in {project}");

                var referencedPath = Path.GetFullPath(Path.Combine(projectDirectory, include));
                pending.Push(Path.GetRelativePath(root, referencedPath).Replace('\\', '/'));
            }
        }

        return graph;
    }

    private static SortedSet<string> GetBuildInputs(string root, IEnumerable<string> projectGraph)
    {
        var inputs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "global.json",
            "NuGet.Config",
        };

        foreach (var project in projectGraph)
        {
            var projectPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException($"Project has no directory: {projectPath}");

            foreach (var fileName in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var buildFile = FindNearestBuildFile(root, projectDirectory, fileName);
                if (buildFile is not null)
                    AddBuildFileAndImports(root, buildFile, inputs);
            }
        }

        return inputs;
    }

    private static string? FindNearestBuildFile(string root, string projectDirectory, string fileName)
    {
        var rootPath = Path.GetFullPath(root);
        var directory = new DirectoryInfo(projectDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;

            if (string.Equals(directory.FullName, rootPath, StringComparison.OrdinalIgnoreCase))
                break;

            directory = directory.Parent;
        }

        return null;
    }

    private static void AddBuildFileAndImports(
        string root,
        string buildFile,
        ISet<string> inputs)
    {
        var relativePath = Path.GetRelativePath(root, buildFile).Replace('\\', '/');
        if (!inputs.Add(relativePath))
            return;

        var buildDirectory = Path.GetDirectoryName(buildFile)
            ?? throw new InvalidOperationException($"Build file has no directory: {buildFile}");
        var document = XDocument.Load(buildFile);
        foreach (var import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
        {
            var project = import.Attribute("Project")?.Value;
            if (string.IsNullOrWhiteSpace(project) || project.Contains("$(", StringComparison.Ordinal))
                continue;

            var importedPath = Path.GetFullPath(Path.Combine(buildDirectory, project));
            if (File.Exists(importedPath))
                AddBuildFileAndImports(root, importedPath, inputs);
        }
    }

    private static string ReadWorkflow()
    {
        return File.ReadAllText(
                Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ExtractJob(string workflow, string jobName, string nextJobName)
    {
        var start = workflow.IndexOf($"  {jobName}:", StringComparison.Ordinal);
        var end = workflow.IndexOf($"\n  {nextJobName}:", start, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Could not find workflow job {jobName}.");
        Assert.True(end > start, $"Could not find workflow job after {jobName}.");
        return workflow[start..end];
    }

    private static string ExtractStep(string job, string stepName, string nextStepName)
    {
        var start = job.IndexOf($"    - name: {stepName}", StringComparison.Ordinal);
        var end = job.IndexOf($"\n    - name: {nextStepName}", start, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Could not find workflow step {stepName}.");
        Assert.True(end > start, $"Could not find workflow step after {stepName}.");
        return job[start..end];
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}
