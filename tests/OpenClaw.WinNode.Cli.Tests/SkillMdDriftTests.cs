using System.Text.RegularExpressions;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;

namespace OpenClaw.WinNode.Cli.Tests;

/// <summary>
/// F-11: skill.md duplicates the tray-side capability surface for agent
/// readability. This test compares the set of <c>### &lt;command&gt;</c>
/// headings in skill.md against <see cref="McpToolBridge.KnownCommands"/>
/// (the canonical list of documented capability commands) so additions
/// or renames in the tray fail loudly here instead of silently shipping
/// drifted documentation.
///
/// The test compares command identifiers only — descriptions, examples,
/// and prose can be tweaked freely without breaking the test.
/// </summary>
public class SkillMdDriftTests
{
    [Fact]
    public void SkillMd_command_set_matches_capability_registry()
    {
        var skillMdPath = LocateSkillMd();
        var content = File.ReadAllText(skillMdPath);
        Assert.Contains("\"provider\": \"piper|windows|elevenlabs|minimax\"", content);

        var documented = ParseCommandHeadings(content);
        var canonical = new HashSet<string>(McpToolBridge.KnownCommands, StringComparer.Ordinal);
        var actual = GetCapabilityCommands();

        var missingFromDescriptions = actual.Except(canonical).OrderBy(s => s).ToList();
        var staleDescriptions = canonical.Except(actual).OrderBy(s => s).ToList();
        if (missingFromDescriptions.Count > 0 || staleDescriptions.Count > 0)
        {
            var msg = "McpToolBridge.CommandDescriptions drifted from the actual capability command lists.\n" +
                      $"  Missing descriptions: [{string.Join(", ", missingFromDescriptions)}]\n" +
                      $"  Stale descriptions: [{string.Join(", ", staleDescriptions)}]";
            Assert.Fail(msg);
        }

        var missingFromDoc = canonical.Except(documented).OrderBy(s => s).ToList();
        var extrasInDoc = documented.Except(canonical).OrderBy(s => s).ToList();

        if (missingFromDoc.Count > 0 || extrasInDoc.Count > 0)
        {
            var msg = "skill.md drifted from the capability registry " +
                      "(McpToolBridge.CommandDescriptions). Update " +
                      $".agents/skills/winnode/SKILL.md.\n  Missing from doc: " +
                      $"[{string.Join(", ", missingFromDoc)}]\n  Extras in doc: " +
                      $"[{string.Join(", ", extrasInDoc)}]";
            Assert.Fail(msg);
        }
    }

    [Fact]
    public void ConnectionStatus_documents_package_and_protocol_compatibility_separately()
    {
        var content = File.ReadAllText(LocateSkillMd());

        Assert.Contains("gateway.packageVersion", content, StringComparison.Ordinal);
        Assert.Contains("wire protocol", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gateway_too_old", content, StringComparison.Ordinal);
        Assert.Contains("gateway_too_new", content, StringComparison.Ordinal);
        Assert.Contains("retryable", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4/3/4", content, StringComparison.Ordinal);
        Assert.Contains("selectedProtocol", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// skill.md lists each command under its own H3 heading like
    /// <c>### system.notify</c>. Anything matching <c>### &lt;dotted.name&gt;</c>
    /// counts as a documented command. We deliberately ignore other H3s
    /// (e.g. "### Message kinds", "### ComponentDef") which don't have a
    /// dotted-name shape.
    /// </summary>
    private static HashSet<string> ParseCommandHeadings(string md)
    {
        // Match ### followed by a single dotted token (lowercase, dots, dots+lowercase
        // segments only) to the end of the line. canvas.a2ui.pushJSONL has a
        // mixed-case suffix, so allow camelCase tail segments too.
        var rx = new Regex(@"^###\s+([a-z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9]+)+)\s*$",
                           RegexOptions.Multiline);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(md))
        {
            set.Add(m.Groups[1].Value);
        }
        return set;
    }

    private static HashSet<string> GetCapabilityCommands()
    {
        var provider = new FakeDeviceStatusProvider();
        var capabilities = new INodeCapability[]
        {
            new AppCapability(NullLogger.Instance),
            new AppConnectionCapability(NullLogger.Instance),
            new BrowserProxyCapability(NullLogger.Instance, "ws://127.0.0.1:8080", bearerToken: null),
            new CameraCapability(NullLogger.Instance),
            new CanvasCapability(NullLogger.Instance),
            new DeviceCapability(NullLogger.Instance, provider),
            new LocationCapability(NullLogger.Instance),
            new ScreenCapability(NullLogger.Instance),
            new SttCapability(NullLogger.Instance),
            new SystemCapability(NullLogger.Instance),
            new TtsCapability(NullLogger.Instance),
        };

        using var ollamaCapability = new OllamaCapability(NullLogger.Instance);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            foreach (var command in capability.Commands)
            {
                commands.Add(command);
            }
        }
        foreach (var command in ollamaCapability.Commands)
        {
            commands.Add(command);
        }
        return commands;
    }

    private sealed class FakeDeviceStatusProvider : IDeviceStatusProvider
    {
        public object GetOsInfo() => new { };
        public Task<object> GetCpuInfoAsync() => Task.FromResult<object>(new { });
        public object GetMemoryInfo() => new { };
        public object GetDiskInfo() => new { };
        public object GetBatteryInfo() => new { };
        public void Dispose() { }
    }

    /// <summary>
    /// SKILL.md ships next to winnode.exe as skill.md. From the test bin
    /// working directory, walk up to the repo root and resolve the source
    /// copy — that's the canonical input the build copies to output. Falls
    /// back to the test bin's own copy if the source can't be located.
    /// </summary>
    private static string LocateSkillMd()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, ".agents", "skills", "winnode", "SKILL.md");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        var nextTo = Path.Combine(AppContext.BaseDirectory, "skill.md");
        if (File.Exists(nextTo)) return nextTo;
        throw new FileNotFoundException("Could not locate .agents/skills/winnode/SKILL.md from the test working directory.");
    }
}
