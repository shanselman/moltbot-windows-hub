using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenClaw.Shared;

namespace OpenClaw.Tray.IntegrationTests;

/// <summary>
/// xUnit class fixture that spawns the WinUI tray app in MCP-only mode against
/// an isolated data directory and a free localhost port, and waits for the MCP
/// HTTP server to come up. One process is shared across the test class.
/// </summary>
public sealed class TrayAppFixture : IAsyncLifetime
{
    public const string SeededExecApprovalPattern = "**/where.exe";
    public const string SeededExecApprovalId = "11111111-1111-1111-1111-111111111111";

    // A generated, argument-bound entry. Remote updates may retain it verbatim but must
    // not be able to keep the path while dropping the binding that keeps it narrow.
    public const string SeededBoundExecApprovalPattern = @"C:\Windows\System32\hostname.exe";
    public const string SeededBoundExecApprovalId = "22222222-2222-2222-2222-222222222222";
    public const string SeededBoundExecApprovalArgPattern = "^--version\u0000$";
    // Escaped for embedding in the JSON template above.
    public const string SeededBoundExecApprovalArgPatternJson = @"^--version\u0000$";
    public const string SeededBoundExecApprovalPatternJson = @"C:\\Windows\\System32\\hostname.exe";

    public string DataDir { get; }
    public int McpPort { get; }
    public string McpEndpoint => $"http://127.0.0.1:{McpPort}/mcp";
    public McpClient Client { get; private set; }

    private readonly string _exePath;
    private readonly Process _process;
    private bool _disposed;

    public TrayAppFixture()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "openclaw-tray-it-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(DataDir);

        McpPort = FindFreePort();
        WriteSettings();
        WriteExecApprovals();

        _exePath = LocateTrayExe();
        _process = SpawnTray();

        // Note: token doesn't exist until the tray starts the MCP server.
        // We reset Client.Authorization once the file appears in InitializeAsync.
        Client = new McpClient(McpEndpoint);
    }

    public async Task InitializeAsync()
    {
        // Readiness has two preconditions, both of which must hold before any
        // test runs a JSON-RPC call:
        //   1. mcp-token.txt has been written by the tray. The tray creates it
        //      synchronously inside StartMcpServer, just before the listener
        //      binds — so on a healthy run it appears slightly *before* the
        //      HTTP server starts accepting. Required for Authorization headers.
        //   2. GET / returns 200 with that bearer token. Confirms the listener
        //      is up *and* the in-memory token matches the on-disk one.
        // Returning ready on (2) alone is unsafe: against a tray binary built
        // before the auth-before-dispatch hardening, GET / returns 200 even
        // without auth, so the fixture would skip step (1) and hand out a
        // tokenless Client — every subsequent POST then 401s.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tokenPath = Path.Combine(DataDir, "mcp-token.txt");
        string? token = null;
        Exception? lastEx = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Tray process exited before MCP server became ready (exit code {_process.ExitCode}). " +
                    $"Logs: {Path.Combine(DataDir, "openclaw-tray.log")}");
            }
            try
            {
                if (token is null)
                {
                    if (!File.Exists(tokenPath))
                    {
                        // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
                        await Task.Delay(500).ConfigureAwait(false);
                        continue;
                    }
                    token = (await File.ReadAllTextAsync(tokenPath).ConfigureAwait(false)).Trim();
                    if (string.IsNullOrEmpty(token))
                    {
                        // Mid-write zero-byte file is theoretically possible (the
                        // tray writes to a sibling temp and renames, but file
                        // systems are funny). Re-read on the next tick.
                        token = null;
                        // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
                        await Task.Delay(500).ConfigureAwait(false);
                        continue;
                    }
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var resp = await http.GetAsync($"http://127.0.0.1:{McpPort}/").ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    Client.Dispose();
                    Client = new McpClient(McpEndpoint, token);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
            // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"MCP server never came up on {McpEndpoint}. Last error: {lastEx?.Message}. " +
            $"Logs: {Path.Combine(DataDir, "openclaw-tray.log")}");
    }

    public Task DisposeAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _disposed = true;

        Client.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                // Tray app has no clean IPC to ask it to quit, so just kill it.
                // Mutex and HTTP listener are released on process exit.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch { /* best effort */ }
        finally
        {
            _process.Dispose();
        }

        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private void WriteSettings()
    {
        // HasSeenActivityStreamTip suppresses the first-run UI tip. EnableMcpServer
        // + !EnableNodeMode routes through StartLocalOnlyAsync (no gateway WebSocket).
        // Disable the MXC sandbox for these tray/MCP wiring smokes because hosted
        // CI does not provide MXC; fail-closed sandbox behavior is covered by
        // OpenClaw.Shared.Tests.Mxc.
        // CaptureConsentTimeoutMs is cut way down from the 120s production default:
        // screen.snapshot/camera.snap now require recording consent (see
        // NodeService.EnsureCaptureConsentAsync), and no human is present in this
        // hosted process to click the consent prompt. The fail-closed timeout
        // still denies (never auto-grants) — it just does so in seconds instead
        // of never, so ScreenSnapshot/CameraSnap integration tests get a
        // deterministic tool error well inside McpClient's 30s HTTP timeout.
        var settings = new SettingsData
        {
            EnableMcpServer = true,
            EnableNodeMode = false,
            SystemRunSandboxEnabled = false,
            AutoStart = false,
            GlobalHotkeyEnabled = false,
            ShowNotifications = false,
            ScreenRecordingConsentGiven = true,
            CameraRecordingConsentGiven = true,
            HasSeenActivityStreamTip = true,
            CaptureConsentTimeoutMs = 3000,
        };
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), settings.ToJson());
    }

    private void WriteExecApprovals()
    {
        File.WriteAllText(
            Path.Combine(DataDir, "exec-approvals.json"),
            $$"""
            {
              "version": 1,
              "defaults": {
                "security": "allowlist",
                "ask": "off",
                "askFallback": "deny",
                "autoAllowSkills": false
              },
              "agents": {
                "main": {
                  "security": "allowlist",
                  "ask": "off",
                  "askFallback": "deny",
                  "autoAllowSkills": false,
                  "allowlist": [
                    {
                      "id": "{{SeededExecApprovalId}}",
                      "pattern": "{{SeededExecApprovalPattern}}"
                    },
                    {
                      "id": "{{SeededBoundExecApprovalId}}",
                      "pattern": "{{SeededBoundExecApprovalPatternJson}}",
                      "source": "allow-always",
                      "argPattern": "{{SeededBoundExecApprovalArgPatternJson}}"
                    }
                  ]
                }
              }
            }
            """);
    }

    private static string LocateTrayExe()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            var other => throw new PlatformNotSupportedException($"Unsupported process architecture: {other}"),
        };

        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        var repoRoot = FindRepoRoot();
        var targetFramework = GetTrayTargetFramework(repoRoot);
        var exe = Path.Combine(
            repoRoot,
            "src", "OpenClaw.Tray.WinUI", "bin", configuration,
            targetFramework, rid, "OpenClaw.Tray.WinUI.exe");

        if (!File.Exists(exe))
        {
            throw new FileNotFoundException(
                $"Tray exe not found at {exe}. Build it first: " +
                $"`dotnet build src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj -c {configuration} -r {rid}`");
        }
        return exe;
    }

    private static string GetTrayTargetFramework(string repoRoot)
    {
        var projectPath = Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj");
        var targetFramework = XDocument.Load(projectPath)
            .Descendants("TargetFramework")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (targetFramework is null)
        {
            throw new InvalidDataException($"Could not locate TargetFramework in {projectPath}");
        }

        return targetFramework;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "openclaw-windows-node.slnx")))
                return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir || parent == null) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root (openclaw-windows-node.slnx) from " + AppContext.BaseDirectory);
    }

    private static int FindFreePort()
    {
        // Bind to port 0 to let the OS pick a free port, then release it.
        // There's a small race window where another process could grab the port
        // before the tray app rebinds, but it's vanishingly small in practice.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private Process SpawnTray()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = Path.GetDirectoryName(_exePath)!,
            UseShellExecute = false,
            CreateNoWindow = false, // tray app is windowed; flag doesn't really apply
        };
        psi.Environment["OPENCLAW_TRAY_DATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_MCP_PORT"] = McpPort.ToString();
        psi.Environment["OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"] = "1";

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start tray app process");
        return p;
    }
}
