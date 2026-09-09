using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using System.Text;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// Keeps the app-owned WSL gateway from routing to a listener while the native
/// llama-server endpoint is absent, changing, or not owned by this companion.
/// </summary>
internal sealed class LocalAiGatewayProviderCoordinator : ILocalAiEndpointLifecycle
{
    private const string FixedPath = "/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:/usr/bin:/bin";
    private const int MaximumConfigBytes = 1024 * 1024;

    private readonly IWslCommandRunner _commands;
    private readonly ILocalAiGatewayDistroResolver _distroResolver;
    private readonly IOpenClawLogger _logger;

    public LocalAiGatewayProviderCoordinator(
        IWslCommandRunner commands,
        ILocalAiGatewayDistroResolver distroResolver,
        IOpenClawLogger logger)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _distroResolver = distroResolver ?? throw new ArgumentNullException(nameof(distroResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        GatewayCapture current = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Success)
            return Failed(current.Detail ?? "The managed Local AI gateway route could not be inspected.");

        string managedPrimary;
        try
        {
            managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
            LocalAiGatewayProviderDefinition.ValidateFallbackModel(
                install.Manifest.GatewayFallbackModel);
            if (current.ProviderExists)
            {
                _ = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
                if (!LocalAiGatewayProviderDefinition.MatchesProviderJson(
                        current.ProviderJson!,
                        install))
                {
                    return Failed("The llamacpp provider was changed outside the companion; preserving it and refusing to cycle the managed endpoint.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }

        bool primaryIsManaged = current.PrimaryExists &&
            string.Equals(current.PrimaryModel, managedPrimary, StringComparison.Ordinal);
        if (current.PrimaryExists &&
            !primaryIsManaged &&
            current.PrimaryModel!.StartsWith("llamacpp/", StringComparison.OrdinalIgnoreCase))
        {
            return Failed("The llamacpp primary model was changed outside the companion; preserving it and refusing to cycle the managed endpoint.");
        }

        // Unsetting the primary model does not make the gateway idle; it makes the
        // gateway resolve its built-in default (an OpenAI model), so a request that
        // lands mid-cycle fails with an unrelated provider-auth error instead of a
        // Local AI one. Retain the managed primary unless there is a real prior
        // model to restore, or Local AI is going away for good.
        string? expectedPrimary = current.PrimaryModel;
        bool retainManagedPrimary = reason == LocalAiQuiesceReason.EndpointCycle &&
            install.Manifest.GatewayFallbackModel is null;
        if (primaryIsManaged && !retainManagedPrimary)
        {
            expectedPrimary = install.Manifest.GatewayFallbackModel;
            LocalAiEndpointLifecycleResult primaryResult = expectedPrimary is null
                ? await UnsetAsync(LocalAiGatewayProviderDefinition.PrimaryModelPath, cancellationToken)
                    .ConfigureAwait(false)
                : await SetPrimaryAsync(expectedPrimary, cancellationToken).ConfigureAwait(false);
            if (!primaryResult.Success)
                return primaryResult;
        }

        if (current.ProviderExists)
        {
            LocalAiEndpointLifecycleResult providerResult = await UnsetAsync(
                    LocalAiGatewayProviderDefinition.ProviderPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!providerResult.Success)
                return providerResult;
        }

        GatewayCapture verified = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success || verified.ProviderExists ||
            verified.PrimaryExists != (expectedPrimary is not null) ||
            (expectedPrimary is not null &&
                !string.Equals(verified.PrimaryModel, expectedPrimary, StringComparison.Ordinal)))
        {
            return Failed("The managed Local AI gateway route remained active after it was disabled.");
        }
        return LocalAiEndpointLifecycleResult.Ok();
    }

    public async Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        string batch;
        try
        {
            batch = LocalAiGatewayProviderDefinition.BuildProviderBatchJson(install);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }

        GatewayCapture current = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Success)
            return Failed(current.Detail ?? "The managed Local AI gateway route could not be inspected.");
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        if (current.ProviderExists)
        {
            return LocalAiGatewayProviderDefinition.MatchesProviderJson(current.ProviderJson!, install) &&
                   current.PrimaryExists &&
                   string.Equals(current.PrimaryModel, managedPrimary, StringComparison.Ordinal)
                ? LocalAiEndpointLifecycleResult.Ok()
                : Failed("The Local AI gateway route changed outside the companion; preserving it instead of publishing the managed endpoint.");
        }

        // An endpoint cycle leaves the managed primary in place (there is no prior
        // model to fall back to), so seeing it here is this companion's own state,
        // not an outside edit.
        string? fallbackModel = install.Manifest.GatewayFallbackModel;
        bool retainedManagedPrimary = fallbackModel is null &&
            current.PrimaryExists &&
            string.Equals(current.PrimaryModel, managedPrimary, StringComparison.Ordinal);
        if (!retainedManagedPrimary &&
            (current.PrimaryExists != (fallbackModel is not null) ||
                (fallbackModel is not null &&
                    !string.Equals(current.PrimaryModel, fallbackModel, StringComparison.Ordinal))))
        {
            return Failed("The gateway primary model changed while Local AI was stopped; preserving it instead of overwriting it.");
        }

        RoutedCommandResult applied = await ApplyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        if (!applied.Routed)
            return Failed(applied.Detail!);
        if (!applied.Result!.Success)
        {
            LocalAiEndpointLifecycleResult cleanup = await QuiesceAsync(install, LocalAiQuiesceReason.Teardown, cancellationToken)
                .ConfigureAwait(false);
            return PublicationFailed(
                "The verified Local AI route could not be published to the app-owned gateway.",
                cleanup);
        }

        GatewayCapture verified = await CaptureGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success || !verified.ProviderExists ||
            !LocalAiGatewayProviderDefinition.MatchesProviderJson(verified.ProviderJson!, install) ||
            !verified.PrimaryExists ||
            !string.Equals(verified.PrimaryModel, managedPrimary, StringComparison.Ordinal))
        {
            LocalAiEndpointLifecycleResult cleanup = await QuiesceAsync(install, LocalAiQuiesceReason.Teardown, cancellationToken)
                .ConfigureAwait(false);
            return PublicationFailed(
                "The app-owned gateway did not retain the verified Local AI route.",
                cleanup);
        }
        return LocalAiEndpointLifecycleResult.Ok();
    }

    private LocalAiEndpointLifecycleResult PublicationFailed(
        string detail,
        LocalAiEndpointLifecycleResult cleanup) => cleanup.Success
            ? Failed($"{detail} The just-written route was removed.")
            : Failed($"{detail} Cleanup also failed: {cleanup.Detail}");

    private async Task<GatewayCapture> CaptureGatewayAsync(CancellationToken cancellationToken)
    {
        SettingCapture provider = await CaptureSettingAsync(
            LocalAiGatewayProviderDefinition.ProviderPath,
            cancellationToken).ConfigureAwait(false);
        if (!provider.Success)
            return new(false, false, null, false, null, provider.Detail);

        SettingCapture primary = await CaptureSettingAsync(
            LocalAiGatewayProviderDefinition.PrimaryModelPath,
            cancellationToken).ConfigureAwait(false);
        if (!primary.Success)
            return new(false, false, null, false, null, primary.Detail);

        string? providerJson = null;
        if (provider.Exists)
        {
            try
            {
                using JsonDocument document = ParseBounded(provider.Json!);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new(false, false, null, false, null, "The managed llamacpp provider has an invalid shape.");
                providerJson = document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                return new(false, false, null, false, null, "The managed llamacpp provider is not valid JSON.");
            }
        }

        string? primaryModel = null;
        if (primary.Exists)
        {
            try
            {
                using JsonDocument document = ParseBounded(primary.Json!);
                if (document.RootElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(primaryModel = document.RootElement.GetString()) ||
                    primaryModel.Length > 512 ||
                    primaryModel.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                {
                    return new(false, false, null, false, null, "The gateway primary model has an invalid shape.");
                }
            }
            catch (JsonException)
            {
                return new(false, false, null, false, null, "The gateway primary model is not valid JSON.");
            }
        }

        return new(true, provider.Exists, providerJson, primary.Exists, primaryModel, null);
    }

    private async Task<SettingCapture> CaptureSettingAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RoutedCommandResult routed = await RunOpenClawAsync(
            ["config", "get", path, "--json"], cancellationToken).ConfigureAwait(false);
        if (!routed.Routed)
            return new(false, false, null, routed.Detail);

        WslCommandResult direct = routed.Result!;
        if (direct.Success)
            return new(true, true, direct.StandardOutput, null);
        string missing = $"Config path not found: {path}";
        return direct.StandardError.Contains(missing, StringComparison.Ordinal)
            ? new(true, false, null, null)
            : new(false, false, null, $"The app-owned gateway setting '{path}' could not be read.");
    }

    private async Task<LocalAiEndpointLifecycleResult> SetPrimaryAsync(
        string model,
        CancellationToken cancellationToken)
    {
        LocalAiGatewayProviderDefinition.ValidateFallbackModel(model);
        string batch = JsonSerializer.Serialize(new[]
        {
            new { path = LocalAiGatewayProviderDefinition.PrimaryModelPath, value = model },
        });
        RoutedCommandResult routed = await ApplyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        if (!routed.Routed)
            return Failed(routed.Detail!);
        return routed.Result!.Success
            ? LocalAiEndpointLifecycleResult.Ok()
            : Failed("The prior gateway primary model could not be restored before the Local AI endpoint changed.");
    }

    private async Task<LocalAiEndpointLifecycleResult> UnsetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        RoutedCommandResult routed = await RunOpenClawAsync(
            ["config", "unset", path], cancellationToken).ConfigureAwait(false);
        if (!routed.Routed)
            return Failed(routed.Detail!);
        return routed.Result!.Success
            ? LocalAiEndpointLifecycleResult.Ok()
            : Failed($"The managed gateway setting '{path}' could not be disabled before the Local AI endpoint changed.");
    }

    private Task<RoutedCommandResult> ApplyBatchAsync(
        string batch,
        CancellationToken cancellationToken)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(batch));
        string script =
            $"set -e\nprintf '%s' '{encoded}' | base64 -d | openclaw config set --batch-file /dev/stdin --dry-run\n" +
            $"printf '%s' '{encoded}' | base64 -d | openclaw config set --batch-file /dev/stdin";
        return RunInManagedDistroAsync(
            ["/usr/bin/env", $"PATH={FixedPath}", "/bin/sh", "-c", script],
            cancellationToken);
    }

    private static JsonDocument ParseBounded(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumConfigBytes)
            throw new JsonException("The gateway configuration is too large.");
        return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private Task<RoutedCommandResult> RunOpenClawAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = new List<string>(arguments.Count + 3)
        {
            "/usr/bin/env",
            $"PATH={FixedPath}",
            "openclaw",
        };
        command.AddRange(arguments);
        return RunInManagedDistroAsync(command, cancellationToken);
    }

    private async Task<RoutedCommandResult> RunInManagedDistroAsync(
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        LocalAiGatewayDistroResolution resolution = _distroResolver.Resolve();
        if (!resolution.Success)
            return new(null, resolution.Detail);

        WslCommandResult result = await _commands.RunInDistroAsync(
                resolution.DistroName!,
                command,
                cancellationToken)
            .ConfigureAwait(false);
        return new(result, null);
    }

    private LocalAiEndpointLifecycleResult Failed(string detail)
    {
        _logger.Warn(detail);
        return LocalAiEndpointLifecycleResult.Failed(detail);
    }

    private sealed record SettingCapture(bool Success, bool Exists, string? Json, string? Detail);
    private sealed record RoutedCommandResult(WslCommandResult? Result, string? Detail)
    {
        public bool Routed => Result is not null;
    }
    private sealed record GatewayCapture(
        bool Success,
        bool ProviderExists,
        string? ProviderJson,
        bool PrimaryExists,
        string? PrimaryModel,
        string? Detail);
}
