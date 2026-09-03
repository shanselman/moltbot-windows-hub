using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenClaw.E2ETests;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

/// <summary>
/// xUnit class fixture that runs the full SetupEngine CLI pipeline headless,
/// then spawns the tray app and waits for MCP readiness. Shared across all
/// tests in the collection — setup runs once, tests verify the result.
///
/// All logs (setup engine, tray, uninstall) are written to a persistent
/// TestResults/E2E directory so CI can upload them as artifacts for debugging.
/// </summary>
public sealed class E2ESetupFixture : IAsyncLifetime
{
    private readonly Action<Dictionary<string, object>>? _settingsPatch;

    /// <summary>
    /// Persistent artifact directory that survives test cleanup.
    /// CI uploads this as a test artifact for post-mortem debugging.
    /// Lives under the repo's TestResults/E2E/ so the CI upload glob finds it.
    /// </summary>
    public string ArtifactDir { get; }

    /// <summary>
    /// Isolated data directory for the tray app (settings, gateways, tokens).
    /// Set as OPENCLAW_TRAY_DATA_DIR env var.
    /// </summary>
    public string DataDir { get; }
    public string LocalAppDataRoot { get; }

    public int McpPort { get; private set; }
    public int GatewayPort { get; private set; }
    public string McpEndpoint => $"http://127.0.0.1:{McpPort}/mcp";
    public McpClient? Client { get; private set; }
    public string DistroName => _distroName;
    public string ConfigPath => _configPath;

    /// <summary>Non-null after a successful setup pipeline run.</summary>
    public string? SetupError { get; private set; }

    private readonly string _configPath;
    private readonly string _distroName;
    private MirroredWslPortLease? _gatewayPortLease;
    private readonly Dictionary<string, string> _trayEnvironment = new(StringComparer.Ordinal);
    private Process? _trayProcess;
    public E2ESetupFixture()
        : this(settingsPatch: null)
    {
    }

    internal E2ESetupFixture(
        Action<Dictionary<string, object>>? settingsPatch,
        bool useProductionLikeDataRoot = false)
    {
        _settingsPatch = settingsPatch;

        if (!E2ETestGate.IsEnabled)
        {
            _distroName = "OpenClawE2E-disabled";
            DataDir = string.Empty;
            LocalAppDataRoot = string.Empty;
            ArtifactDir = string.Empty;
            _configPath = string.Empty;
            return;
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        _distroName = $"OpenClawE2E-{runId}";
        var repoRoot = FindRepoRoot();

        // MXC containment proofs need the same default-deny boundary as the production
        // %APPDATA%\OpenClawTray directory. %TEMP% is AppContainer-writable and cannot
        // prove that the settings directory remains protected.
        DataDir = useProductionLikeDataRoot
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenClawE2E",
                runId)
            : Path.Combine(Path.GetTempPath(), $"openclaw-e2e-{runId}");
        LocalAppDataRoot = Path.Combine(Path.GetTempPath(), $"openclaw-e2e-localappdata-{runId}");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LocalAppDataRoot);

        // Artifact dir under repo TestResults — persists after cleanup for CI upload
        ArtifactDir = Path.Combine(repoRoot, "TestResults", "E2E", runId);
        Directory.CreateDirectory(ArtifactDir);

        var gatewayPortLease = MirroredWslPortLease.Acquire();
        try
        {
            _gatewayPortLease = gatewayPortLease;
            GatewayPort = gatewayPortLease.Port;

            // Write isolated config JSON
            _configPath = Path.Combine(DataDir, "e2e-config.json");
            WriteConfig();
            WritePortAllocationArtifact(gatewayPortLease);

            Log($"E2E fixture initialized: distro={_distroName}, gatewayPort={GatewayPort}, dataDir={DataDir}, localAppDataRoot={LocalAppDataRoot}, artifacts={ArtifactDir}");
        }
        catch
        {
            gatewayPortLease.Dispose();
            _gatewayPortLease = null;
            throw;
        }
    }

    public async Task InitializeAsync()
    {
        if (!E2ETestGate.IsEnabled)
            return;

        try
        {
            await InitializeEnabledAsync();
        }
        catch
        {
            ReleaseGatewayPortLease();
            throw;
        }
    }

    private async Task InitializeEnabledAsync()
    {
        // ── Phase 1: Run SetupEngine CLI ──
        Log("Phase 1: Running SetupEngine CLI pipeline...");
        var setupLogPath = Path.Combine(ArtifactDir, "setup-engine.jsonl");

        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", LocalAppDataRoot);

        var setupArguments = new List<string>
        {
            "--config", _configPath,
            "--headless",
            "--rollback-on-failure",
            "--log-path", setupLogPath
        };
        var candidateVersion = Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_VERSION");
        var candidatePackage = Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ");
        if (!string.IsNullOrWhiteSpace(candidateVersion))
        {
            setupArguments.Add("--validate-gateway-candidate");
        }
        if (!string.IsNullOrWhiteSpace(candidatePackage))
        {
            if (string.IsNullOrWhiteSpace(candidateVersion))
                throw new InvalidOperationException(
                    "OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ requires OPENCLAW_E2E_GATEWAY_VERSION.");

            setupArguments.Add("--gateway-candidate-package");
            setupArguments.Add(candidatePackage);
        }

        var exitCode = await Program.Main([.. setupArguments]);

        if (exitCode != 0)
        {
            SetupError = $"SetupEngine CLI exited with code {exitCode}. Logs: {setupLogPath}";
            CopyDataDirLogs();
            throw new InvalidOperationException(SetupError);
        }

        Log("Phase 1 complete: pipeline succeeded.");

        // ── Phase 2: Verify artifacts ──
        Log("Phase 2: Verifying artifacts...");
        var settingsPath = Path.Combine(DataDir, "settings.json");
        var gatewaysPath = Path.Combine(DataDir, "gateways.json");

        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("settings.json not written by setup pipeline", settingsPath);
        if (!File.Exists(gatewaysPath))
            throw new FileNotFoundException("gateways.json not written by setup pipeline", gatewaysPath);

        // Patch EnableMcpServer into settings (setup writes EnableNodeMode but not EnableMcpServer)
        PatchSettingsForMcp(settingsPath);
        Log("Phase 2 complete: artifacts verified, EnableMcpServer patched.");

        // ── Phase 3: Spawn tray and wait for MCP ──
        Log("Phase 3: Spawning tray app...");
        McpPort = FindFreePort();
        var exePath = LocateTrayExe();
        _trayProcess = SpawnTray(exePath);
        Log($"Tray spawned: PID={_trayProcess.Id}, MCP port={McpPort}");

        Client = new McpClient(McpEndpoint);
        await WaitForMcpReady();
        Log("Phase 3 complete: MCP server ready.");

        // ── Phase 4: Wait for gateway connection to reach Ready ──
        Log("Phase 4: Waiting for tray gateway connection...");
        await WaitForConnectionReady();
        await WaitForNodeListReady();
        Log("Phase 4 complete: tray fully connected and node list populated.");
    }

    public async Task DisposeAsync()
    {
        if (!E2ETestGate.IsEnabled)
            return;

        try
        {
            await DisposeEnabledAsync();
        }
        finally
        {
            ReleaseGatewayPortLease();
        }
    }

    private async Task DisposeEnabledAsync()
    {
        Log("Teardown starting...");

        // 1-2. Dispose MCP client and stop the exact proof-owned tray.
        await StopTrayAsync();

        // 3. Uninstall via CLI
        Log("Running SetupEngine CLI uninstall...");
        var uninstallLogPath = Path.Combine(ArtifactDir, "uninstall-engine.jsonl");

        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR", LocalAppDataRoot);

        try
        {
            var exitCode = await Program.Main([
                "--config", _configPath,
                "--uninstall",
                "--confirm-destructive",
                "--log-path", uninstallLogPath
            ]);
            Log($"Uninstall completed with exit code {exitCode}.");
        }

        catch (Exception ex)
        {
            Log($"Warning: uninstall threw: {ex.Message}");
        }

        // 4. Copy logs from data dir to artifact dir before deleting
        CopyDataDirLogs();

        // 5. Delete temp data dirs (best-effort)
        try { Directory.Delete(DataDir, recursive: true); }
        catch (Exception ex) { Log($"Warning: temp dir cleanup failed: {ex.Message}"); }
        try { Directory.Delete(LocalAppDataRoot, recursive: true); }
        catch (Exception ex) { Log($"Warning: temp local appdata cleanup failed: {ex.Message}"); }

        Log("Teardown complete.");
    }

    public void SetTrayEnvironmentVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _trayEnvironment[name] = value;
    }

    public void RemoveTrayEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _trayEnvironment.Remove(name);
    }

    public async Task StopTrayAsync()
    {
        Client?.Dispose();
        Client = null;

        if (_trayProcess is null)
            return;

        var process = _trayProcess;
        _trayProcess = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Log($"Tray process killed (PID={process.Id}).");
            }
        }
        catch (Exception ex)
        {
            Log($"Warning: tray kill failed: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task StartTrayAsync(bool waitForConnection = true)
    {
        if (_trayProcess is { HasExited: false })
            throw new InvalidOperationException("The E2E tray is already running.");

        McpPort = FindFreePort();
        var exePath = LocateTrayExe();
        _trayProcess = SpawnTray(exePath);
        Log($"Tray respawned: PID={_trayProcess.Id}, MCP port={McpPort}");
        Client = new McpClient(McpEndpoint);
        await WaitForMcpReady();
        if (waitForConnection)
        {
            await WaitForConnectionReady(TimeSpan.FromSeconds(120));
            await WaitForNodeListReady(TimeSpan.FromSeconds(90));
        }
    }

    public static int AllocateFreePort() => FindFreePort();

    // ─── Helpers ───

    private void WriteConfig()
    {
        var candidateVersion =
            Environment.GetEnvironmentVariable("OPENCLAW_E2E_GATEWAY_VERSION")?.Trim();
        var gateway = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(candidateVersion))
            gateway["Version"] = candidateVersion;

        var config = new
        {
            DistroName = _distroName,
            GatewayPort = GatewayPort,
            BaseDistro = "Ubuntu-24.04",
            Headless = true,
            AutoApprovePairing = true,
            RollbackOnFailure = true,
            CleanBeforeRun = true,
            SkipPermissions = true,
            SkipWizard = false,
            LogLevel = "trace",
            WizardAnswers = new Dictionary<string, string>
            {
                ["openclaw-setup"] = "true",
                ["security-disclaimer"] = "true",
                ["i-understand-this-is-personal-by-default-and-shared-multi-user-use-requires-lock-down-continue"] = "true",
                ["setup-mode"] = "quickstart",
                ["existing-config-detected"] = "true",
                ["config-handling"] = "keep",
                ["quickstart"] = "true",
                ["model-auth-provider"] = "skip",
                ["default-model"] = "__keep__",
                ["select-channel-quickstart"] = "__skip__",
                ["search-provider"] = "__skip__",
                ["configure-skills-now-recommended"] = "false",
            },
            Settings = new
            {
                EnableNodeMode = true,
                AutoStart = false,
                NodeTtsEnabled = true,
                NodeSttEnabled = true,
            },
            Gateway = gateway
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private void WritePortAllocationArtifact(MirroredWslPortLease lease)
    {
        var artifact = new
        {
            gatewayPort = lease.Port,
            candidateRange = new
            {
                start = MirroredWslPortLease.CandidateRangeStart,
                end = MirroredWslPortLease.CandidateRangeEnd,
            },
            windowsDynamicTcpRanges = lease.WindowsPortState.DynamicRanges.Select(range => new
            {
                start = range.Start,
                end = range.End,
            }),
            windowsExcludedTcpRanges = lease.WindowsPortState.ExcludedRanges.Select(range => new
            {
                start = range.Start,
                end = range.End,
            }),
            selectionChecks = new[]
            {
                "outside-windows-dynamic-range",
                "outside-windows-excluded-ranges",
                "exclusive-ipv4-ipv6-loopback-bind-probe",
                "cross-process-fixture-lease",
            },
        };

        File.WriteAllText(
            Path.Combine(ArtifactDir, "gateway-port-allocation.json"),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PatchSettingsForMcp(string settingsPath)
    {
        var json = File.ReadAllText(settingsPath);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        // Build a merged dictionary with EnableMcpServer added
        var merged = new Dictionary<string, object>();
        foreach (var kvp in settings)
            merged[kvp.Key] = kvp.Value;
        merged["EnableMcpServer"] = true;
        merged["HasSeenActivityStreamTip"] = true;
        merged["ShowNotifications"] = false;
        merged["GlobalHotkeyEnabled"] = false;
        _settingsPatch?.Invoke(merged);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task WaitForMcpReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var tokenPath = Path.Combine(DataDir, "mcp-token.txt");
        string? token = null;
        Exception? lastEx = null;

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess!.HasExited)
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited before MCP server became ready (exit code {_trayProcess.ExitCode}). " +
                    $"Logs: {ArtifactDir}");
            }

            try
            {
                if (token is null)
                {
                    if (!File.Exists(tokenPath))
                    {
                        // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
                        await Task.Delay(500);
                        continue;
                    }
                    token = (await File.ReadAllTextAsync(tokenPath)).Trim();
                    if (string.IsNullOrEmpty(token))
                    {
                        token = null;
                        // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
                        await Task.Delay(500);
                        continue;
                    }
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    Log($"MCP token acquired ({token.Length} chars).");
                }

                var resp = await http.GetAsync($"http://127.0.0.1:{McpPort}/");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    Client?.Dispose();
                    Client = new McpClient(McpEndpoint, token);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }

            // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
            await Task.Delay(500);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"MCP server never came up on {McpEndpoint} within 90s. Last error: {lastEx?.Message}. " +
            $"Logs: {ArtifactDir}");
    }

    /// <summary>
    /// Polls app.status via MCP until the tray reports operator connected and
    /// node connected+paired. The derived connectionStatus ("Ready") requires
    /// both roles' FSMs to reach Connected, but the node service reports its
    /// own connected state directly — use that as the reliable signal.
    /// </summary>
    public async Task WaitForConnectionReady(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(90));
        string lastStatus = "unknown";
        bool lastNodeConnected = false;
        bool lastNodePaired = false;

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess!.HasExited)
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited while waiting for connection (exit code {_trayProcess.ExitCode}). " +
                    $"Last status: {lastStatus}, nodeConnected: {lastNodeConnected}. Logs: {ArtifactDir}");
            }

            try
            {
                using var doc = await Client!.CallToolExpectSuccessAsync("app.status");
                var root = doc.RootElement;
                lastStatus = root.GetProperty("connectionStatus").GetString() ?? "null";
                lastNodeConnected = root.TryGetProperty("nodeConnected", out var nc) && nc.GetBoolean();
                lastNodePaired = root.TryGetProperty("nodePaired", out var np) && np.GetBoolean();

                Log($"Connection poll: status={lastStatus}, nodeConnected={lastNodeConnected}, nodePaired={lastNodePaired}");

                // Accept Connected or Ready when node is confirmed connected+paired
                if ((lastStatus is "Ready" or "Connected") && lastNodeConnected && lastNodePaired)
                    return;
            }
            catch (Exception ex)
            {
                Log($"Connection poll error: {ex.Message}");
            }

            // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
            await Task.Delay(2000);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"Tray never reached connected state within {timeout?.TotalSeconds ?? 90}s. Last: status={lastStatus}, " +
            $"nodeConnected={lastNodeConnected}, nodePaired={lastNodePaired}. Logs: {ArtifactDir}");
    }

    public async Task WaitForNodeListReady(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(60));
        string lastResponse = "unknown";

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess!.HasExited)
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited while waiting for node list (exit code {_trayProcess.ExitCode}). " +
                    $"Last app.nodes response: {lastResponse}. Logs: {ArtifactDir}");
            }

            try
            {
                using var doc = await Client!.CallToolExpectSuccessAsync("app.nodes");
                var root = doc.RootElement;
                lastResponse = root.GetRawText();
                if (root.ValueKind == JsonValueKind.Array
                    && root.GetArrayLength() >= 1
                    && root[0].TryGetProperty("IsOnline", out var online)
                    && online.GetBoolean()
                    && root[0].TryGetProperty("CapabilityCount", out var capabilities)
                    && capabilities.GetInt32() > 0)
                {
                    Log($"Node list ready: {lastResponse}");
                    return;
                }

                Log($"Node list not ready: {lastResponse}");
            }
            catch (Exception ex)
            {
                Log($"Node list poll error: {ex.Message}");
            }

            // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
            await Task.Delay(1000);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"app.nodes never returned an online node with capabilities within {timeout?.TotalSeconds ?? 60}s. " +
            $"Last response: {lastResponse}. Logs: {ArtifactDir}");
    }

    private Process SpawnTray(string exePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["OPENCLAW_TRAY_DATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_TRAY_APPDATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = LocalAppDataRoot;
        psi.Environment["OPENCLAW_MCP_PORT"] = McpPort.ToString();
        psi.Environment["OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"] = "1";
        foreach (var (key, value) in _trayEnvironment)
            psi.Environment[key] = value;

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start tray app process");

        // Capture stdout/stderr to artifact files asynchronously
        var stdoutPath = Path.Combine(ArtifactDir, "tray-stdout.log");
        var stderrPath = Path.Combine(ArtifactDir, "tray-stderr.log");
        _ = CaptureStreamAsync(p.StandardOutput, stdoutPath);
        _ = CaptureStreamAsync(p.StandardError, stderrPath);

        return p;
    }

    public async Task<OpenClaw.SetupEngine.CommandResult> RunInWslAsync(
        string command,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool inputViaStdin = false,
        string? user = null)
    {
        command = command.Replace("\r", "");
        Log($"WSL command: {SanitizeForLog(command)}");

        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = inputViaStdin,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(_distroName);
        if (!string.IsNullOrWhiteSpace(user))
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(user);
        }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add(inputViaStdin ? "-s" : "-c");
        if (!inputViaStdin)
            psi.ArgumentList.Add(command);

        if (environment is { Count: > 0 })
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

            var wslEnvKeys = string.Join(":", environment.Keys);
            var existing = psi.Environment.ContainsKey("WSLENV") ? psi.Environment["WSLENV"] : null;
            psi.Environment["WSLENV"] = !string.IsNullOrEmpty(existing)
                ? $"{existing}:{wslEnvKeys}"
                : wslEnvKeys;
        }

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wsl.exe");

        if (inputViaStdin)
        {
            await process.StandardInput.WriteAsync(command);
            await process.StandardInput.WriteAsync("\n");
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        sw.Stop();

        var result = new OpenClaw.SetupEngine.CommandResult(timedOut ? -1 : process.ExitCode, stdout, stderr, sw.Elapsed, timedOut);
        Log($"WSL result: exit={result.ExitCode}, timedOut={result.TimedOut}, stdout={SanitizeForLog(stdout.Trim())}, stderr={SanitizeForLog(stderr.Trim())}");
        return result;
    }

    public (string GatewayUrl, string? SharedGatewayToken, string ActiveId) ReadActiveGatewayRecord()
    {
        var gatewaysPath = Path.Combine(DataDir, "gateways.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(gatewaysPath));
        var root = doc.RootElement;
        var activeId = TryGetPropertyIgnoreCase(root, "ActiveId", out var activeIdElement)
            ? activeIdElement.GetString()
            : null;

        if (!TryGetPropertyIgnoreCase(root, "Gateways", out var gatewaysElement) ||
            gatewaysElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("gateways.json is missing gateways array");
        }

        var gateways = gatewaysElement.EnumerateArray().ToArray();
        var active = gateways.FirstOrDefault(g =>
            string.IsNullOrWhiteSpace(activeId) ||
            (TryGetPropertyIgnoreCase(g, "Id", out var idElement) &&
             string.Equals(idElement.GetString(), activeId, StringComparison.Ordinal)));

        if (active.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("No active gateway record found in gateways.json");

        var url = TryGetPropertyIgnoreCase(active, "Url", out var urlElement)
            ? urlElement.GetString()
            : null;
        var sharedToken = TryGetPropertyIgnoreCase(active, "SharedGatewayToken", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var id = TryGetPropertyIgnoreCase(active, "Id", out var activeIdValueElement)
            ? activeIdValueElement.GetString()
            : null;

        Log($"Active gateway record: id={id}, url={url}, sharedTokenPresent={!string.IsNullOrWhiteSpace(sharedToken)}, sharedTokenLength={sharedToken?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("Active gateway record is missing Url or Id");

        return (url, sharedToken, id);
    }

    public (bool HasOperatorToken, bool HasNodeToken, bool HasBootstrapToken, string IdentityDir) ReadActiveGatewayCredentialState()
    {
        var gateway = ReadActiveGatewayRecord();
        var identityDir = Path.Combine(DataDir, "gateways", gateway.ActiveId);
        var identityPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (!File.Exists(identityPath))
            throw new FileNotFoundException("Active gateway identity file not found", identityPath);

        using var identityDoc = JsonDocument.Parse(File.ReadAllText(identityPath));
        var root = identityDoc.RootElement;
        var hasOperatorToken =
            TryGetPropertyIgnoreCase(root, "DeviceToken", out var operatorToken) &&
            operatorToken.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(operatorToken.GetString());
        var hasNodeToken =
            TryGetPropertyIgnoreCase(root, "NodeDeviceToken", out var nodeToken) &&
            nodeToken.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nodeToken.GetString());

        var gatewaysPath = Path.Combine(DataDir, "gateways.json");
        using var gatewaysDoc = JsonDocument.Parse(File.ReadAllText(gatewaysPath));
        var hasBootstrapToken = false;
        if (TryGetPropertyIgnoreCase(gatewaysDoc.RootElement, "Gateways", out var gatewaysElement) &&
            gatewaysElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in gatewaysElement.EnumerateArray())
            {
                if (TryGetPropertyIgnoreCase(record, "Id", out var idElement) &&
                    string.Equals(idElement.GetString(), gateway.ActiveId, StringComparison.Ordinal))
                {
                    hasBootstrapToken =
                        TryGetPropertyIgnoreCase(record, "BootstrapToken", out var bootstrapToken) &&
                        bootstrapToken.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(bootstrapToken.GetString());
                    break;
                }
            }
        }

        Log($"Credential state: operatorToken={hasOperatorToken}, nodeToken={hasNodeToken}, bootstrapToken={hasBootstrapToken}, identityDir={identityDir}");
        return (hasOperatorToken, hasNodeToken, hasBootstrapToken, identityDir);
    }

    public string ReadActiveGatewayDeviceId()
    {
        var credentials = ReadActiveGatewayCredentialState();
        var identityPath = Path.Combine(credentials.IdentityDir, "device-key-ed25519.json");
        using var identityDoc = JsonDocument.Parse(File.ReadAllText(identityPath));
        if (TryGetPropertyIgnoreCase(identityDoc.RootElement, "DeviceId", out var deviceId) &&
            deviceId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(deviceId.GetString()))
        {
            return deviceId.GetString()!;
        }

        throw new InvalidDataException($"Active gateway identity file is missing DeviceId: {identityPath}");
    }

    public async Task<(bool HasOperatorToken, bool HasNodeToken, bool HasBootstrapToken, string IdentityDir)> WaitForDurablePairedCredentialsAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(45));
        (bool HasOperatorToken, bool HasNodeToken, bool HasBootstrapToken, string IdentityDir)? last = null;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = ReadActiveGatewayCredentialState();
                if (last.Value.HasOperatorToken && last.Value.HasNodeToken && !last.Value.HasBootstrapToken)
                    return last.Value;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Timed out waiting for durable paired credentials. Last={last?.ToString() ?? "<none>"}; error={lastError?.Message}");
    }

    public async Task<string> WaitForTrayKeepAliveReadyAsync(TimeSpan? timeout = null)
    {
        var logPath = Path.Combine(DataDir, "openclaw-tray.log");
        var expectedStarted = $"[WslKeepAlive] Started keepalive for {_distroName}";
        var expectedExisting = $"[WslKeepAlive] Keepalive already running for {_distroName}";
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        string lastLogTail = "";

        while (DateTime.UtcNow < deadline)
        {
            if (_trayProcess is { HasExited: true })
            {
                CopyDataDirLogs();
                throw new InvalidOperationException(
                    $"Tray process exited while waiting for WSL keepalive log (exit code {_trayProcess.ExitCode}). " +
                    $"Log path: {logPath}. Logs: {ArtifactDir}");
            }

            if (File.Exists(logPath))
            {
                var lines = await File.ReadAllLinesAsync(logPath);
                lastLogTail = string.Join(Environment.NewLine, lines.TakeLast(20));
                var match = lines.LastOrDefault(line =>
                    line.Contains(expectedStarted, StringComparison.Ordinal) ||
                    line.Contains(expectedExisting, StringComparison.Ordinal));
                if (match is not null)
                {
                    Log($"Tray keepalive verified from log: {match}");
                    return match;
                }
            }

            // slopwatch-ignore: SW004 Integration fixture polling delay is intentional and bounded while waiting for external process state.
            await Task.Delay(500);
        }

        CopyDataDirLogs();
        throw new TimeoutException(
            $"Tray did not log WSL keepalive startup within {timeout?.TotalSeconds ?? 30}s. " +
            $"Expected: {expectedStarted} or {expectedExisting}. " +
            $"Log tail: {lastLogTail}. Logs: {ArtifactDir}");
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string SanitizeForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sanitized = Regex.Replace(value, @"(?i)(token|authorization|secret|password)([""'\s:=]+)([^""'\s,}]+)", "$1$2[REDACTED]");
        sanitized = Regex.Replace(sanitized, @"(?i)bearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]");
        sanitized = Regex.Replace(sanitized, @"[A-Za-z0-9_-]{48,}", "[REDACTED]");
        return sanitized;
    }

    private static async Task CaptureStreamAsync(System.IO.StreamReader reader, string filePath)
    {
        try
        {
            using var writer = new StreamWriter(filePath, append: false) { AutoFlush = true };
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                await writer.WriteLineAsync(line);
            }
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch { /* process exited — expected */ }
    }

    /// <summary>
    /// Copies any log files from the data dir to the artifact dir
    /// so they're available even after temp cleanup.
    /// </summary>
    private void CopyDataDirLogs()
    {
        try
        {
            CopyLogsFrom(DataDir, "data-dir");
            CopyLogsFrom(LocalAppDataRoot, "localappdata-dir");
        }
        catch (Exception ex)
        {
            Log($"Warning: copying data dir logs failed: {ex.Message}");
        }
    }

    private void CopyLogsFrom(string root, string artifactSubdir)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var ext in new[] { "*.log", "*.jsonl", "*.json" })
        {
            foreach (var file in Directory.EnumerateFiles(root, ext, SearchOption.AllDirectories))
            {
                if (!ShouldCopyArtifactFile(file))
                {
                    Log($"Skipping secret-bearing artifact: {Path.GetRelativePath(root, file)}");
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file);
                var dest = Path.Combine(ArtifactDir, artifactSubdir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
    }

    private static bool ShouldCopyArtifactFile(string file)
    {
        var fileName = Path.GetFileName(file);
        if (fileName.Equals("gateways.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("device-key", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("gateways", StringComparison.OrdinalIgnoreCase));
    }

    private const string TrayExecutableOverrideEnvironmentVariable = "OPENCLAW_E2E_TRAY_EXE";

    private static string LocateTrayExe() => ResolveTrayExecutable(
        Environment.GetEnvironmentVariable(TrayExecutableOverrideEnvironmentVariable),
        repoRoot: null);

    internal static string ResolveTrayExecutable(string? overridePath, string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var executable = Path.GetFullPath(overridePath);
            // The release gate must exercise its verified artifact, never silently
            // fall back to a checkout build when that artifact is unavailable.
            if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable))
            {
                throw new FileNotFoundException(
                    $"{TrayExecutableOverrideEnvironmentVariable} must name an existing .exe file.",
                    executable);
            }

            return executable;
        }

        repoRoot ??= FindRepoRoot();
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
            throw new InvalidDataException($"Could not locate TargetFramework in {projectPath}");

        return targetFramework;
    }

    private static string FindRepoRoot()
    {
        // Prefer env var for worktree support
        var envRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
            return envRoot;

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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private void ReleaseGatewayPortLease()
    {
        _gatewayPortLease?.Dispose();
        _gatewayPortLease = null;
    }

    private void Log(string message)
    {
        var logLine = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [E2E] {message}";
        Console.WriteLine(logLine);
        try
        {
            File.AppendAllText(Path.Combine(ArtifactDir, "e2e-fixture.log"), logLine + Environment.NewLine);
        }
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        catch { /* best effort */ }
    }
}
