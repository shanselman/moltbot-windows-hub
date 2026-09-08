using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class PairOperatorStep : SetupStep
{
    public override string Id => "pair-operator";
    public override string DisplayName => "Pair operator connection";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var gatewayUrl = ctx.GatewayUrl!;
        var token = SetupPairingCredentialPolicy.ResolveInitialPairingToken(ctx);

        if (string.IsNullOrEmpty(token))
            return StepResult.Terminal("No credential available for operator pairing");

        // Register gateway in registry (only once — reuse across retries)
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();

        string identityPath;
        if (!string.IsNullOrEmpty(ctx.GatewayRecordId))
        {
            var existing = registry.GetById(ctx.GatewayRecordId);
            if (existing == null)
                return StepResult.Fail($"Gateway record {ctx.GatewayRecordId} not found");
            identityPath = registry.GetIdentityDirectory(existing.Id);
            ctx.Logger.Info($"Reusing existing gateway record: id={existing.Id}");
        }
        else
        {
            var record = new GatewayRecord
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Url = gatewayUrl,
                FriendlyName = ctx.Config.Tailscale.Enabled
                    ? $"Tailscale ({ctx.DistroName})"
                    : $"Local ({ctx.DistroName})",
                SharedGatewayToken = ctx.SharedGatewayToken,
                BootstrapToken = ctx.BootstrapToken,
                IsLocal = true,
                SetupManagedDistroName = ctx.DistroName,
                LastConnected = DateTime.UtcNow
            };

            record = registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            registry.Save();
            ctx.GatewayRecordId = record.Id;
            identityPath = registry.GetIdentityDirectory(record.Id);
            ctx.Logger.Info($"Gateway record created: id={record.Id}");
        }

        // Initialize device identity
        Directory.CreateDirectory(identityPath);
        var identity = new DeviceIdentity(identityPath);
        try
        {
            identity.Initialize();
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "operator pairing", ex);
        }
        ctx.Logger.Info($"Device identity initialized: {identity.DeviceId[..16]}...");
        ctx.OperatorDeviceId = identity.DeviceId;

        var reachability = await WindowsGatewayReachability.VerifyAsync(ctx, "operator", ct);
        if (!reachability.IsSuccess)
            return reachability;
        var provenanceCheck = await EnsurePairingEndpointTrustedAsync(ctx, ct);
        if (provenanceCheck is not null)
            return provenanceCheck;

        // Connect operator WebSocket — handle pairing-required flow
        var wsLogger = new SetupOpenClawLogger(ctx.Logger);
        OpenClawGatewayClient? client = null;

        try
        {
            // Phase 1: Initial connect (may get PAIRING_REQUIRED)
            client = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
            ApplyReconnectAuthorization(client, ctx);
            client.UseV2Signature = true; // Local gateway uses v2 signature format
            var phase1Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(15), ct);

            if (phase1Result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Operator connected directly (no pairing needed)");
                return StepResult.Ok("Operator connected and paired");
            }

            if (phase1Result == ConnectionOutcome.PairingRequired)
            {
                if (!ctx.Config.AutoApprovePairing)
                    return StepResult.Fail("Pairing required but auto-approve is disabled");

                ctx.Logger.Info("Pairing required — auto-approving via CLI");
                var requestId = client.PairingRequiredRequestId;
                await client.DisconnectAsync();
                client.Dispose();
                client = null;

                // Auto-approve the pending pairing request
                var approveResult = await AutoApprovePairing(ctx, requestId, ct);
                if (!approveResult.IsSuccess)
                    return approveResult;

                // Wait for gateway to process the approval
                await Task.Delay(2000, ct);

                // Phase 2: Reconnect — the device should now be approved
                provenanceCheck = await EnsurePairingEndpointTrustedAsync(ctx, ct);
                if (provenanceCheck is not null)
                    return provenanceCheck;
                client = new OpenClawGatewayClient(gatewayUrl, token, logger: wsLogger, identityPath: identityPath);
                ApplyReconnectAuthorization(client, ctx);
                client.UseV2Signature = true;
                var phase2Result = await WaitForConnectionOrPairing(client, ctx, TimeSpan.FromSeconds(20), ct);

                if (phase2Result == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Operator paired successfully after approval");
                    // Disconnect before finalization
                    await client.DisconnectAsync();
                    client.Dispose();
                    client = null;

                    // Phase 3: Skip operator finalization here — it must happen AFTER node pairing.
                    // The node pairing changes the device's "current metadata" to node/node-host,
                    // so operator finalization (as cli/cli) must come last to match what the tray sends.
                    ctx.Logger.Info("Operator paired — finalization deferred to after node pairing");
                    return StepResult.Ok("Operator paired (finalization deferred)");
                }

                return ConnectionFailureResult(ctx, "Reconnection after approval failed", phase2Result);
            }

            return ConnectionFailureResult(ctx, "Operator connection failed", phase1Result);
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "operator pairing", ex);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"Operator pairing failed: {ex.Message}", ex);
        }
        finally
        {
            if (client != null)
            {
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
    }

    internal static async Task<StepResult?> EnsurePairingEndpointTrustedAsync(
        SetupContext ctx,
        CancellationToken cancellationToken,
        int noListenerRetryCount = 0,
        TimeSpan? noListenerRetryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(noListenerRetryCount);
        var retryDelay = noListenerRetryDelay ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        var record = new GatewayRecord
        {
            Id = ctx.GatewayRecordId ?? "setup-managed-gateway",
            Url = ctx.GatewayUrl ?? ctx.Config.EffectiveGatewayUrl,
            IsLocal = true,
            SetupManagedDistroName = ctx.DistroName,
        };
        var probe = ctx.EndpointProvenanceProbe ??
            new ManagedLocalGatewayPortProvenanceService(
                new SetupOpenClawLogger(ctx.Logger)).InspectAsync;
        var provenance =
            await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                cancellationToken => probe(record, cancellationToken),
                noListenerRetryCount,
                retryDelay,
                cancellationToken).ConfigureAwait(false);

        return provenance.Kind switch
        {
            GatewayEndpointProvenanceKind.ExpectedManagedGateway or
            GatewayEndpointProvenanceKind.NotApplicable => null,
            GatewayEndpointProvenanceKind.NoListener =>
                StepResult.Fail("The managed WSL gateway is not listening; no pairing credential was sent."),
            _ => StepResult.Terminal(
                provenance.Detail ??
                "The managed gateway address is owned by an unverified process; no pairing credential was sent."),
        };
    }

    internal static void ApplyReconnectAuthorization(
        WebSocketClientBase client,
        SetupContext ctx,
        int provenanceRetryCount = 0,
        TimeSpan? provenanceRetryDelay = null)
    {
        async Task<ReconnectAuthorizationResult> AuthorizeCredentialHandoffAsync(
            CancellationToken cancellationToken)
        {
            var failure = await EnsurePairingEndpointTrustedAsync(
                ctx,
                cancellationToken,
                provenanceRetryCount,
                provenanceRetryDelay).ConfigureAwait(false);
            return failure is null
                ? ReconnectAuthorizationResult.AllowedResult
                : new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    failure.Message);
        }

        client.ReconnectAuthorizationAsync = AuthorizeCredentialHandoffAsync;
        switch (client)
        {
            case OpenClawGatewayClient gatewayClient:
                gatewayClient.HandshakeAuthorizationAsync =
                    AuthorizeCredentialHandoffAsync;
                break;
            case WindowsNodeClient nodeClient:
                nodeClient.HandshakeAuthorizationAsync =
                    AuthorizeCredentialHandoffAsync;
                break;
        }
    }

    /// <summary>
    /// After initial pairing, the gateway knows us via auth.token (shared gateway token).
    /// The tray will connect using auth.deviceToken (the token we just received).
    /// This "finalizes" the transition so the gateway doesn't flag it as metadata-upgrade.
    /// </summary>
    private static async Task<StepResult> FinalizeWithDeviceToken(
        SetupContext ctx, string gatewayUrl, string identityPath, IOpenClawLogger wsLogger, CancellationToken ct)
    {
        ctx.Logger.Info("Finalizing: reconnect with device token (like tray will)");

        // Read the device token we just stored
        var identity = new DeviceIdentity(identityPath);
        try
        {
            identity.Initialize();
        }
        catch (DeviceIdentityLoadException ex)
        {
            return SetupIdentityFailure.Terminal(ctx, "operator finalization", ex);
        }
        var deviceToken = identity.DeviceToken;

        if (string.IsNullOrEmpty(deviceToken))
        {
            ctx.Logger.Warn("No device token stored after pairing — skipping finalization");
            return StepResult.Ok("Operator paired (no finalization needed)");
        }

        // Wait for the gateway's internal session grace period to expire.
        // Without this delay, the gateway accepts the deviceToken connect within grace
        // but would later reject the tray's identical connect as "metadata-upgrade".
        ctx.Logger.Info("Waiting for gateway grace period to expire before finalization...");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        // Connect exactly as the tray would: pass deviceToken as the credential
        var finalClient = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
        ApplyReconnectAuthorization(finalClient, ctx);
        finalClient.UseV2Signature = true;

        try
        {
            var result = await WaitForConnectionOrPairing(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

            if (result == ConnectionOutcome.Connected)
            {
                ctx.Logger.Info("Finalization connected — tray will connect seamlessly");
                return StepResult.Ok("Operator paired and finalized for tray");
            }

            if (result == ConnectionOutcome.PairingRequired)
            {
                ctx.Logger.Info("Metadata-upgrade detected during finalization — auto-approving");
                var requestId = finalClient.PairingRequiredRequestId;
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
                finalClient = null;

                // Approve the metadata-upgrade
                var approveResult = await AutoApprovePairing(ctx, requestId, ct);
                if (!approveResult.IsSuccess)
                    return StepResult.Fail($"Finalization approval failed: {approveResult.Message}");

                await Task.Delay(2000, ct);

                // One more connect to confirm
                finalClient = new OpenClawGatewayClient(gatewayUrl, deviceToken, logger: wsLogger, identityPath: identityPath);
                ApplyReconnectAuthorization(finalClient, ctx);
                finalClient.UseV2Signature = true;
                var finalResult = await WaitForConnectionOrPairing(finalClient, ctx, TimeSpan.FromSeconds(15), ct);

                if (finalResult == ConnectionOutcome.Connected)
                {
                    ctx.Logger.Info("Finalization approved — tray will connect seamlessly");
                    return StepResult.Ok("Operator paired and finalized for tray");
                }

                return ConnectionFailureResult(ctx, "Finalization failed after approval", finalResult);
            }

            return ConnectionFailureResult(ctx, "Finalization connect failed", result);
        }
        finally
        {
            if (finalClient != null)
            {
                await finalClient.DisconnectAsync();
                finalClient.Dispose();
            }
        }
    }

    internal static async Task<StepResult> AutoApprovePairing(SetupContext ctx, CancellationToken ct)
        => await AutoApprovePairing(ctx, requestId: null, ct);

    internal static async Task<StepResult> AutoApprovePairing(SetupContext ctx, string? requestId, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        var token = ctx.SharedGatewayToken ?? ctx.BootstrapToken ?? throw new InvalidOperationException("No gateway token available for auto-approve");

        var env = new Dictionary<string, string> { ["OPENCLAW_GATEWAY_TOKEN"] = token };

        if (string.IsNullOrWhiteSpace(requestId))
        {
            var preview = await ctx.Commands.RunInWslAsync(
                distro,
                $"""{ctx.WslPathPrefix} && openclaw devices approve --latest --json""",
                TimeSpan.FromSeconds(30), env, ct);

            ctx.Logger.Info($"Approve preview: exit={preview.ExitCode}");

            var parsed = ApprovalRequestHelper.TryReadSelectedRequestId(preview.Stdout.Trim());
            if (!parsed.Success)
            {
                ctx.Logger.Warn($"Could not select pairing request: {parsed.Error}");
                return StepResult.Fail("Could not find a safe pending pairing request to approve");
            }

            requestId = parsed.RequestId;
        }

        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
        {
            ctx.Logger.Warn("Refusing to approve pairing request with unsafe request ID");
            return StepResult.Fail("Pairing request ID contained unsafe characters");
        }

        ctx.Logger.Info($"Approving pairing request: {requestId}");
        var approvalEnv = ApprovalRequestHelper.AddRequestIdEnvironment(env, requestId!);

        var approve = await ctx.Commands.RunInWslAsync(
            distro,
            $"""{ctx.WslPathPrefix} && {ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Device)}""",
            TimeSpan.FromSeconds(30), approvalEnv, ct);

        ctx.Logger.Info($"Approve result: exit={approve.ExitCode}");

        if (approve.ExitCode != 0)
        {
            var approveOutput = approve.Stdout.Trim();
            if (ApprovalRequestHelper.IsPluginNotFoundError(approveOutput))
                return StepResult.Terminal(ApprovalRequestHelper.PluginNotFoundMessage);
            return StepResult.Fail($"Device approval failed (exit {approve.ExitCode}): {approveOutput}");
        }

        return StepResult.Ok($"Approved request {requestId}");
    }

    internal enum ConnectionOutcome { Connected, PairingRequired, CompatibilityFailure, Error, Timeout }

    internal static StepResult ConnectionFailureResult(
        SetupContext ctx,
        string prefix,
        ConnectionOutcome outcome)
    {
        if (outcome == ConnectionOutcome.CompatibilityFailure &&
            ctx.GatewayCompatibilityFailure is { } compatibilityFailure)
        {
            return StepResult.Terminal(compatibilityFailure.Message, compatibilityFailure);
        }

        return StepResult.Fail($"{prefix}: {outcome}");
    }

    internal static ConnectionOutcome? ClassifySetupConnectionStatus(
        ConnectionStatus status,
        bool isPairingRequired,
        int? lastRemoteCloseStatusCode,
        bool retryGatewayStartupDisconnects) =>
        status switch
        {
            ConnectionStatus.Connected => ConnectionOutcome.Connected,
            ConnectionStatus.Error => ConnectionOutcome.Error,
            ConnectionStatus.Disconnected when isPairingRequired =>
                ConnectionOutcome.PairingRequired,
            ConnectionStatus.Disconnected when
                retryGatewayStartupDisconnects &&
                GatewayWizardRestartRecoveryPolicy.IsRetryableGatewayStartupDisconnect(
                    lastRemoteCloseStatusCode) => null,
            ConnectionStatus.Disconnected => ConnectionOutcome.Error,
            _ => null,
        };

    internal static async Task<ConnectionOutcome> WaitForConnectionOrPairing(
        OpenClawGatewayClient client,
        SetupContext ctx,
        TimeSpan timeout,
        CancellationToken ct,
        bool retryGatewayStartupDisconnects = false)
    {
        var tcs = new TaskCompletionSource<ConnectionOutcome>();
        ctx.ObservedGatewaySelf = null;
        ctx.GatewayCompatibilityFailure = null;

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            ctx.Logger.Debug($"Operator connection status: {status}");
            if (status == ConnectionStatus.Connected)
            {
                var compatibilityFailure = GatewayInstallPolicy.ValidateHandshake(
                    ctx.Config,
                    ctx.ObservedGatewaySelf);
                if (compatibilityFailure is null)
                {
                    tcs.TrySetResult(ConnectionOutcome.Connected);
                }
                else
                {
                    ctx.GatewayCompatibilityFailure = compatibilityFailure;
                    tcs.TrySetResult(ConnectionOutcome.CompatibilityFailure);
                }
                return;
            }

            var outcome = ClassifySetupConnectionStatus(
                status,
                client.IsPairingRequired,
                client.LastRemoteCloseStatusCode,
                retryGatewayStartupDisconnects);
            if (outcome is not null)
            {
                tcs.TrySetResult(outcome.Value);
            }
            else if (status == ConnectionStatus.Disconnected)
            {
                ctx.Logger.Debug(
                    "Gateway is still starting after restart; waiting for the authenticated reconnect.");
            }
        }

        client.StatusChanged += OnStatusChanged;
        EventHandler<DeviceTokenReceivedEventArgs> onDeviceToken = (_, _) => ctx.Logger.Info("Device token received from gateway");
        client.DeviceTokenReceived += onDeviceToken;
        EventHandler<GatewaySelfInfo> onGatewaySelf = (_, gatewaySelf) => ctx.ObservedGatewaySelf = gatewaySelf;
        client.GatewaySelfUpdated += onGatewaySelf;

        try
        {
            await client.ConnectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ConnectionOutcome.Timeout;
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"Operator connection failed: {ex.Message}");
            return ConnectionOutcome.Error;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
            client.DeviceTokenReceived -= onDeviceToken;
            client.GatewaySelfUpdated -= onGatewaySelf;
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();

        // Find all local gateway records to remove (mirrors old uninstall step 6a)
        var localRecords = registry.GetAll()
            .Where(r => IsSetupManagedLocalRecord(r, ctx))
            .ToList();

        if (localRecords.Count > 0)
        {
            foreach (var record in localRecords)
            {
                // Remove identity directory
                var identityDir = registry.GetIdentityDirectory(record.Id);
                if (Directory.Exists(identityDir))
                {
                    Directory.Delete(identityDir, recursive: true);
                    ctx.Logger.Info($"[Uninstall] Deleted identity directory: {identityDir}");
                }
                registry.Remove(record.Id);
            }
            registry.Save();
            ctx.Logger.Info($"[Uninstall] Removed {localRecords.Count} local gateway record(s)");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] No local gateway records found");
        }

        // Null operator device token (mirrors old uninstall step 7)
        // Check if external gateways remain — if so, preserve root device tokens
        var hasExternalGateways = registry.GetAll().Any(r =>
            !r.IsLocal && !(r.SshTunnel is null && LocalGatewayUrlClassifier.IsLocalGatewayUrl(r.Url)));

        if (hasExternalGateways)
        {
            ctx.Logger.Info("[Uninstall] Preserving root device tokens — external gateway records remain");
        }
        else
        {
            var operatorCleared = DeviceIdentity.TryClearDeviceTokenForRole(ctx.DataDir, "operator");
            ctx.Logger.Info(operatorCleared
                ? "[Uninstall] Cleared operator device token"
                : "[Uninstall] Operator device token already absent");
        }

        // Best-effort revoke operator token via gateway HTTP endpoint (mirrors old step 4)
        await TryRevokeOperatorTokenAsync(ctx, ct);
    }

    internal static bool IsSetupManagedLocalRecord(GatewayRecord record, SetupContext ctx)
    {
        if (!record.IsLocal || record.SshTunnel != null)
            return false;

        if (string.Equals(record.SetupManagedDistroName, ctx.DistroName, StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(record.SetupManagedDistroName)
            && string.Equals(record.Url, ctx.GatewayUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.FriendlyName, $"Local ({ctx.DistroName})", StringComparison.Ordinal);
    }

    private static async Task TryRevokeOperatorTokenAsync(SetupContext ctx, CancellationToken ct)
    {
        try
        {
            // Read settings.json for legacy token if available
            var settingsPath = Path.Combine(ctx.DataDir, "settings.json");
            if (!File.Exists(settingsPath)) return;

            var settingsJson = await File.ReadAllTextAsync(settingsPath, ct);
            using var doc = JsonDocument.Parse(settingsJson);

            string? token = null;
            if (doc.RootElement.TryGetProperty("Token", out var tokenProp))
                token = tokenProp.GetString();

            if (string.IsNullOrWhiteSpace(token)) return;

            var gatewayUrl = ctx.GatewayUrl ?? "ws://127.0.0.1:18789";
            var httpBase = gatewayUrl
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('/');

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await http.PostAsync($"{httpBase}/api/v1/operator/disconnect", content: null, cts.Token);
            ctx.Logger.Info($"[Uninstall] Revoke operator token: HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            ctx.Logger.Info($"[Uninstall] Best-effort token revoke failed ({ex.GetType().Name}); gateway may be down");
        }
    }
}
