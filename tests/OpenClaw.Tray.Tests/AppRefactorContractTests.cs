using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class AppRefactorContractTests
{
    [Fact]
    public void Startup_UsesConnectionManagerAsOnlyGatewayClientOwner()
    {
        var source = ReadAppSources();

        Assert.Contains("new CredentialResolver", source);
        Assert.Contains("new GatewayClientFactory", source);
        Assert.Contains("new NodeConnector", source);
        Assert.Contains("_connectionManager = new GatewayConnectionManager", source);
        Assert.Contains("nodeConnector.ClientCreated +=", source);
        Assert.Contains("_nodeService.AttachClient(args.Client, args.BearerToken)", source);
        Assert.Contains("_connectionManager.OperatorClientChanged += OnOperatorClientChanged", source);
        Assert.Contains("_connectionManager.StateChanged += OnManagerStateChanged", source);
        Assert.DoesNotMatch(new Regex(@"\bnew\s+OpenClawGatewayClient\s*\(", RegexOptions.Multiline), source);
        Assert.DoesNotMatch(new Regex(@"\bnew\s+WindowsNodeClient\s*\(", RegexOptions.Multiline), source);
    }

    [Fact]
    public void Startup_Order_PreservesInitializationInvariants()
    {
        var source = ReadAppSources();

        AssertInOrder(
            source,
            "AppUserModelIdRegistrar.RegisterCurrentProcess(AppIdentity.AppUserModelId);",
            "appUserModelIdRegistration.Attempted",
            "_settings = new SettingsManager();",
            "CheckForUpdatesAsync();",
            "ToastNotificationManagerCompat.OnActivated += OnToastActivated;",
            "InitializeTrayIcon();",
            "_gatewayRegistry = new GatewayRegistry",
            "_connectionManager = new GatewayConnectionManager",
            "await ShowOnboardingAsync();",
            "EnsureNodeService(_settings);",
            "InitializeGatewayClient();",
            "await _activationRouter.StartForwardedActivationListenerAsync(this, CancellationToken.None);");
    }

    [Fact]
    public void Startup_WslKeepAlive_IsOwnedByDedicatedService()
    {
        var source = ReadAppSources();
        var startup = ExtractMethod(source, "OnLaunchedAsync");
        var service = ReadWslKeepAliveServiceSource();

        Assert.Contains("new WslGatewayKeepAliveService(() => _settings, () => _gatewayRegistry)", startup);
        Assert.Contains("Task.Run(wslKeepAlive.TryEnsureAsync)", startup);

        foreach (var duplicateMethod in new[]
        {
            "TryEnsureLocalGatewayKeepAliveAsync",
            "StopStaleLocalGatewayKeepAliveAsync",
            "ReadKeepAliveMarkerDistroNames",
            "ReadSetupStateDistroNameAsync",
            "StopKeepAliveProcessesForDistro",
            "DeleteKeepAliveMarker",
            "GetProcessCommandLine",
            "ResolveWslExePath",
            "ResolveLocalGatewayDistroNameAsync",
        })
        {
            Assert.DoesNotContain(duplicateMethod, source);
        }

        Assert.Contains("public async Task TryEnsureAsync()", service);
        Assert.Contains("StopStaleLocalGatewayKeepAliveAsync", service);
        Assert.Contains("ReadKeepAliveMarkerDistroNames", service);
        Assert.Contains("ReadSetupStateDistroNameAsync", service);
        Assert.Contains("StopKeepAliveProcessesForDistro", service);
        Assert.Contains("DeleteKeepAliveMarker", service);
        Assert.Contains("GetProcessCommandLine", service);
        Assert.Contains("ResolveWslExePath", service);
        Assert.Contains("ResolveLocalGatewayDistroNameAsync", service);
    }

    [Fact]
    public void ManagedLocalGatewayRepair_StaysDelegatedToDedicatedOwners()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = ReadAppSources();
        var startup = ExtractMethod(app, "OnLaunchedAsync");
        var monitor = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "ManagedLocalGatewayAutoRepairMonitor.cs"));
        var coordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "ManagedLocalGatewayRepairCoordinator.cs"));

        Assert.Contains("new OpenClawTray.Services.ManagedLocalGatewayRepairCoordinator(", startup);
        Assert.Contains("new OpenClawTray.Services.ManagedLocalGatewayAutoRepairMonitor(", startup);
        Assert.Contains("private async Task RunAsync(CancellationToken cancellationToken)", monitor);
        Assert.Contains("private async Task<bool> SafeProbeAsync", coordinator);
        Assert.Contains("private async Task<bool> VerifyAsync", coordinator);
        Assert.DoesNotContain("private async Task<bool> SafeProbeAsync", app);
        Assert.DoesNotContain("private async Task<bool> VerifyAsync", app);
        Assert.Contains("WslKeepAlivePolicy.IsSameSetupManagedGateway(", startup);
    }

    [Fact]
    public void GatewayRecordEdits_HoldSharedLifecycleLease()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var connectionPage = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));
        var statusWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "ConnectionStatusWindow.xaml.cs"));
        var directConnectService = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "GatewayDirectConnectService.cs"));
        var pageEdit = ExtractMethod(connectionPage, "DoDirectConnectFromAddFormAsync");
        var windowEdit = ExtractMethod(statusWindow, "OnDirectConnectAsync");

        Assert.Contains("_gatewayDirectConnectService.ConnectAsync(", pageEdit);
        Assert.DoesNotContain("BeginManualGatewayLifecycleOperationAsync", pageEdit);
        Assert.DoesNotContain("_gatewayRegistry.AddOrUpdate", pageEdit);
        AssertInOrder(
            directConnectService,
            "BeginManualGatewayLifecycleOperationAsync",
            "DisconnectAsync",
            "_registry.AddOrUpdate(candidate)");
        Assert.Contains("GatewayDirectConnectService", windowEdit);
        Assert.Contains("directConnectService.ConnectAsync(", windowEdit);
        Assert.DoesNotContain("BeginManualGatewayLifecycleOperationAsync", windowEdit);
        Assert.DoesNotContain("_registry.AddOrUpdate", windowEdit);
    }

    [Fact]
    public void SavedGatewaySwitch_LeavesActiveIdMutationToManager()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var connectionPage = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));
        var switchMethod = ExtractMethod(connectionPage, "OnConnectSavedGatewayAsync");

        Assert.DoesNotContain("_gatewayRegistry.SetActive(gwId)", switchMethod);
        AssertInOrder(
            switchMethod,
            "await _connectionManager.SwitchGatewayAsync(gwId)",
            "LoadSavedGateways()",
            "RefreshFromSnapshot(_lastSnapshot)");
    }

    [Fact]
    public void CredentialReplacementFlows_DoNotBlindlyClearDeviceTokens()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var manager = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Connection",
            "GatewayConnectionManager.cs"));
        var statusWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "ConnectionStatusWindow.xaml.cs"));
        var setupCode = ExtractMethod(manager, "ApplySetupCodeAsync");
        var sharedToken = ExtractMethod(manager, "ConnectWithSharedTokenAsync");
        var directConnect = ExtractMethod(statusWindow, "OnDirectConnectAsync");
        var pageDirectConnect = ExtractMethod(
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "OpenClaw.Tray.WinUI",
                "Pages",
                "ConnectionPage.xaml.cs")),
            "DoDirectConnectFromAddFormAsync");
        var directConnectService = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "GatewayDirectConnectService.cs"));
        var capabilityHandlers = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "App.CapabilityHandlers.cs"));

        Assert.DoesNotContain("ClearStoredTokens", setupCode);
        Assert.DoesNotContain("ClearStoredTokens", sharedToken);
        Assert.DoesNotContain("ClearStoredTokens", directConnect);
        Assert.Contains("directConnectService.ConnectAsync(", directConnect);
        Assert.Contains("PreserveExistingSharedTokenWhenMissing: true", directConnect);
        Assert.Contains("isolatedValidationTunnel", sharedToken);
        Assert.Contains("StartAsync(validationConfig", sharedToken);
        Assert.Contains("ValidateSharedTokenBeforeReplacementAsync(", sharedToken);
        Assert.Contains("_validationTunnelFactory()", sharedToken);
        Assert.Contains("StopAndDisposeValidationTunnelAsync(isolatedValidationTunnel)", sharedToken);
        Assert.DoesNotContain("ClearStoredTokens", pageDirectConnect);
        Assert.DoesNotContain("BeginTransactionalTokenClear", pageDirectConnect);
        Assert.Contains("BeginTransactionalTokenClear", directConnectService);
        Assert.Contains(
            "_gatewayDirectConnectService.SynchronizeSettingsWithCommittedGateway(record)",
            capabilityHandlers);
        Assert.DoesNotContain("if (result.GatewayCommitted)", capabilityHandlers);
    }

    [Fact]
    public void StatusWindowDirectConnect_WaitsForManagerStateBeforeReportingConnected()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var statusWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "ConnectionStatusWindow.xaml.cs"));
        var directConnect = ExtractMethod(statusWindow, "OnDirectConnectAsync");
        var setupConnect = ExtractMethod(statusWindow, "OnConnectAsync");
        var stateChanged = ExtractMethod(statusWindow, "OnManagerStateChanged");

        Assert.Contains("ConnectionStatus_Connecting", directConnect);
        Assert.Contains("ConnectionPage_ConnectedTo", directConnect);
        Assert.Contains("ConnectionStatus_Applying", setupConnect);
        Assert.DoesNotContain("ConnectionStatus_ConnectedTo", setupConnect);
        Assert.DoesNotContain("SetupCodeOutcome.Success =>", setupConnect);
        Assert.Contains("directConnectService.ConnectAsync(", directConnect);
        Assert.Contains("GatewayDirectConnectOutcome.Failed", directConnect);
        Assert.Contains("PreserveExistingSharedTokenWhenMissing: true", directConnect);
        AssertInOrder(
            directConnect,
            "ConnectionStatus_Connecting",
            "directConnectService.ConnectAsync(");
        Assert.Contains("snapshot.OverallState == OverallConnectionState.Error", stateChanged);
        Assert.Contains("snapshot.OperatorError", stateChanged);
        Assert.Contains("SetupCodeResult.Text = errorText", stateChanged);
        Assert.Contains("SetupCodeResult.Text = connectedText", stateChanged);
        Assert.Contains("OverallConnectionState.Degraded", stateChanged);
        Assert.Contains("HubWindow_Pill_Degraded", stateChanged);
        Assert.Contains("OverallConnectionState.Connected or OverallConnectionState.Ready", stateChanged);
        Assert.Contains("OverallConnectionState.Idle or OverallConnectionState.Disconnecting", stateChanged);
        Assert.Contains("ConnectionStatus_Disconnected", stateChanged);
        Assert.Contains("statusMessageGeneration", stateChanged);
        Assert.Contains("Volatile.Read(ref _statusMessageGeneration)", stateChanged);
        Assert.DoesNotContain("_registry.Save()", directConnect);
        Assert.DoesNotContain("SaveOrThrow()", directConnect);
        Assert.DoesNotContain("RollbackDirectConnectState(", statusWindow);
    }

    [Fact]
    public void DirectConnectRollback_RestoresNullActiveGatewayExactly()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var directConnectService = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "GatewayDirectConnectService.cs"));
        var rollback = ExtractMethod(directConnectService, "Rollback");

        Assert.Contains("_registry.SetActive(previousActiveId);", rollback);
        Assert.DoesNotContain("if (previousActiveId != null)", rollback);
    }

    [Fact]
    public void DirectConnectRollback_UsesTransactionalTokenClearAfterLifecycleLease()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var connectionPage = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));
        var directConnect = ExtractMethod(connectionPage, "DoDirectConnectFromAddFormAsync");
        var directConnectService = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "GatewayDirectConnectService.cs"));
        var serviceConnect = ExtractMethod(directConnectService, "ConnectAsync");
        var rollback = ExtractMethod(directConnectService, "Rollback");

        Assert.Contains("_gatewayDirectConnectService.ConnectAsync(", directConnect);
        Assert.DoesNotContain("_gatewayRegistry.AddOrUpdate", directConnect);
        Assert.DoesNotContain("BeginTransactionalTokenClear", directConnect);
        AssertInOrder(
            serviceConnect,
            "BeginManualGatewayLifecycleOperationAsync",
            "var previousActiveId = _registry.ActiveGatewayId",
            "await _connectionManager.DisconnectAsync()",
            "BeginTransactionalTokenClear(identityDir, _logger)");
        AssertInOrder(
            serviceConnect,
            "BeginTransactionalTokenClear(identityDir, _logger)",
            "ConnectAndWaitForTerminalStateAsync(",
            "await _connectionManager.DisconnectAsync()",
            "Rollback(");
        Assert.Contains("if (!clearResult.Success)", serviceConnect);
        Assert.Contains("candidateRegistryCommitted", serviceConnect);
        AssertInOrder(
            serviceConnect,
            "_registry.Save();",
            "candidateRegistryCommitted = true",
            "BeginTransactionalTokenClear(identityDir, _logger)");
        AssertInOrder(
            rollback,
            "_registry.Save();",
            "RestoreTransactionalTokenClear(");
        Assert.Contains("RestoreTransactionalTokenClear(", rollback);
        Assert.Contains("DeviceTokenRestoreOutcome.Superseded", rollback);
        Assert.Contains("DeviceTokenRestoreOutcome.Failed", rollback);
        Assert.Contains("ReconcileSettings(candidate)", rollback);
        Assert.Contains("previousSettings.Restore(_settings)", rollback);
        Assert.Contains("_reconcileRuntimeTunnel()", rollback);
    }

    [Fact]
    public void DirectConnectTransaction_StaysOutOfConnectionPage()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));
        var app = ReadAppSources();
        var service = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "GatewayDirectConnectService.cs"));
        var pageMethod = ExtractMethod(page, "DoDirectConnectFromAddFormAsync");

        Assert.Contains("new GatewayDirectConnectService(", app);
        Assert.Contains("_gatewayDirectConnectService.ConnectAsync(", pageMethod);
        Assert.DoesNotContain("BeginManualGatewayLifecycleOperationAsync", pageMethod);
        Assert.DoesNotContain("_gatewayRegistry.AddOrUpdate", pageMethod);
        Assert.DoesNotContain("_gatewayRegistry.SetActive", pageMethod);
        Assert.DoesNotContain("BeginTransactionalTokenClear", pageMethod);
        Assert.DoesNotContain("SaveOrThrow", pageMethod);
        Assert.DoesNotContain("RollbackDirectConnect", page);
        Assert.Contains("BeginManualGatewayLifecycleOperationAsync", service);
        Assert.Contains("BeginTransactionalTokenClear", service);
        Assert.Contains("RestoreTransactionalTokenClear", service);
    }

    [Fact]
    public void BrowserAuthorization_RequiresExactOwnedSshListener()
    {
        var source = ReadAppSources();

        Assert.Contains("uri.Port != browserForwardPort", source);
        Assert.Contains("IsOwnedListenerReadyAsync(", source);
        Assert.Contains("uri.Port,", source);
        Assert.DoesNotContain("_sshTunnelService?.IsActive == true", source);
    }

    [Fact]
    public void McpOnlyStartup_DoesNotRequireGatewayCredentials()
    {
        var source = ReadAppSources();

        var method = ExtractMethod(source, "TryStartLocalMcpOnlyNode");
        Assert.Contains("!_settings.EnableMcpServer || _settings.EnableNodeMode", method);
        Assert.Contains("EnsureNodeService(_settings)", method);
        Assert.Contains("StartLocalOnlyAsync()", method);
        Assert.Contains("McpRuntimeStatePolicy.PlanStartupNotification", method);
        Assert.Contains("ApplyMcpStartupNotificationPlan", method);
        Assert.Contains("WireAppCapabilityHandlers()", method);
        AssertInOrder(method, "nodeService.StartLocalOnlyAsync()", "WireAppCapabilityHandlers()");
        AssertInOrder(method, "WireAppCapabilityHandlers()", "Started MCP-only node service without gateway connection");

        var init = ExtractMethod(source, "InitializeGatewayClient");
        AssertInOrder(init, "TryStartLocalMcpOnlyNode();", "Gateway URL not configured");
        AssertInOrder(init, "TryStartLocalMcpOnlyNode()", "No stored device token");
        Assert.Contains("catch (DeviceIdentityLoadException ex)", init);
        Assert.Contains("ShowTransientConnectionError(ex.Message)", init);
        AssertInOrder(
            init,
            "catch (DeviceIdentityLoadException ex)",
            "ShowTransientConnectionError(ex.Message)",
            "TryStartLocalMcpOnlyNode()",
            "return;");
        Assert.Contains("Active gateway has no usable credential", source);
    }

    [Fact]
    public void LegacyCredentialMigration_StaysRegistryBacked()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "TryMigrateLegacyGatewaySettings");

        Assert.Contains("_gatewayRegistry.MigrateFromSettings", method);
        Assert.Contains("_settings.LegacyToken", method);
        Assert.Contains("_settings.LegacyBootstrapToken", method);
        Assert.Contains("SettingsManager.SettingsDirectoryPath", method);
        Assert.DoesNotContain("SharedGatewayToken =", method);
        Assert.DoesNotContain("BootstrapToken =", method);
    }

    [Fact]
    public void LifecycleStatus_IsWrittenFromManagerSnapshotOnly()
    {
        var source = ReadAppSources();
        var managerHandler = ExtractMethod(source, "OnManagerStateChanged");
        var rawHandler = ExtractMethod(source, "OnGatewayConnectionStatusChanged");

        Assert.Contains("ConnectionStatusPresenter.ToLegacyStatus(snap)", managerHandler);
        Assert.Contains("_trayController?.ApplyConnectionState(mapped, snap.OverallState)", managerHandler);
        Assert.Contains("_windowManager?.UpdateHubTitleBarStatus(snap, mapped)", managerHandler);
        Assert.Contains("_appState.Status = mapped", managerHandler);
        Assert.DoesNotContain("_appState.Status =", rawHandler);
        Assert.DoesNotContain("SyncConnectionToggle(status)", rawHandler);
        Assert.DoesNotContain("RunHealthCheckAsync()", rawHandler);
        Assert.DoesNotContain("TryConnectLocalNodeServiceAsync()", rawHandler);
    }

    [Fact]
    public void Dashboard_SurfacesSshTunnelConfigurationFailure()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "OpenDashboard");

        Assert.Contains("if (!EnsureSshTunnelConfigured())", method);
        Assert.Contains("_toastService?.ShowToast", method);
        Assert.Contains("Check SSH tunnel settings and logs.", method);
    }

    [Fact]
    public void SshTunnelExit_RecoversActiveRegistryGatewayThroughConnectionManager()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "OnSshTunnelExitedAsync");

        Assert.Contains("var connectionManager = _connectionManager;", method);
        Assert.Contains("tunnelService?.TryMarkRestarting(tunnelExit) != true", method);
        Assert.Contains("await connectionManager.RecoverSshTunnelAsync(tunnelExit)", method);
        Assert.Contains("tunnelService.TryRestart(tunnelExit)", method);
        Assert.Contains("tunnelService.TryMarkRecoveryFailed(tunnelExit", method);
        Assert.Contains("_sshTunnelRecoveryBudget.TryReserve(", method);
        Assert.Contains("_sshTunnelRecoveryBudget.ReportRecovered(tunnelExit)", method);
        Assert.DoesNotContain("_gatewayRegistry?.GetActive()", method);
        Assert.DoesNotContain("_settings?.UseSshTunnel", method);
        Assert.DoesNotContain("_sshTunnelService.EnsureStarted", method);
    }

    [Fact]
    public void UserSshRestart_StaysDelegatedToConnectionManager()
    {
        var source = ReadAppSources();
        var wrapper = ExtractMethod(source, "RestartSshTunnelAsync");
        var action = ExtractMethod(source, "RestartSshTunnelCoreAsync");

        Assert.Contains("_connectionManager.RestartSshTunnelAsync()", wrapper);
        Assert.Contains("RestartSshTunnelAsync()", action);
        Assert.DoesNotContain("_sshTunnelService", action);
        Assert.DoesNotContain("EnsureSshTunnelConfigured", action);
        Assert.DoesNotContain("ReconnectWithSyncedBrowserProxyForward", action);
    }

    [Fact]
    public void ConnectionIssueNotification_PrefersNodeOwnedFailuresBeforeGenericGatewayError()
    {
        var source = ReadAppSources();

        AssertInOrder(
            source,
            "snapshot.NodeState == RoleConnectionState.PairingRequired",
            "TryBuildNodeConnectionIssueNotification(snapshot",
            "if (snapshot.OverallState == OverallConnectionState.Error)");
        Assert.Contains("TryBuildNodeConnectionIssueNotification", source);
        Assert.Contains("snapshot.OperatorState == RoleConnectionState.Error", source);
    }

    [Fact]
    public void CommandCenter_UsesOverallStateBeforeLegacyStatus()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "CommandCenterStateBuilder.cs"));

        AssertInOrder(
            source,
            "if (overallState == OpenClaw.Connection.OverallConnectionState.Degraded)",
            "else if (_snapshot.Status == ConnectionStatus.Error)");
        Assert.Contains("_snapshot.Settings?.EnableMcpServer == true", source);
        Assert.Contains("!string.IsNullOrWhiteSpace(mcpStartupError)", source);
    }

    [Fact]
    public void AppSettingsSet_AppliesSettingsSavedLifecycle()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        AssertInOrder(
            method,
            "app.SettingsSetHandler = (name, value) =>",
            "_settings.Save();",
            "ApplySettingsSavedAndWait();",
            "McpRuntimeStatePolicy.GetSettingsSetError",
            "return new { error = runtimeError };",
            "return new { name, value = prop.GetValue(_settings) };");
    }

    [Fact]
    public void OnSettingsSaved_AppliesMcpStartupNotificationPlan()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "ApplyMcpRuntime");

        Assert.Contains("nodeService?.SetMcpEnabled(settings.EnableMcpServer)", method);
        Assert.Contains("McpRuntimeStatePolicy.PlanStartupNotification", method);
        Assert.Contains("ApplyMcpStartupNotificationPlan", method);
        AssertInOrder(
            method,
            "nodeService?.SetMcpEnabled(settings.EnableMcpServer)",
            "ApplyMcpStartupNotificationPlan",
            "McpRuntimeStatePolicy.PlanStartupNotification");
    }

    [Fact]
    public void McpOnlyCapabilityReload_RebuildsTheSharedCapabilityList()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var appSource = ReadAppSources();
        var nodeServiceSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs"));

        var reconnect = ExtractMethod(appSource, "ReconnectWithSyncedBrowserProxyForward");
        var refresh = ExtractMethod(nodeServiceSource, "RefreshMcpOnlyCapabilities");

        AssertInOrder(
            reconnect,
            "SyncActiveGatewayBrowserProxyForward()",
            "_nodeService?.RefreshMcpOnlyCapabilities()",
            "_connectionManager?.ReconnectAsync()");
        Assert.Contains("lock (_clientLock)", refresh);
        Assert.Contains("if (!_enableMcpServer || _mcpServer == null || _nodeClient != null)", refresh);
        Assert.Contains("RegisterCapabilities();", refresh);
    }

    [Fact]
    public void McpRestart_RebuildsOnlyLocalTransportAndPreservesCapabilityOwners()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs"));
        var enable = ExtractMethod(source, "SetMcpEnabled");
        var refresh = ExtractMethod(source, "RefreshMcpOnlyCapabilities");
        var register = ExtractMethod(source, "RegisterCapabilities");
        var start = ExtractMethod(source, "StartMcpServer");

        AssertInOrder(
            enable,
            "lock (_clientLock)",
            "lock (_capabilitiesLock)",
            "McpRuntimeStatePolicy.PlanCapabilityEnable",
            "McpCapabilityEnablePlan.RebuildFromCurrentSettings",
            "RegisterCapabilities();");
        Assert.Contains("hasGatewayClient: _nodeClient != null", enable);
        Assert.Contains("hasCapabilities: _capabilities.Count != 0", enable);
        Assert.Contains("StartMcpServer();", enable);
        Assert.Contains("StopMcpServer();", enable);

        Assert.Contains("lock (_clientLock)", refresh);
        Assert.Contains("RegisterCapabilities();", refresh);
        AssertInOrder(
            register,
            "lock (_capabilitiesLock)",
            "_capabilities.Clear();",
            "_execApprovalsV2Handler ??=",
            "_textToSpeechService ??=",
            "_voiceService ??=",
            "} // end lock",
            "StartMcpServer();");

        Assert.DoesNotContain("_mcpOnlyCapabilities.Clear()", register);
        AssertInOrder(
            start,
            "merged.AddRange(_capabilities)",
            "merged.AddRange(_mcpOnlyCapabilities)");
    }

    [Fact]
    public void AppStatus_ReportsNodeStateFromManagerSnapshot()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        Assert.Contains("var snapshot = _connectionManager?.CurrentSnapshot;", method);
        Assert.Contains("overallState = snapshot?.OverallState.ToString()", method);
        Assert.Contains("operatorState = snapshot?.OperatorState.ToString()", method);
        Assert.Contains("nodeState = snapshot?.NodeState.ToString()", method);
        Assert.Contains("nodeConnected = snapshot?.NodeState == RoleConnectionState.Connected", method);
        Assert.Contains("nodePaired = snapshot?.NodePairingStatus == PairingStatus.Paired", method);
        Assert.Contains("nodePendingApproval = snapshot?.NodeState == RoleConnectionState.PairingRequired", method);
        Assert.Contains("nodeError = snapshot?.NodeError", method);
        Assert.Contains("operatorDeviceId = snapshot?.OperatorDeviceId", method);
    }

    [Fact]
    public void AppMenu_StatusItemIncludesManagerSnapshotState()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        Assert.Contains("app.MenuHandler = () =>", method);
        Assert.Contains("overallState = snapshot?.OverallState.ToString()", method);
        Assert.Contains("nodeState = snapshot?.NodeState.ToString()", method);
        Assert.Contains("nodeError = snapshot?.NodeError", method);
    }

    [Fact]
    public void AppChatQueueMcpHandlers_AreWiredToAppCapability()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "WireAppCapabilityHandlers");

        Assert.Contains("app.ChatQueueListHandler = ListQueuedChatMessagesForMcpAsync;", method);
        Assert.Contains("app.ChatQueueCancelHandler = CancelQueuedChatMessageForMcpAsync;", method);
    }

    [Fact]
    public void AppChatSnapshot_IncludesQueuePayload()
    {
        var source = ReadAppSources();
        var snapshotMethod = ExtractMethod(source, "BuildChatSnapshotPayload");
        var queueMethod = ExtractMethod(source, "BuildChatQueuePayload");
        var queueMessageMethod = ExtractMethod(source, "ToMcpQueuedMessage");
        var cancelMethod = ExtractMethod(source, "CancelQueuedChatMessageForMcpAsync");

        Assert.Contains("queue = BuildChatQueuePayload(snapshot, resolvedThreadId, filterToThread: false)", snapshotMethod);
        Assert.Contains("snapshot.QueuedMessagesByThread", queueMethod);
        Assert.Contains("sendState = message.SendState.ToString()", queueMessageMethod);
        Assert.Contains("canCancel = CanCancelQueuedMessage(message)", queueMessageMethod);
        Assert.Contains("canceled = await provider.CancelQueuedMessageAsync(resolvedThreadId, queuedMessageId)", cancelMethod);
        Assert.Contains("Queued message is already sending and cannot be canceled", cancelMethod);
        Assert.Contains("it may have started sending before cancellation was processed", cancelMethod);
    }

    [Fact]
    public void Startup_NodeOnlyReconnect_UsesNodeCredentialAndLegacyIdentityFallback()
    {
        var source = ReadAppSources();
        var connectMethod = ExtractMethod(source, "TryConnectGatewayIfCredentialAvailable");
        var nodeCredentialMethod = ExtractMethod(source, "ResolveStartupNodeCredential");

        Assert.Contains("ResolveStartupNodeCredential(record, resolver, identityDir)", connectMethod);
        Assert.Contains("_connectionManager.ConnectNodeOnlyAsync(record.Id)", connectMethod);
        Assert.Contains("resolver.ResolveNodeDetailed(record, SettingsManager.SettingsDirectoryPath)", nodeCredentialMethod);
        Assert.Contains("ResolveStartupCredentialOrThrow", nodeCredentialMethod);
        Assert.Contains("TryCopyLegacyIdentityToGateway(record.Id, identityDir)", nodeCredentialMethod);
    }

    [Fact]
    public void Startup_CorruptActiveIdentity_StopsBeforeMcpFallbackAndShowsConnectionError()
    {
        var source = ReadAppSources();
        var connectMethod = ExtractMethod(source, "TryConnectGatewayIfCredentialAvailable");
        var resolutionMethod = ExtractMethod(source, "ResolveStartupCredentialOrThrow");

        Assert.Contains("catch (DeviceIdentityLoadException ex)", connectMethod);
        Assert.Contains("ShowTransientConnectionError(ex.Message)", connectMethod);
        Assert.Equal(2, Regex.Matches(connectMethod, "catch \\(DeviceIdentityLoadException ex\\)").Count);
        Assert.Equal(2, Regex.Matches(connectMethod, "ShowTransientConnectionError\\(ex.Message\\);\\s*return false;").Count);
        Assert.Contains("GatewayCredentialResolutionStatus.Unreadable", resolutionMethod);
        Assert.Contains("GatewayCredentialResolutionStatus.Corrupt", resolutionMethod);
        Assert.Contains("throw new DeviceIdentityLoadException", resolutionMethod);
    }

    [Fact]
    public void TrayMenu_CorruptIdentity_StillBuildsReconfigureSnapshotAndShowsConnectionError()
    {
        var source = ReadAppSources();
        var captureMethod = ExtractMethod(source, "CaptureTrayMenuSnapshot");

        Assert.Contains("catch (DeviceIdentityLoadException ex)", captureMethod);
        Assert.Contains("ShowTransientConnectionError(ex.Message)", captureMethod);
        Assert.Contains("hasExistingConfig = true", captureMethod);
        Assert.Contains("return new TrayMenuSnapshot", captureMethod);
    }

    [Fact]
    public void LaunchSetupProbe_CorruptIdentity_ShowsConnectionErrorWithoutStartingOnboarding()
    {
        var source = ReadAppSources();
        var launchMethod = ExtractMethod(source, "OnLaunchedAsync");

        Assert.Contains("catch (DeviceIdentityLoadException ex)", launchMethod);
        Assert.Contains("ShowTransientConnectionError(ex.Message)", launchMethod);
        AssertInOrder(
            launchMethod,
            "catch (DeviceIdentityLoadException ex)",
            "catch (Exception ex)");
    }

    [Fact]
    public void ToastActivation_RoutesOnUiThread()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "OnToastActivated");

        AssertInOrder(
            method,
            "var activationRouter = _activationRouter;",
            "activationRouter.PlanToast(args.Argument)",
            "activationRouter.DispatchPlanAsync(plan, this, CancellationToken.None)");
        Assert.DoesNotContain("_activationRouter.PlanToast", method);
        Assert.Contains("ObserveBackgroundFault(", method);

        var routerSource = ReadActivationRouterServiceSource();
        Assert.Contains("public ActivationPlan PlanToast(string? argument)", routerSource);

        var toastRouteSource = ReadToastActivationRouterSource();
        Assert.Contains("internal static ActivationRoute? PlanRoute(", toastRouteSource);
        Assert.Contains("case \"open_dashboard\"", toastRouteSource);
        Assert.Contains("case \"open_settings\"", toastRouteSource);
        Assert.Contains("case \"open_chat\"", toastRouteSource);
        Assert.Contains("case \"open_activity\"", toastRouteSource);
        Assert.Contains("case \"copy_pairing_command\"", toastRouteSource);

        var sinkSource = ReadAppActivationRouterSource();
        Assert.Contains("Task IActivationPlanSink.DispatchAsync(ActivationRoute route, CancellationToken cancellationToken)", sinkSource);
        Assert.Contains("_dispatcherQueue?.TryEnqueue(", sinkSource);
    }

    [Fact]
    public void ShowWebChat_ClearsStalePendingSessionKeyOnPlainOpen()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "ShowWebChat");

        Assert.Contains("PendingChatSessionKey = sessionKey;", method);
        Assert.Contains("_windowManager?.SetPendingChatSessionKey(sessionKey);", method);
        Assert.Contains("PendingChatSessionKey = null;", method);
        Assert.Contains("_windowManager?.SetPendingChatSessionKey(null);", method);
        AssertInOrder(
            method,
            "if (!string.IsNullOrEmpty(sessionKey))",
            "PendingChatSessionKey = sessionKey;",
            "else",
            "PendingChatSessionKey = null;",
            "ShowHub(\"chat\");");
    }

    [Fact]
    public void ChatWebView_KeepsBaseChatUrlSeparateFromPendingSessionKey()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "ChatPage.xaml.cs"));
        var init = ExtractMethod(source, "InitializeWebViewAsync");
        var readiness = ExtractMethod(source, "NavigateWhenChatReadyAsync");

        Assert.Contains("GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage)", init);
        Assert.DoesNotContain("GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage, _pendingWebViewSessionKey)", init);
        Assert.Contains("_chatUrl = chatUrl;", init);
        Assert.DoesNotContain("_pendingWebViewSessionKey = null;", init);
        Assert.Contains("NavigateWebViewToCurrentChatUrl()", readiness);
        Assert.DoesNotContain("WebView.CoreWebView2.Navigate(_chatUrl)", readiness);
    }

    [Fact]
    public void PermissionsPage_ExecApprovals_UsesAppOwnedStoreWithCas()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var pageSource = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Presentation", "PermissionsPageViewModel.cs"));

        Assert.Contains("_execApprovalsStore.GetSnapshotReadOnlyAsync()", viewModelSource);
        Assert.Contains("_execApprovalsStore.ReplaceAsync(baseHash, workingFile, _execApprovalsOrigin)", viewModelSource);
        Assert.Contains("ExecApprovalsMutationKind.AddRule", viewModelSource);
        Assert.Contains("ExecApprovalsMutationKind.RemoveRule", viewModelSource);
        Assert.DoesNotContain("CurrentApp.ExecApprovalsStore.GetSnapshot", pageSource);
        Assert.DoesNotContain("CurrentApp.ExecApprovalsStore.ReplaceAsync", pageSource);
        Assert.DoesNotContain("Path.Combine(CurrentApp.DataDirectoryPath, \"exec-approvals.json\")", pageSource);
        Assert.DoesNotContain("File.WriteAllText(tmpPath", pageSource);
    }

    [Fact]
    public void PermissionsPage_ExecPolicyEditor_IsExecutablePathAllowlistOnly()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml"));
        var resources = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Strings",
            "en-us",
            "Resources.resw"));

        Assert.Single(Regex.Matches(xaml, "<ComboBox ").Cast<Match>());
        Assert.DoesNotContain("x:Name=\"NewRuleAction\"", xaml);
        Assert.DoesNotContain("PermissionsPage_NewRuleAction", xaml);
        Assert.Contains("x:Name=\"ExecAllowlistPatternValidation\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"ExecAllowlistPatternValidation\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml);
        Assert.Matches(
            new Regex("x:Name=\"RulesEmptyState\"[\\s\\S]*?TextWrapping=\"Wrap\"[\\s\\S]*?Visibility=\"Collapsed\""),
            xaml);

        Assert.Contains("<value>Executable-path allowlist</value>", resources);
        Assert.Contains(
            "<value>Controls which executables the agent can launch on this node. The executable-path allowlist starts empty.</value>",
            resources);
        Assert.Contains(
            "<value>Allow Always creates an argument-bound entry for an eligible native .exe command. Entries added here are path-only and can allow matching executables with any arguments. Deny and Ask remain controlled by Default action. Changes save automatically.</value>",
            resources);
        Assert.Contains(
            "<value>No executable-path allowlist entries. Approve an eligible native .exe command with Allow Always, or add a path-only pattern such as **/hostname.exe.</value>",
            resources);
        Assert.Contains(
            "<value>Enter an executable-path pattern such as **/hostname.exe. Basename or command-text patterns such as hostname are invalid.</value>",
            resources);
        Assert.Contains("<value>Add entry</value>", resources);
        Assert.DoesNotContain("<value>Add Rule</value>", resources);
    }

    [Fact]
    public void PermissionsPage_ExecPolicyCopy_IsLocalizedAcrossSupportedLocales()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var stringsRoot = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings");
        var enUs = ReadReswValues(Path.Combine(stringsRoot, "en-us", "Resources.resw"));
        var localizedKeys = new[]
        {
            "PermissionsPage_TextBlock_17.Text",
            "PermissionsPage_TextBlock_28.Text",
        };
        var hostnameExampleKeys = new[]
        {
            "PermissionsPage_NoRulesYetAdd.Text",
            "PermissionsPage_ExecAllowlistPatternValidation.Text",
        };
        const string runtimeContractKey = "PermissionsPage_PatternsAreMatchedLeft.Text";

        foreach (var locale in new[] { "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            var localized = ReadReswValues(Path.Combine(stringsRoot, locale, "Resources.resw"));
            foreach (var key in localizedKeys)
            {
                var value = Assert.Contains(key, localized);
                Assert.NotEmpty(value);
                Assert.NotEqual(enUs[key], value);
            }

            foreach (var key in hostnameExampleKeys)
            {
                var value = Assert.Contains(key, localized);
                Assert.NotEmpty(value);
                Assert.Contains("hostname.exe", value, StringComparison.Ordinal);
                Assert.DoesNotContain("git.exe", value, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(enUs[key], value);
            }

            var runtimeContract = Assert.Contains(runtimeContractKey, localized);
            Assert.NotEmpty(runtimeContract);
            Assert.Contains(".exe", runtimeContract, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(enUs[runtimeContractKey], runtimeContract);
        }
    }

    [Fact]
    public void PermissionsPage_ExecPolicyValidation_PersistsUntilValidAdd()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs"));
        var addRule = ExtractMethod(source, "OnAddRule");
        var showValidation = ExtractMethod(source, "ShowExecAllowlistPatternValidation");
        var hideValidation = ExtractMethod(source, "HideExecAllowlistPatternValidation");

        AssertInOrder(
            addRule,
            "_viewModel is null",
            "!await _viewModel.TryAddExecApprovalRuleAsync(NewRulePattern.Text.Trim())",
            "ShowExecAllowlistPatternValidation();",
            "return;",
            "NewRulePattern.Text = string.Empty;",
            "HideExecAllowlistPatternValidation();");

        AssertInOrder(
            showValidation,
            "ExecAllowlistPatternValidation.Visibility = Visibility.Visible;",
            "AutomationProperties.SetHelpText(",
            "NewRulePattern,",
            "ExecAllowlistPatternValidation.Text);",
            "NewRulePattern.Focus(FocusState.Programmatic);",
            "DispatcherQueue.TryEnqueue(",
            "DispatcherQueuePriority.Low",
            "ExecAllowlistPatternValidation.Visibility == Visibility.Visible",
            "ExecAllowlistPatternValidation.StartBringIntoView(",
            "new BringIntoViewOptions { AnimationDesired = false });");
        Assert.DoesNotContain("UpdateLayout()", showValidation);
        AssertInOrder(
            hideValidation,
            "ExecAllowlistPatternValidation.Visibility = Visibility.Collapsed;",
            "AutomationProperties.SetHelpText(",
            "NewRulePattern,",
            "string.Empty);");
    }

    [Fact]
    public void App_ExecApprovalsStore_UsesRoamingProductionDataRoot()
    {
        var source = ReadAppSources();

        Assert.Contains("_execApprovalsStore ??= new ExecApprovalsStore(", source);
        Assert.Contains("AppIdentity.ResolveRoamingDataDirectory()", source);
    }

    [Fact]
    public void TrayArtifactCleanup_UsesActiveExecApprovalsStatePath()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine",
            "TrayArtifactCleanup.cs"));

        Assert.Contains("ExecApprovalsStore.ResolveFilePath(appDataDir)", source);
        Assert.Contains("legacyExecApprovalsPath", source);
    }

    [Fact]
    public void PermissionsPage_ExecPolicyRemoveButtons_HaveAccessibleNames()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs"));

        Assert.Contains("AutomationProperties.Name=\"{Binding RemoveRuleAutomationName}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"{Binding RemoveRuleAutomationId}\"", xaml);
        Assert.Contains("RemoveRuleAutomationName = $\"Remove allowlist entry {rule.Pattern}\"", codeBehind);
        Assert.Contains("RemoveRuleAutomationId = $\"RemoveExecPolicyRuleButton_{index}\"", codeBehind);
    }

    [Fact]
    public void Shutdown_Order_PreservesAwaitedTeardownBeforeExit()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "BuildShutdownPlan");

        AssertInOrder(
            method,
            "\"activation router\"",
            "ToastNotificationManagerCompat.OnActivated -= OnToastActivated",
            "_activationRouter = null",
            "activationRouter.DisposeAsync()",
            "\"global hotkey\"",
            "\"chat coordinator\"",
            "\"managed-local auto-repair monitor\"",
            "\"gateway client\"",
            "connectionManager.DisposeAsync()",
            "\"node service\"",
            "nodeService.DisposeAsync()",
            "\"standalone voice service\"",
            "standaloneVoiceService.DisposeAsync()",
            "\"ssh tunnel service\"",
            "\"pairing approval\"",
            "\"app state observers\"",
            "\"window manager\"",
            "\"tray menu window\"",
            "\"service provider\"",
            "\"tray icon\"",
            "\"single-instance mutex\"",
            "ExitApplication: Exit");

        var exitMethod = ExtractMethod(source, "ExitApplicationAsync");
        Assert.Contains("var plan = BuildShutdownPlan();", exitMethod);
        Assert.Contains("_shutdownCoordinator.ShutdownAsync(plan)", exitMethod);

        var coordinatorSource = ReadAppShutdownCoordinatorServiceSource();
        Assert.Contains("plan.BeginShutdown();", coordinatorSource);
        AssertInOrder(
            coordinatorSource,
            "plan.BeginShutdown();",
            "foreach (var step in plan.Steps)",
            "plan.ExitApplication();");
    }

    [Fact]
    public void Shutdown_AsyncResourceFieldsClearInFinally_AndActivationDetachesBeforeAwait()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "BuildShutdownPlan");

        AssertInOrder(
            method,
            "ToastNotificationManagerCompat.OnActivated -= OnToastActivated;",
            "ReferenceEquals(_activationRouter, activationRouter)",
            "_activationRouter = null;",
            "await activationRouter.DisposeAsync();");

        AssertAsyncResourceClearedInFinally(
            method,
            "var autoRepairMonitor = _managedLocalAutoRepairMonitor;",
            "var connectionManager = _connectionManager;",
            "await autoRepairMonitor.DisposeAsync();",
            "ReferenceEquals(_managedLocalAutoRepairMonitor, autoRepairMonitor)",
            "_managedLocalAutoRepairMonitor = null;");
        AssertAsyncResourceClearedInFinally(
            method,
            "var connectionManager = _connectionManager;",
            "steps.Add(new AppShutdownStep(\"OpenTelemetry endpoint\"",
            "await connectionManager.DisposeAsync();",
            "ReferenceEquals(_connectionManager, connectionManager)",
            "_connectionManager = null;");
        AssertAsyncResourceClearedInFinally(
            method,
            "var nodeService = _nodeService;",
            "var standaloneVoiceService = _standaloneVoiceService;",
            "await nodeService.DisposeAsync();",
            "ReferenceEquals(_nodeService, nodeService)",
            "_nodeService = null;");
        AssertAsyncResourceClearedInFinally(
            method,
            "var standaloneVoiceService = _standaloneVoiceService;",
            "steps.Add(new AppShutdownStep(\"ssh tunnel service\"",
            "await standaloneVoiceService.DisposeAsync();",
            "ReferenceEquals(_standaloneVoiceService, standaloneVoiceService)",
            "_standaloneVoiceService = null;");
    }

    [Fact]
    public void Setup_IsHostedInTrayAndUsesSelfRestartAfterCompletion()
    {
        var source = ReadAppSources() + Environment.NewLine + ReadWindowManagerSource();
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var welcomePage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        var wizardPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var progressPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        var updateCoordinator = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "UpdateCoordinator.cs"));
        var cliUninstall = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "CliUninstallHandler.cs"));
        var setupProgram = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine", "Program.cs"));
        var settingsPage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs"));
        var settingsManager = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "SettingsManager.cs"));
        var keepAlivePolicy = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "WslKeepAlivePolicy.cs"));
        var setupClassifier = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "SetupExistingGatewayClassifier.cs"));

        Assert.Contains("setupWindow = new SetupWindow(", source);
        Assert.Contains("dataDir: AppIdentity.ResolveRoamingDataDirectory()", source);
        Assert.Contains("localDataDir: AppIdentity.ResolveSetupLocalDataDirectory()", source);
        Assert.Contains("distroNameOverride: AppIdentity.SetupDistroName", source);
        Assert.Contains("gatewayPortOverride: AppIdentity.SetupGatewayPort", source);
        Assert.Contains("commandLineArgs: SetupWindowArgumentProjection.Project(", source);
        Assert.Contains("_callbacks.IsDeepLinkArg,", source);
        Assert.Contains("Environment.ProcessId)", source);
        Assert.Contains("SetupRunLock.TryAcquire(_dataDir", setupWindow);
        Assert.Contains("new SetupContext(", progressPage);
        Assert.Contains("step is not RunGatewayWizardStep", progressPage);
        Assert.Contains("config.SkipWizard || step is not WindowsNodeBootstrapContextStep", progressPage);
        Assert.Contains("_dataDir,", progressPage);
        Assert.Contains("_localDataDir);", progressPage);
        Assert.Contains("var dataDir = setupWindow.DataDir", welcomePage);
        Assert.Contains("SetupWindow.Active?.DataDir ?? SetupContext.ResolveDataDir()", wizardPage);
        Assert.Contains("await CompleteSetupAsync(generation)", wizardPage);
        Assert.Contains("ApplyWindowsNodeContextAsync", wizardPage);
        Assert.Contains("var pipeline = new SetupPipeline(", setupWindow);
        Assert.Contains("[new WindowsNodeBootstrapContextStep()]", setupWindow);
        Assert.Contains("rollbackOnFailureOverride: false", setupWindow);
        AssertInOrder(
            setupWindow,
            "_lifetimeCts.Cancel()",
            "await contextApplyTask",
            "_setupLock?.Dispose()");
        Assert.Contains("distroNameOverride: _config.DistroName", wizardPage);
        Assert.Contains("if (AppIdentity.IsDev)", updateCoordinator);
        Assert.Contains("Skipping release-channel update check in development build", updateCoordinator);
        Assert.Contains("\"Update_Message_Skipped_Dev\"", updateCoordinator);
        Assert.Contains("\"--data-dir\", AppIdentity.ResolveRoamingDataDirectory()", cliUninstall);
        Assert.Contains("\"--local-data-dir\", AppIdentity.ResolveSetupLocalDataDirectory()", cliUninstall);
        Assert.Contains("\"--distro-name\", AppIdentity.SetupDistroName", cliUninstall);
        Assert.Contains("\"--autostart-name\", AppIdentity.AutoStartRegistryName", cliUninstall);
        Assert.Contains("AppIdentity.SetupDistroName", settingsPage);
        Assert.Contains("AppIdentity.SetupGatewayUrl", settingsManager);
        Assert.Contains("AppIdentity.SetupDistroName", keepAlivePolicy);
        Assert.Contains("AppIdentity.SetupDistroName", setupClassifier);
        Assert.Contains("TrayArtifactCleanup.Run(ctx, preserveLogs, autoStartName, startupTaskName)", setupProgram);
        Assert.Contains("setupWindow.SetupCompleted += _callbacks.SetupCompleted", source);
        Assert.Contains("ShowGatewayWizardAsync", source);
        Assert.Contains("startAtGatewayInstalledMilestone: true", source);
        Assert.Contains("startAtGatewayInstalledMilestone", setupWindow);
        Assert.Contains("_persistStartupPreferenceOnComplete = false", setupWindow);
        Assert.Contains("_showStartupPreferenceOnComplete = false", setupWindow);
        Assert.Contains("CanNavigateToGatewayInstalledMilestone", setupWindow);
        Assert.Contains("RootFrame.Content is not ProgressPage { IsPipelineRunning: true }", setupWindow);
        Assert.Contains("RootFrame.Content is not WizardPage", setupWindow);
        Assert.Contains("TryNavigateToGatewayInstalledMilestone", setupWindow);
        Assert.Contains("setupWindow.TryNavigateToGatewayInstalledMilestone()", source);
        AssertInOrder(
            setupWindow,
            "SetupRunLock.TryAcquire",
            "if (startAtGatewayInstalledMilestone)",
            "NavigateToGatewayInstalledMilestone()");
        Assert.Contains("CanNavigateToWizard", setupWindow);
        // Direct onboarding may reuse an already-open idle setup window, but
        // must not cancel an in-progress install running on ProgressPage.
        Assert.Contains("EnsureSetupWindowAsync", source);
        Assert.Contains("!created && setupWindow is { IsClosed: false }", source);
        Assert.Contains("RestartAfterSetupAsync", source);
        Assert.Contains("\"--post-setup-restart\"", source);
        Assert.Contains("\"--wait-for-pid\"", source);
        Assert.Contains("\"--post-setup-launch\"", source);
        var activationRouterSource = ReadActivationRouterServiceSource();
        Assert.Contains("$\"{_protocolScheme}://chat\"", activationRouterSource);
        Assert.Contains("input.PostSetupLaunch, \"chat\"", activationRouterSource);
        Assert.Contains("WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs())", source);
        AssertInOrder(source, "WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs())", "_mutex = new Mutex");
        Assert.DoesNotContain("setupWindow.TryNavigateToWizard()", source);
        Assert.DoesNotContain("ResolveSetupEngineUiPath", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.UI.exe", source);
        Assert.DoesNotContain("Process.GetProcessesByName(\"OpenClaw.SetupEngine.UI\")", source);
        Assert.False(File.Exists(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "SetupWizardWindow.cs")));
    }

    [Fact]
    public void GatewayInstalledMilestone_ShowsInlineStatusIfWizardCannotStart()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        var onBoard = ExtractMethod(code, "Onboard_Click");

        Assert.Contains("x:Name=\"MilestoneStatusText\"", xaml);
        Assert.Contains("SetupWindow.Active?.TryNavigateToWizard() == true", onBoard);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", xaml);
        Assert.Contains("MilestoneStatusText.Text", onBoard);
        Assert.DoesNotContain("NavigateToWizard();", onBoard);
    }

    [Fact]
    public void SetupProgress_PreparesWslBeforeLocalAiDownloads()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));

        AssertInOrder(
            code,
            "(\"wsl-platform\", \"Prepare WSL\", [\"ensure-wsl-platform\"])",
            "(\"local-ai-engine\", \"Install Local AI\"",
            "(\"local-ai-model\", \"Download AI model\"");
        Assert.Contains(
            "(\"wsl-networking\", \"Connect WSL to Local AI\", [\"configure-local-ai-wsl-networking\"])",
            code);
        Assert.DoesNotContain("Verify Local AI before WSL setup", code);
    }

    [Fact]
    public void SetupWelcome_BlocksOnWslReadinessBeforeLocalAiDecisionUi()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var welcome = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml.cs"));
        var capabilities = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml.cs"));
        var startInstall = ExtractMethod(welcome, "StartInstallAsync");
        var detectLocalAi = ExtractMethod(welcome, "DetectLocalAiAvailabilityAsync");

        AssertInOrder(
            startInstall,
            "GetWslViabilityAsync(refresh: true)",
            "if (wslViability.BlocksSetup)",
            "Title = \"WSL2 is not ready\"",
            "PrimaryButtonText = \"Try again\"",
            "ExistingConfigDetector.Detect",
            "NavigateToCapabilities()");
        AssertInOrder(
            detectLocalAi,
            "GetWslViabilityAsync()",
            "if (wslViability.BlocksSetup)",
            "GetLocalAiHardwareAsync()");
        Assert.DoesNotContain("GetWslViabilityAsync", capabilities);
        Assert.DoesNotContain("WslViabilityKind", capabilities);
    }

    [Fact]
    public void SetupProgress_HidesEveryLocalAiOnlyGroupAndKeepsNonLocalPreviewActive()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        var buildRows = ExtractMethod(code, "BuildStepRows");
        var preview = ExtractMethod(code, "RenderProgressPreview");

        Assert.Contains("IsLocalAiOnlyGroup(stepIds)", buildRows);
        Assert.Contains("stepIds.All(stepId => stepId.Contains(\"local-ai\"", code);
        Assert.Contains("localAiPreview ? \"local-ai-model\" : \"wsl-create\"", preview);
        Assert.DoesNotContain("groupId.StartsWith(\"local-ai\"", buildRows);
        Assert.DoesNotContain(": 3;", preview);
    }

    [Fact]
    public void SetupCompletion_PersistsStartupChoiceBeforeRestart()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var method = ExtractMethod(source, "RequestSetupCompleted");

        Assert.Contains("if (_persistStartupPreferenceOnComplete)", method);
        Assert.Contains("_config.Settings.AutoStart = enableAutoStart", method);
        Assert.Contains("TraySettingsConfig.UpdateAutoStartInSettingsFile", method);
        AssertInOrder(
            method,
            "if (_persistStartupPreferenceOnComplete)",
            "_config.Settings.AutoStart = enableAutoStart",
            "TraySettingsConfig.UpdateAutoStartInSettingsFile",
            "handler.Invoke");
    }

    [Fact]
    public void SetupWindowOwnership_WaitsForCleanupBeforeAllowingAnotherRun()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var windowManager = ReadWindowManagerSource();
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));

        Assert.Contains("public Task CleanupCompleted => _cleanupCompleted.Task", setupWindow);
        AssertInOrder(
            setupWindow,
            "_lifetimeCts.Cancel()",
            "await contextApplyTask",
            "catch (OperationCanceledException)",
            "_setupLock?.Dispose()",
            "_cleanupCompleted.TrySetResult(true)");
        Assert.Contains("rollbackOnFailureOverride: false", setupWindow);
        AssertInOrder(
            windowManager,
            "while (_setupWindow is not null)",
            "await existingSetupWindow.WaitForInitialContentReadyAsync()",
            "if (!existingSetupWindow.IsClosed)",
            "await existingSetupWindow.CleanupCompleted",
            "_setupWindow = null",
            "new SetupWindow(");
        AssertInOrder(
            windowManager,
            "setupWindow.Closed += OnSetupClosed",
            "CompleteSetupCloseAsync(setupWindow)",
            "await setupWindow.CleanupCompleted",
            "_setupWindow = null");
    }

    [Fact]
    public void CompletePage_UsesCompletionArgsForStartupPreference()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var complete = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));
        var navigate = ExtractMethod(setupWindow, "NavigateToComplete");

        Assert.Contains("DefaultAutoStart: true", navigate);
        Assert.Contains("ShowStartupPreference: _showStartupPreferenceOnComplete", navigate);
        Assert.Contains("StartupToggle.IsOn = args.DefaultAutoStart", complete);
        Assert.Contains("StartupRow.Visibility = args.ShowStartupPreference ? Visibility.Visible : Visibility.Collapsed", complete);
        Assert.Contains("StartupRow.Visibility == Visibility.Visible && StartupToggle.IsOn", complete);
        Assert.DoesNotContain("StartupToggle.IsOn = true", complete);
    }

    [Fact]
    public void CompletePage_ShowsFailureMessageOnlyInErrorCard()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var complete = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));

        Assert.Contains("SubtitleText.Visibility = Visibility.Visible", complete);
        Assert.Contains("SubtitleText.Visibility = Visibility.Collapsed", complete);
        Assert.Contains("ErrorText.Text = errorMessage", complete);
        Assert.DoesNotContain("SubtitleText.Text = helpUrl is null", complete);
    }

    [Fact]
    public void CompletePage_OffersExactFallbackOnlyThroughTypedCompatibilityPath()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var complete = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));
        var progress = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));

        Assert.Contains("result.CompatibilityFailure", progress);
        Assert.Contains("GatewayReleasePolicy.CanRetryWithFallback(_config, failureKind)", setupWindow);
        Assert.Contains("GatewayReleasePolicy.TryApplyFallback(_config, out error)", setupWindow);
        Assert.Contains("Retry with validated fallback {args.GatewayFallbackVersion}", complete);
        Assert.Contains("FallbackButton.Visibility = args.CanRetryGatewayFallback", complete);
    }

    [Fact]
    public void CompletePage_OffersTypedRestartChoiceWithoutForcingApplicationsClosed()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml"));
        var complete = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CompletePage.xaml.cs"));
        var restartLauncher = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "WindowsRestartLauncher.cs"));
        var progress = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var deferRestart = ExtractMethod(complete, "RestartLaterButton_Click");

        Assert.Contains("restartRequired: result.RequiresRestart", progress);
        Assert.Contains("if (args.RequiresRestart)", complete);
        Assert.Contains("OpenClaw needs to restart Windows to continue the installation. Would you like to restart now?", complete);
        Assert.Contains("Content=\"Yes, restart now\"", xaml);
        Assert.Contains("Content=\"No, I'm not ready yet\"", xaml);
        Assert.Contains("Path.Combine(Environment.SystemDirectory, \"shutdown.exe\")", restartLauncher);
        Assert.Contains("ArgumentList = { \"/r\", \"/t\", \"0\" }", restartLauncher);
        Assert.DoesNotContain("\"/f\"", restartLauncher);
        Assert.Contains("SetupWindow.Active?.Close()", deferRestart);
        Assert.DoesNotContain("Process.", deferRestart);
        var restartNow = ExtractMethod(complete, "RestartNowButton_Click");
        var restartWindows = ExtractMethod(complete, "RestartWindowsAsync");
        var restartError = ExtractMethod(complete, "ShowRestartError");
        Assert.Contains("AsyncEventHandlerGuard.Run(", restartNow);
        Assert.Contains("RestartWindowsAsync", restartNow);
        Assert.Contains("ShowRestartError", restartNow);
        Assert.Contains("await s_windowsRestartLauncher.RestartAsync()", restartWindows);
        Assert.Contains("RestartNowButton.IsEnabled = true", restartError);
        Assert.Contains("RestartLaterButton.IsEnabled = true", restartError);
        Assert.Contains("public bool RequiresRestart { get; init; }", setupWindow);
        Assert.DoesNotContain("LocalAiFailureDetail? Detail = null,\n    bool RequiresRestart", setupWindow.Replace("\r\n", "\n"));
    }

    [Fact]
    public void CapabilitiesPage_PersistsSelectedProfileIntoRuntimeNodeSettings()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var method = ExtractMethod(source, "WriteCapabilities");

        Assert.Contains("config.Settings.ApplyCapabilities(caps)", method);
        Assert.Contains("config.Tailscale.TrustTailscaleAuth = TailscaleTrustAuthToggle.IsOn == true", method);
        AssertInOrder(
            method,
            "prop?.SetValue(caps, toggle.IsOn)",
            "config.Settings.ApplyCapabilities(caps)");
        Assert.Contains("_config.UsesBundledDefaultConfig", source);
        Assert.Contains("_treatBundledAllOnAsPlaceholder ? 1 : 2", source);
        Assert.Contains("return -1", source);
    }

    [Fact]
    public void CapabilitiesPage_PermissionProbeFaultsShowInlineWarning()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var click = ExtractMethod(source, "PrimaryClickAsync");
        var build = ExtractMethod(source, "BuildPermissionRows");

        Assert.Contains("!permissionsTask.IsCompletedSuccessfully", click);
        Assert.Contains("catch (Exception ex)", build);
        Assert.Contains("new InfoBar", build);
        Assert.Contains("Couldn't read Windows permission status", build);
        Assert.Contains("Review permissions later in Settings", build);
    }

    [Fact]
    public void CapabilitiesPage_RefreshesPermissionStateWhenSetupIsReactivated()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var activated = ExtractMethod(source, "SetupWindow_Activated");
        var refresh = ExtractMethod(source, "RefreshPermissionRowsAsync");

        Assert.Contains("_setupWindow.Activated += SetupWindow_Activated", source);
        Assert.Contains("_setupWindow.Activated -= SetupWindow_Activated", source);
        Assert.Contains("WindowActivationState.Deactivated", activated);
        Assert.Contains("RefreshPermissionRowsAsync(_permissionsTask)", activated);
        AssertInOrder(refresh, "await previousRefresh", "await BuildPermissionRows()");
    }

    [Fact]
    public void CapabilitiesPage_ExposesExplicitCustomCapabilitySetsForReview()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));
        var detectProfile = ExtractMethod(source, "DetectProfileIndex");

        Assert.Contains("x:Name=\"CapabilityExpander\"", xaml);
        Assert.Contains("\"Custom capabilities (review)\"", source);
        Assert.Contains("CapabilityExpander.IsExpanded = true", source);
        Assert.Contains("_treatBundledAllOnAsPlaceholder ? 1 : 2", detectProfile);
        Assert.Contains("return -1", detectProfile);
        Assert.Contains("toggle.Toggled += Capability_Toggled", source);
        AssertInOrder(
            source,
            "_treatBundledAllOnAsPlaceholder = _config.UsesBundledDefaultConfig",
            "_suppressProfile = true",
            "ApplyProfile(1)",
            "_suppressProfile = false",
            "_treatBundledAllOnAsPlaceholder = false");
        AssertInOrder(
            ExtractMethod(source, "Capability_Toggled"),
            "DetectProfileIndex()",
            "ProfileRadio.SelectedIndex = profileIndex",
            "UpdateCapabilityProfilePresentation(profileIndex)");
    }

    [Fact]
    public void CapabilitiesPage_DisclosesAlwaysOnDeviceStatusWithoutOfferingFalseToggle()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));

        Assert.DoesNotContain("(\"Device\", \"Device\"", source);
        Assert.DoesNotContain("[\"Canvas\", \"Screen\", \"Device\"]", source);
        Assert.Contains("_config.Capabilities.Device = true", source);
        Assert.Contains("Basic device info and status stay available while Node Mode is on.", xaml);
    }

    [Fact]
    public void CapabilitiesPage_AggregatesOnlyLocalAiHardwareAndNetworkingDiagnosis()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));
        Assert.Contains("LocalAiUnavailableDetailsButton", xaml);
        Assert.Contains("SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableDetailsDialogTitle\")", source);
        Assert.Contains("LocalAiInstallReviewCard.Visibility = Visibility.Visible", ExtractMethod(source, "ShowLocalAiUnavailable"));
        Assert.Contains("LocalAiAvailabilityReasons.Build", source);
        Assert.DoesNotContain("WslViability", source);
        Assert.Contains("This PC does not meet one or more Local AI requirements.", xaml);
        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: false)", ExtractMethod(source, "ShowLocalAiUnavailable"));
        Assert.Contains("Message=\"This PC does not meet one or more Local AI requirements.\"", xaml);
        Assert.Contains("<InfoBar.ActionButton>", xaml);
        Assert.True(
            xaml.IndexOf("x:Name=\"LocalAiUnavailablePanel\"", StringComparison.Ordinal) <
            xaml.IndexOf("x:Name=\"LocalAiInstallReviewCard\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CapabilitiesPage_FiltersModelsBySelectedGpuCapacityAndShowsMemoryEvidence()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        var diagnostics = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Shared", "Inference", "Catalog", "LocalInferenceEligibilityDiagnostics.cs"));
        var resources = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"));

        Assert.Contains("LocalInferenceEligibility.Evaluate(_localAiHardware!, model.Id)", source);
        Assert.Contains("eligibility.RequiredTotalMemoryBytes", diagnostics);
        Assert.Contains("eligibility.DetectedTotalMemoryBytes", diagnostics);
        Assert.Contains("model weights, KV cache, and runtime workspace", resources);
        Assert.Contains("SetupReviewSummaryBuilder.DisplayModelName(model)", source);
        Assert.Contains("(isRecommended ? \" (Recommended)\" : string.Empty)", source);
        Assert.Contains("SetupReviewSummaryBuilder.DisplayModelName(plan.Model)", source);
        Assert.Contains("bytes / (1024d * 1024d * 1024d)", diagnostics);
        Assert.Contains("GiB", resources);
        Assert.DoesNotContain(" ({FormatSize(model.Weights.SizeBytes)})", source);
        Assert.DoesNotContain("2 GiB runtime margin", source);
        Assert.DoesNotContain("HardwareProfile", source);
        Assert.DoesNotContain("RTX PRO 6000", source);
    }

    [Fact]
    public void WizardSecondaryButton_DoesNotSkipEntireWizardInErrorState()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var method = ExtractMethod(source, "SecondaryClickAsync");

        Assert.DoesNotContain("_errorState", method);
        Assert.DoesNotContain("SkipWizardAsync", method);
        Assert.Contains("SendCurrentAnswerAsync(skip: true)", method);
    }

    [Fact]
    public void WizardProgressPolling_UsesStepIdForTimeoutClassification()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));

        Assert.Contains("WizardTimeouts.ForStep(title, message, _stepId)", source);
    }

    [Fact]
    public void WizardResetInputs_RemovesOverflowMoreButton()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var reset = ExtractMethod(source, "ResetInputs");

        // The "More ▾" overflow button is a sibling of SelectOptions in the shared
        // StackPanel, so ResetInputs must remove it between steps or it leaks forward.
        Assert.Contains("_moreOptionsButton", reset);
        Assert.Contains("morePanel.Children.Remove(_moreOptionsButton)", reset);
        Assert.Contains("_moreOptionsButton = null", reset);
    }

    [Fact]
    public void WizardBack_IsUnavailableWithoutDedicatedGatewayBackRpc()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml"));
        var buildOptions = ExtractMethod(source, "BuildOptions");
        var expandMore = ExtractMethod(source, "ExpandMoreOptionsAsync");
        var isBackOption = ExtractMethod(source, "IsBackOption");

        Assert.DoesNotContain("WizardBackButton", xaml);
        Assert.DoesNotContain("WizardBack_Click", source);
        Assert.DoesNotContain("_stepHistory", source);
        Assert.DoesNotContain("ApplyPayloadAsync(previousPayload)", source);
        Assert.DoesNotContain("\"wizard.back\"", source);
        Assert.Contains("\"__back\"", isBackOption);
        Assert.Contains("\"back\"", isBackOption);
        Assert.Contains("!IsBackOption(o)", buildOptions);
        Assert.Contains("!IsBackOption(o)", expandMore);
        Assert.Contains("\"wizard.next\"", source);
        Assert.Contains("MoreOptionsButton", xaml);
        Assert.Contains("StartOver_Click", xaml);
        Assert.Contains("SkipWizard_Click", xaml);
    }

    [Fact]
    public void WizardStartup_KeepsRecoveryVisibleAndRejectsStaleConnections()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var start = ExtractMethod(source, "StartWizardAsync");
        var startOver = ExtractMethod(source, "StartOverAsync");
        var disposeStaleClient = ExtractMethod(source, "DisconnectAndDisposeClientAsync");

        Assert.DoesNotContain("HideRecoveryActions()", start);
        AssertInOrder(
            start,
            "var generation = AdvanceOperationGeneration();",
            "ShowRecoveryActions();",
            "await CancelCurrentSessionAsync();",
            "if (generation != _operationGeneration)",
            "var client = await ConnectClientAsync();",
            "if (generation != _operationGeneration)",
            "await DisconnectAndDisposeClientAsync(client);",
            "_client = client;",
            "SendWizardRequestAsync(\"wizard.start\"");
        AssertInOrder(
            startOver,
            "var generation = AdvanceOperationGeneration();",
            "await CancelCurrentSessionAsync();",
            "if (generation != _operationGeneration)",
            "await StartWizardAsync();");
        Assert.Contains("finally", disposeStaleClient);
        Assert.Contains("client.Dispose()", disposeStaleClient);
    }

    [Fact]
    public void WizardCompletion_AppliesWindowsNodeContextBeforeSummary()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var complete = ExtractMethod(source, "CompleteSetupAsync");
        var skip = ExtractMethod(source, "SkipWizardAsync");
        var primary = ExtractMethod(source, "PrimaryClickAsync");
        var finalizationError = ExtractMethod(source, "ShowFinalizationError");

        AssertInOrder(
            complete,
            "ApplyWindowsNodeContextAsync",
            "if (!contextResult.IsSuccess)",
            "NavigateToComplete(true");
        Assert.Contains("await CompleteSetupAsync(generation)", skip);
        Assert.Contains("_errorState = false", skip);
        AssertInOrder(
            primary,
            "if (_finalizationErrorState)",
            "_errorState = false",
            "await CompleteSetupAsync(_operationGeneration)",
            "await StartWizardAsync()");
        Assert.Contains("ShowFinalizationError", complete);
        Assert.Contains("Retry Windows integration", finalizationError);
    }

    [Fact]
    public void WizardConnect_UsesActiveGatewayRecordUrl()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var method = ExtractMethod(source, "ConnectClientAsync");

        Assert.Contains("GatewayClientEndpointResolver.Resolve(record)", method);
        Assert.Contains("new OpenClawGatewayClient(gatewayUrl, token", method);
        Assert.DoesNotContain("config.EffectiveGatewayUrl", method);
    }

    [Fact]
    public void WizardTerminalRestartRecovery_IsExactVersionManagedLocalAndFailClosed()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var connect = ExtractMethod(source, "ConnectClientAsync");
        var statusChanged = ExtractMethod(source, "OnWizardClientStatusChanged");
        var sendAnswer = ExtractMethod(source, "SendCurrentAnswerAsync");

        Assert.Contains(
            "GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync",
            connect);
        Assert.Contains("_expectedTerminalRestart", connect);
        Assert.Contains("_expectedTerminalRestart", statusChanged);
        Assert.Contains("_hostAccessPlan.CanControlWslGateway", sendAnswer);
        Assert.Contains("GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart", sendAnswer);
        Assert.Contains("WaitForReconnectAsync", sendAnswer);
        Assert.Contains("HasHandshakeSnapshot", source);
        Assert.Contains("HandshakeSucceeded", source);
        Assert.Contains("Disposed", source);
    }

    [Fact]
    public void Settings_OnboardCardRequiresActiveManagedWslGateway()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs"));

        Assert.Contains("x:Name=\"OpenClawOnboardCard\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.Contains("GatewayHostAccessClassifier.Classify(CurrentApp.Registry?.GetActive())", code);
        Assert.Contains("OpenClawOnboardCard.Visibility = activeGatewayAccess.CanControlWslGateway", code);
        Assert.Contains("CurrentApp.Registry?.Load();", code);
        Assert.Contains("OpenClawOnboardCard.Visibility = Visibility.Collapsed;", code);
    }

    [Fact]
    public void HubBackNavigation_PrunesUnavailableGatewayPages()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs"));

        Assert.Contains("RemoveUnavailableGatewayBackStackEntries", source);
        Assert.Contains("ContentFrame.BackStack.RemoveAt(i)", source);
        Assert.Contains("RemoveBackStackEntries(HubPageRegistry.IsGatewayPageTag)", source);
        Assert.Contains("RemoveUnavailableGatewayBackStackEntries();", ExtractMethod(source, "GoBack"));
        Assert.Contains("RemoveUnavailableGatewayBackStackEntries();", ExtractMethod(source, "UpdateGatewayNavVisibility"));
    }

    [Fact]
    public void SetupUiPages_DoNotOwnTrayProcessHandoff()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupUiDir = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI");
        var source = string.Join(
            "\n",
            Directory
                .EnumerateFiles(setupUiDir, "*.cs", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));

        Assert.Contains("SetupWindow.Active", source);
        Assert.Contains("RequestSetupCompleted", source);
        Assert.Contains("RequestAdvancedSetup", source);
        Assert.DoesNotContain("App.MainWindow", source);
        Assert.DoesNotContain("GetProcessesByName", source);
        Assert.DoesNotContain("Process.Kill", source);
        Assert.DoesNotContain("Environment.Exit", source);
        Assert.DoesNotContain("TrayExecutableResolver", source);
        Assert.DoesNotContain("OpenClaw.Tray.WinUI", source);
    }

    [Fact]
    public void SettingsLocalGatewayRemoval_UsesCancelableTrayChildProcess()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs"));

        Assert.Contains("ResolveCurrentExecutablePath()", source);
        Assert.Contains("psi.ArgumentList.Add(\"--uninstall\")", source);
        Assert.Contains("proc.WaitForExitAsync(_uninstallCts.Token)", source);
        Assert.Contains("proc.Kill(entireProcessTree: true)", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.Program.Main(setupArgs)", source);
        Assert.DoesNotContain("OpenClaw.SetupEngine.UI.exe", source);
    }

    [Fact]
    public void SetupUiImages_UseLibraryQualifiedAssetUris()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var setupUiDir = Path.Combine(root, "src", "OpenClaw.SetupEngine.UI");
        var xaml = string.Join(
            "\n",
            Directory
                .EnumerateFiles(setupUiDir, "*.xaml", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));

        Assert.Contains("ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/OpenClawMascot.png", xaml);
        Assert.DoesNotContain("ms-appx:///Assets/Setup/", xaml);
    }

    [Fact]
    public void SetupWelcomePage_RunsExistingConfigDetectionOffUiThread()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        var method = ExtractMethod(source, "StartInstallAsync");

        Assert.Contains("NextButton.IsEnabled = false", method);
        Assert.Contains("InstallCheckProgress.IsActive = true", method);
        Assert.Contains("InstallCheckProgress.Visibility = Visibility.Visible", method);
        Assert.Contains("var setupWindow = SetupWindow.Active", method);
        Assert.Contains("await Task.Run(() => ExistingConfigDetector.Detect", method);
        Assert.Contains("setupWindow.IsClosed || xamlRoot is null", method);
        Assert.Contains("!setupWindow.IsClosed", method);
        Assert.Contains("InstallCheckProgress.IsActive = false", method);
        Assert.Contains("InstallCheckProgress.Visibility = Visibility.Collapsed", method);
        Assert.Contains("NextButton.IsEnabled = true", method);

        // The busy state is carried by the progress ring alone. Overwriting the option
        // title hid which option was being acted on for as long as the check ran.
        Assert.DoesNotContain("InstallTitle.Text", method);

        // A failed inspection must stay recoverable instead of ending the flow on the
        // recommended option with no way forward.
        Assert.Contains("PrimaryButtonText = \"Try again\"", method);

        Assert.Contains("AutomationProperties.AutomationId=\"WelcomeInstallCheckProgress\"", File.ReadAllText(
            Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml")));
        AssertInOrder(
            method,
            "NextButton.IsEnabled = false",
            "await Task.Run(() => ExistingConfigDetector.Detect",
            "setupWindow.IsClosed || xamlRoot is null",
            "dialog.ShowAsync()",
            "setupWindow.NavigateToCapabilities()");
    }

    [Fact]
    public void SetupWelcomePage_RetriesWslReadinessWithFreshInspection()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var welcome = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml.cs"));
        var setupWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));
        var method = ExtractMethod(welcome, "StartInstallAsync");

        Assert.Contains("GetWslViabilityAsync(bool refresh = false)", setupWindow);
        Assert.Contains("GetWslViabilityAsync(refresh: true)", method);
        Assert.Contains("PrimaryButtonText = \"Try again\"", method);
        Assert.Contains("if (retry != ContentDialogResult.Primary)", method);
        AssertInOrder(
            method,
            "while (true)",
            "GetWslViabilityAsync(refresh: true)",
            "if (wslViability.BlocksSetup)",
            "PrimaryButtonText = \"Try again\"",
            "if (retry != ContentDialogResult.Primary)");
    }

    [Fact]
    public void SetupWelcomePage_KeepsNavigationOutsideScrollableSemanticChoices()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml"));
        var welcomePage = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        var setupWindow = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));

        Assert.Contains("<ScrollViewer Grid.Row=\"1\"", xaml);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("<ListView x:Name=\"GatewayChoiceSelector\"", xaml);
        Assert.Contains("SelectionMode=\"Single\"", xaml);
        Assert.Contains("ScrollViewer.VerticalScrollMode=\"Disabled\"", xaml);
        Assert.Contains("SelectionChanged=\"GatewayChoice_SelectionChanged\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"WelcomeInstallLocalGatewayChoice\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"WelcomeConnectExistingGatewayChoice\"", xaml);
        Assert.DoesNotContain("PointerPressed=", xaml);
        // The old duplicate card-selection chrome and its border-swap logic must not return.
        Assert.DoesNotContain("x:Name=\"InstallCard\"", xaml);
        Assert.DoesNotContain("x:Name=\"ConnectCard\"", xaml);
        Assert.DoesNotContain("UpdateCardSelection", welcomePage);
        AssertInOrder(xaml, "<ScrollViewer Grid.Row=\"1\"", "</ScrollViewer>", "<Grid Grid.Row=\"2\"");
        Assert.DoesNotContain("GatewayChoiceSelector.SelectedIndex = 0;", welcomePage);
        Assert.Contains("_suppressSelectionWrite = true", welcomePage);
        Assert.Contains("SetupWindow.Active?.IsWelcomeInstallSelected ?? true", welcomePage);
        Assert.Contains("SetupWindow.Active?.SetWelcomeInstallSelected(installSelected)", welcomePage);
        Assert.Contains("private bool _isWelcomeInstallSelected = true", setupWindow);
        Assert.Contains("public bool IsWelcomeInstallSelected => _isWelcomeInstallSelected", setupWindow);
    }

    [Fact]
    public void WizardErrorState_UsesMoreOptionsAndPreservesTranscriptOnGatewayRestart()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WizardPage.xaml.cs"));
        var showError = ExtractMethod(source, "ShowError");
        var restart = ExtractMethod(source, "RestartGatewayAsync");

        Assert.Contains("SecondaryButton.Visibility = Visibility.Collapsed", showError);
        Assert.Contains("ShowRecoveryActions()", showError);
        Assert.DoesNotContain("SecondaryButton.Content = \"Skip wizard\"", showError);
        Assert.Contains("StartWizardAsync(clearTranscript: false)", restart);
    }

    [Fact]
    public void TrayIcon_UpdateDelegatesToCoordinator()
    {
        var appMethod = ExtractMethod(ReadAppSources(), "UpdateTrayIcon");
        var controller = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "TrayController.cs"));
        var controllerMethod = ExtractMethod(controller, "RefreshIcon");

        Assert.Contains("_trayController?.RefreshIcon()", appMethod);
        Assert.Contains("_trayIconCoordinator?.UpdateTrayIcon()", controllerMethod);
        Assert.DoesNotContain("SetIcon(", controllerMethod);
        Assert.DoesNotContain("private void ApplyTrayTooltip", controller);
    }

    [Fact]
    public void TrayCoordinator_UpdateGuardsLivenessBeforeTouchingIcon()
    {
        var source = ReadCoordinatorSource();
        var method = ExtractMethod(source, "UpdateTrayIcon");

        // A queued update can run after shutdown disposes the tray icon, so the
        // coordinator must bail on the liveness check before it ever calls SetIcon.
        var guardIndex = method.IndexOf("_isAlive()", StringComparison.Ordinal);
        var setIconIndex = method.IndexOf("SetIcon(", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "UpdateTrayIcon must check the liveness guard");
        Assert.True(setIconIndex >= 0, "UpdateTrayIcon must still set the icon");
        Assert.True(guardIndex < setIconIndex, "Liveness guard must run before SetIcon");
    }

    [Fact]
    public void TrayCoordinator_UsesStatusBadgedLobsterIcon()
    {
        var method = ExtractMethod(ReadCoordinatorSource(), "UpdateTrayIcon");

        // The tray lobster mirrors the companion-app status dot instead of the
        // static openclaw.ico, so it must resolve the accent and the badged icon.
        Assert.Contains("ConnectionStatusPresenter.Accent(", method);
        Assert.Contains("StatusBadgeIconFactory.GetBadgedIconPath(", method);
        Assert.DoesNotContain("\"openclaw.ico\"", method);
    }

    [Fact]
    public void HubWindow_DesktopIconMirrorsStatusAccent()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs"));

        // The desktop/taskbar icon is refreshed from the same accent as the pill.
        var apply = ExtractMethod(source, "ApplyWindowStatusIcon");
        Assert.Contains("StatusBadgeIconFactory.GetBadgedIconPath(accent)", apply);

        // Both status-update paths must repaint the window icon.
        var update = ExtractMethod(source, "UpdateTitleBarStatus");
        Assert.Contains("ApplyWindowStatusIcon(accent)", update);
    }

    [Fact]
    public void AppNotifications_ConnectionIssueUsesStableDedupeKey()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "UpdateConnectionIssueNotification");

        Assert.Contains("private const string ConnectionIssueNotificationDedupeKey = \"connection:issue\"", source);
        Assert.Contains("ConnectionIssueNotificationDedupeKey", method);
        Assert.DoesNotContain("$\"connection:{key}\"", method);
    }

    [Fact]
    public void AppNotifications_SandboxRiskProbeRunsOffUiPath()
    {
        var source = ReadAppSources();
        var publishMethod = ExtractMethod(source, "PublishSandboxRiskNotificationIfNeeded");
        var probeMethod = ExtractMethod(source, "StartSandboxRiskProbeIfNeeded");

        Assert.DoesNotContain("MxcAvailability.Probe", publishMethod);
        Assert.Contains("Task.Run(() => MxcAvailability.Probe", probeMethod);
        Assert.Contains("ContinueWith", probeMethod);
    }

    [Fact]
    public void AppNotifications_SandboxRiskUsesStableDedupeKey()
    {
        var source = ReadAppSources();

        Assert.Contains("private const string SandboxRiskNotificationId = \"sandbox:risk\"", source);
        Assert.Contains("private const string SandboxRiskNotificationDedupeKey = \"sandbox:risk\"", source);
        Assert.Contains("SandboxRiskNotificationDedupeKey", source);
        Assert.Contains("id: SandboxRiskNotificationId", source);
        Assert.DoesNotContain("$\"sandbox:{riskKey}\"", source);
    }

    [Fact]
    public void AppNotifications_SandboxRiskMessageReflectsStrictFallbackBlocking()
    {
        var source = ReadAppSources();
        var method = ExtractMethod(source, "PublishSandboxRiskNotification", parameterHint: "MxcAvailability");

        Assert.Contains("SystemRunBlockHostFallbackWhenMxcUnavailable", method);
        Assert.Contains("AppNotification_SandboxUnavailableBlocked_Title", method);
        Assert.Contains("AppNotification_SandboxUnavailableBlocked_MessageFormat", method);
        Assert.Contains("host-fallback", method);
        Assert.Contains("blocked", method);
    }

    [Fact]
    public void SandboxPage_NormalizesDefinitiveUnavailableMxcOff()
    {
        var source = ReadSandboxPageSource();
        var refresh = ExtractMethod(source, "RefreshAvailabilityAsync");
        var loadState = ExtractMethod(source, "LoadState");
        var definitiveUnavailable = ExtractMethod(source, "IsSandboxDefinitivelyUnavailable");
        var normalize = ExtractMethod(source, "NormalizeSandboxToggleForAvailability");

        AssertInOrder(
            refresh,
            "NormalizeSandboxToggleForAvailability();",
            "UpdateSandboxStatusCard();",
            "UpdateControlsEnabledState();");
        AssertInOrder(
            loadState,
            "NormalizeSandboxToggleForAvailability();",
            "UpdatePresetHighlight();",
            "UpdateSandboxStatusCard();",
            "UpdateControlsEnabledState();");
        Assert.Contains("CanRunSystemRunSandbox: false", definitiveUnavailable);
        Assert.Contains("ProbeErrored: false", definitiveUnavailable);
        Assert.Contains("ProbeSuppressedBySkuGate: false", definitiveUnavailable);
        AssertInOrder(
            normalize,
            "settings.SystemRunSandboxEnabled",
            "settings.SystemRunBlockHostFallbackWhenMxcUnavailable",
            "settings.SystemRunSandboxEnabled = false");
        Assert.Contains("settings.SystemRunSandboxEnabled = false", normalize);
        Assert.Contains("SandboxEnabledToggle.IsOn = false", normalize);
        Assert.Contains("Save();", normalize);
    }

    [Fact]
    public void SandboxPage_SkuSuppressionIsNotClassifiedAsMissingComponents()
    {
        var source = ReadSandboxPageSource();
        var actionBar = ExtractMethod(source, "UpdateUnavailableActionBar");

        AssertInOrder(
            actionBar,
            "var isSetupIssue",
            "!availability.ProbeSuppressedBySkuGate",
            "!availability.IsWxcExecResolvable");
    }

    [Fact]
    public void SandboxPage_RejectsTurningOnWhenMxcIsDefinitivelyUnavailable()
    {
        var source = ReadSandboxPageSource();
        var toggle = ExtractMethod(source, "OnSandboxEnabledToggledAsync");
        var reject = ExtractMethod(source, "RejectSandboxEnableWhenUnavailableAsync");

        AssertInOrder(
            toggle,
            "newValue",
            "!oldValue",
            "IsSandboxDefinitivelyUnavailable()",
            "!s.SystemRunBlockHostFallbackWhenMxcUnavailable",
            "await RejectSandboxEnableWhenUnavailableAsync();",
            "return;");
        Assert.Contains("SandboxEnabledToggle.IsOn = false", reject);
        Assert.Contains("Node Sandbox unavailable", reject);
        Assert.Contains("MXC BaseContainer without host DACL augmentation", reject);
    }

    private static string ReadCoordinatorSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "TrayIconCoordinator.cs"));
    }

    private static string ReadWslKeepAliveServiceSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "WslGatewayKeepAliveService.cs"));
    }

    private static string ReadAppSources()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var appDir = Path.Combine(root, "src", "OpenClaw.Tray.WinUI");
        return string.Join(
            "\n",
            Directory
                .EnumerateFiles(appDir, "App*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText));
    }

    private static string ReadSandboxPageSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "SandboxPage.xaml.cs"));
    }

    private static Dictionary<string, string> ReadReswValues(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

    private static string ReadWindowManagerSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "WindowManager.cs"));
    }

    private static string ReadActivationRouterServiceSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "ActivationRouter.cs"));
    }

    private static string ReadToastActivationRouterSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "ToastActivationRouter.cs"));
    }

    private static string ReadAppActivationRouterSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "App.ActivationRouter.cs"));
    }

    private static string ReadAppShutdownCoordinatorServiceSource()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "AppShutdownCoordinator.cs"));
    }

    private static string ExtractMethod(string source, string methodName, string? parameterHint = null)
    {
        var pattern = $@"(?m)^\s*(?:(?:private|protected|public|internal)\s+)?(?:static\s+)?(?:async\s+)?(?:Task(?:<[^>]+>)?|System\.Threading\.Tasks\.Task|void|bool|int|string\??|object\??|IntPtr|TrayMenuSnapshot|RollbackResult|OpenClaw\.Connection\.GatewayCredential\?|AppShutdownPlan)\s+(?:[A-Za-z0-9_]+\.)?{Regex.Escape(methodName)}\s*\(";
        var matches = Regex.Matches(source, pattern);
        Assert.True(matches.Count > 0, $"Could not find method {methodName}.");

        // Prefer a block-bodied candidate matching parameterHint (when given): thin
        // expression-bodied forwarders sharing the same short name (e.g. explicit interface
        // effect-port implementations) must not shadow the real implementation. Fall back to
        // the first match when no block-bodied candidate qualifies, preserving prior behavior
        // for single-match expression-bodied methods.
        var methodStart = -1;
        foreach (Match candidate in matches)
        {
            var paramStart = source.IndexOf('(', candidate.Index);
            var paramEnd = source.IndexOf(')', paramStart);

            var afterParams = paramEnd + 1;
            while (afterParams < source.Length && char.IsWhiteSpace(source[afterParams]))
                afterParams++;
            if (afterParams >= source.Length || source[afterParams] != '{')
                continue;

            if (parameterHint != null)
            {
                var parameterList = source.Substring(paramStart, paramEnd - paramStart);
                if (!parameterList.Contains(parameterHint, StringComparison.Ordinal))
                    continue;
            }

            methodStart = candidate.Index;
            break;
        }

        if (methodStart < 0)
            methodStart = matches[0].Index;

        var brace = source.IndexOf('{', methodStart);
        Assert.True(brace >= 0, $"Could not find body for method {methodName}.");

        var depth = 0;
        for (var index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(methodStart, index - methodStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not extract method {methodName}.");
    }

    private static void AssertAsyncResourceClearedInFinally(
        string source,
        string blockStart,
        string blockEnd,
        string disposeAwait,
        string referenceCheck,
        string fieldClear)
    {
        var start = source.IndexOf(blockStart, StringComparison.Ordinal);
        var end = source.IndexOf(blockEnd, start + blockStart.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not isolate shutdown block starting with: {blockStart}");
        var block = source.Substring(start, end - start);
        AssertInOrder(block, "try", disposeAwait, "finally", referenceCheck, fieldClear);
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        var current = -1;
        foreach (var marker in markers)
        {
            var next = source.IndexOf(marker, current + 1, StringComparison.Ordinal);
            Assert.True(next >= 0, $"Could not find marker after index {current}: {marker}");
            current = next;
        }
    }

}
