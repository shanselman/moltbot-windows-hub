using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiGatewayPriorState(
    bool ProviderExisted,
    string? ProviderJson,
    bool PrimaryModelExisted,
    string? PrimaryModelJson);

internal static class LocalAiGatewayConfigBuilder
{
    internal const string ProviderPath = LocalAiGatewayProviderDefinition.ProviderPath;
    internal const string PrimaryModelPath = LocalAiGatewayProviderDefinition.PrimaryModelPath;

    public static string BuildBatchJson(SetupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var install = context.LocalAiResolvedInstall
            ?? throw new InvalidOperationException("The Local AI install receipt is required.");
        _ = context.LocalAiEligibility?.Plan
            ?? throw new InvalidOperationException("The qualified Local AI plan is required.");
        using JsonDocument provider = JsonDocument.Parse(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install));
        object[] operations =
        [
            new { path = ProviderPath, value = (object)provider.RootElement.Clone() },
            new { path = PrimaryModelPath, value = (object)LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) },
        ];
        return JsonSerializer.Serialize(operations);
    }

    public static string BuildRestoreBatchJson(LocalAiGatewayPriorState prior)
    {
        ArgumentNullException.ThrowIfNull(prior);
        var operations = new List<object>(1);
        // Setup accepts a pre-existing provider only when it already matches the
        // managed definition. Do not replay its CLI-redacted API key on rollback.
        // The exact current provider is retained below when it existed beforehand.
        if (prior.PrimaryModelExisted)
        {
            using JsonDocument primary = JsonDocument.Parse(prior.PrimaryModelJson!);
            operations.Add(new { path = PrimaryModelPath, value = (object)primary.RootElement.Clone() });
        }
        return JsonSerializer.Serialize(operations);
    }

    public static string ExpectedPrimaryModel(SetupContext context) =>
        LocalAiGatewayProviderDefinition.BuildPrimaryModel(
            context.LocalAiResolvedInstall
                ?? throw new InvalidOperationException("The Local AI install receipt is required."));

    public static string BuildRecoveryRestoreBatchJson(
        LocalAiGatewayPriorState prior,
        LocalAiResolvedInstall originalInstall)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(originalInstall);
        using JsonDocument provider = JsonDocument.Parse(
            LocalAiGatewayProviderDefinition.BuildProviderJson(originalInstall));
        using JsonDocument primary = JsonDocument.Parse(prior.PrimaryModelJson!);
        object[] operations =
        [
            new { path = ProviderPath, value = (object)provider.RootElement.Clone() },
            new { path = PrimaryModelPath, value = (object)primary.RootElement.Clone() },
        ];
        return JsonSerializer.Serialize(operations);
    }
}

public sealed class ConfigureLocalAiGatewayStep : SetupStep
{
    private const string ProviderMarker = "OPENCLAW_LOCAL_AI_PROVIDER_B64=";
    private const string PrimaryMarker = "OPENCLAW_LOCAL_AI_PRIMARY_B64=";
    private const string MissingValue = "MISSING";
    private const string BatchVariable = "OPENCLAW_LOCAL_AI_BATCH_B64";
    private const int MaximumSnapshotBytes = 1024 * 1024;

    public override string Id => "configure-local-ai-gateway";
    public override string DisplayName => "Connect gateway to Local AI";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiResolvedInstall is null || ctx.LocalAiEligibility?.Plan is null)
            return StepResult.Terminal("Local AI gateway configuration requires a qualified install receipt.");

        CommandResult snapshotResult = await CaptureStateAsync(ctx, ct);
        if (snapshotResult.ExitCode != 0 || snapshotResult.TimedOut)
            return StepResult.Fail("Could not safely snapshot the existing Local AI gateway configuration.");

        LocalAiGatewayPriorState prior;
        try
        {
            prior = ParseSnapshot(snapshotResult.Stdout);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            return StepResult.Fail("The existing Local AI gateway configuration could not be validated.", ex);
        }

        LocalAiResolvedInstall install = ctx.LocalAiResolvedInstall;
        string expectedPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        string? fallbackModel;
        if (prior.ProviderExisted)
        {
            bool matchesCurrentInstall = install.Endpoint is not null &&
                LocalAiGatewayProviderDefinition.MatchesProviderJson(prior.ProviderJson!, install);
            bool matchesRecoveryInstall = false;
            if (!matchesCurrentInstall &&
                ctx.LocalAiRecoveryOriginalInstall is { Endpoint: not null } originalInstall)
            {
                string originalPrimary = JsonSerializer.Serialize(
                    LocalAiGatewayProviderDefinition.BuildPrimaryModel(originalInstall));
                matchesRecoveryInstall =
                    LocalAiGatewayProviderDefinition.MatchesProviderJson(
                        prior.ProviderJson!,
                        originalInstall) &&
                    JsonEquals(originalPrimary, expectedPrimary);
            }

            if ((!matchesCurrentInstall && !matchesRecoveryInstall) ||
                !prior.PrimaryModelExisted ||
                !JsonEquals(prior.PrimaryModelJson!, expectedPrimary))
            {
                return StepResult.Fail(
                    "The existing llamacpp gateway route is not the exact companion-managed configuration; preserving it.");
            }
            if (matchesRecoveryInstall)
                ctx.LocalAiRecoveryProviderTransition = true;
            fallbackModel = install.Manifest.GatewayFallbackModel;
        }
        else if (prior.PrimaryModelExisted)
        {
            if (!LocalAiGatewayProviderDefinition.TryReadPrimaryModelJson(
                    prior.PrimaryModelJson!,
                    out fallbackModel))
            {
                return StepResult.Fail(
                    "The existing gateway primary model cannot be safely restored after Local AI stops; preserving it.");
            }
        }
        else
        {
            fallbackModel = null;
        }
        ctx.LocalAiGatewayPriorState ??= prior;

        if (!string.Equals(
                install.Manifest.GatewayFallbackModel,
                fallbackModel,
                StringComparison.Ordinal))
        {
            try
            {
                var store = new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir));
                LocalAiInstallManifest updatedManifest = install.Manifest with
                {
                    GatewayFallbackModel = fallbackModel,
                };
                await store.SaveAsync(updatedManifest, ct).ConfigureAwait(false);
                ctx.LocalAiResolvedInstall = store.ResolveAndValidate(updatedManifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return StepResult.Fail(
                    "The prior gateway model could not be recorded before Local AI was enabled.", ex);
            }
        }

        string batchJson = LocalAiGatewayConfigBuilder.BuildBatchJson(ctx);
        CommandResult result = await ApplyBatchAsync(ctx, batchJson, "LOCAL_AI_GATEWAY_CONFIGURED", ct);
        if (result.ExitCode != 0 || result.TimedOut ||
            !result.Stdout.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal))
        {
            return StepResult.Fail(result.TimedOut
                ? "Local AI gateway configuration timed out."
                : $"Local AI gateway configuration failed (exit {result.ExitCode}).");
        }

        return StepResult.Ok("Gateway configured to use the managed llama-server provider");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.IsUninstalling)
        {
            await RemoveManagedStateForUninstallAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        if (ctx.LocalAiGatewayPriorState is not { } prior)
            return;

        CommandResult currentResult = await CaptureStateAsync(ctx, ct);
        if (currentResult.ExitCode != 0 || currentResult.TimedOut)
        {
            ctx.Logger.Warn("Could not inspect the Local AI gateway configuration during rollback; preserving it.");
            return;
        }

        LocalAiGatewayPriorState current;
        try
        {
            current = ParseSnapshot(currentResult.Stdout);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            ctx.Logger.Warn($"Could not validate Local AI gateway rollback state; preserving it ({ex.GetType().Name}).");
            return;
        }

        LocalAiResolvedInstall? recoveryOriginal = ctx.LocalAiRecoveryProviderTransition
            ? ctx.LocalAiRecoveryOriginalInstall
            : null;
        if (recoveryOriginal is not null &&
            current.ProviderExisted &&
            prior.ProviderExisted &&
            LocalAiGatewayProviderDefinition.MatchesProviderJson(
                current.ProviderJson!,
                recoveryOriginal) &&
            current.PrimaryModelExisted == prior.PrimaryModelExisted &&
            (!current.PrimaryModelExisted ||
                JsonEquals(current.PrimaryModelJson!, prior.PrimaryModelJson!)))
        {
            return;
        }

        string expectedPrimary = JsonSerializer.Serialize(LocalAiGatewayConfigBuilder.ExpectedPrimaryModel(ctx));
        if (!current.ProviderExisted || !current.PrimaryModelExisted ||
            !LocalAiGatewayProviderDefinition.MatchesProviderJson(
                current.ProviderJson!,
                ctx.LocalAiResolvedInstall!) ||
            !JsonEquals(current.PrimaryModelJson!, expectedPrimary))
        {
            ctx.Logger.Warn("Local AI gateway settings changed after setup; preserving the newer values.");
            return;
        }

        string restoreBatch;
        if (recoveryOriginal is not null)
        {
            restoreBatch = LocalAiGatewayConfigBuilder.BuildRecoveryRestoreBatchJson(
                prior,
                recoveryOriginal);
        }
        else
        {
            restoreBatch = LocalAiGatewayConfigBuilder.BuildRestoreBatchJson(prior);
        }
        if (restoreBatch != "[]")
        {
            CommandResult restore = await ApplyBatchAsync(ctx, restoreBatch, "LOCAL_AI_GATEWAY_RESTORED", ct);
            if (restore.ExitCode != 0 || restore.TimedOut)
            {
                ctx.Logger.Warn("Restoring the previous Local AI gateway settings failed.");
                return;
            }
        }

        var unset = new List<string>(2);
        if (!prior.PrimaryModelExisted)
            unset.Add($"openclaw config unset {LocalAiGatewayConfigBuilder.PrimaryModelPath}");
        if (!prior.ProviderExisted)
            unset.Add($"openclaw config unset {LocalAiGatewayConfigBuilder.ProviderPath}");
        if (unset.Count > 0)
        {
            string script = $"set -e\n{ctx.WslPathPrefix}\n{string.Join("\n", unset)}\necho LOCAL_AI_GATEWAY_UNSET";
            CommandResult result = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, script, TimeSpan.FromMinutes(2), ct: ct,
                user: ctx.Config.Wsl.User, inputViaStdin: true);
            if (result.ExitCode != 0 || result.TimedOut)
                ctx.Logger.Warn("Removing setup-created Local AI gateway settings failed.");
        }
    }

    private static async Task RemoveManagedStateForUninstallAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        LocalAiResolvedInstall? install = ctx.LocalAiResolvedInstall;
        if (install is null)
        {
            install = await new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir))
                .LoadAsync(ct)
                .ConfigureAwait(false);
        }
        if (install is null)
            return;

        CommandResult currentResult = await CaptureStateAsync(ctx, ct).ConfigureAwait(false);
        if (currentResult.ExitCode != 0 || currentResult.TimedOut)
        {
            throw new IOException(
                "Could not safely inspect the managed Local AI gateway configuration during uninstall.");
        }

        LocalAiGatewayPriorState current = ParseSnapshot(currentResult.Stdout);
        if (!current.ProviderExisted && !current.PrimaryModelExisted)
            return;
        if (install.Endpoint is null)
        {
            throw new InvalidDataException(
                "The Local AI manifest has no verified endpoint, so existing gateway settings cannot be proven app-owned.");
        }

        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        string expectedPrimary = JsonSerializer.Serialize(managedPrimary);
        string? fallbackModel = install.Manifest.GatewayFallbackModel;
        string? currentPrimary = null;
        bool currentPrimaryIsManaged = current.PrimaryModelExisted &&
            JsonEquals(current.PrimaryModelJson!, expectedPrimary);
        bool currentPrimaryIsFallback = current.PrimaryModelExisted &&
            fallbackModel is not null &&
            JsonEquals(current.PrimaryModelJson!, JsonSerializer.Serialize(fallbackModel));
        if (current.PrimaryModelExisted &&
            !currentPrimaryIsManaged &&
            !currentPrimaryIsFallback &&
            LocalAiGatewayProviderDefinition.TryReadPrimaryModelJson(current.PrimaryModelJson!, out string? parsed))
        {
            currentPrimary = parsed;
        }

        if ((current.ProviderExisted &&
                !LocalAiGatewayProviderDefinition.MatchesProviderJson(current.ProviderJson!, install)) ||
            (current.PrimaryModelExisted &&
                !currentPrimaryIsManaged &&
                !currentPrimaryIsFallback &&
                currentPrimary is null))
        {
            throw new InvalidDataException(
                "Local AI gateway settings changed after setup; preserving them instead of removing unproven values.");
        }

        if (currentPrimaryIsManaged && fallbackModel is not null)
        {
            string restorePrimary = JsonSerializer.Serialize(new[]
            {
                new { path = LocalAiGatewayConfigBuilder.PrimaryModelPath, value = fallbackModel },
            });
            CommandResult restored = await ApplyBatchAsync(
                ctx, restorePrimary, "LOCAL_AI_PRIMARY_RESTORED", ct).ConfigureAwait(false);
            if (restored.ExitCode != 0 || restored.TimedOut)
                throw new IOException("Restoring the prior gateway primary model failed during uninstall.");
        }

        var unset = new List<string>(capacity: 2);
        if (currentPrimaryIsManaged && fallbackModel is null)
            unset.Add($"openclaw config unset {LocalAiGatewayConfigBuilder.PrimaryModelPath}");
        if (current.ProviderExisted)
            unset.Add($"openclaw config unset {LocalAiGatewayConfigBuilder.ProviderPath}");

        if (unset.Count > 0)
        {
            string script = $"set -e\n{ctx.WslPathPrefix}\n{string.Join("\n", unset)}\necho LOCAL_AI_GATEWAY_UNSET";
            CommandResult result = await ctx.Commands.RunInWslAsync(
                ctx.DistroName!, script, TimeSpan.FromMinutes(2), ct: ct,
                user: ctx.Config.Wsl.User, inputViaStdin: true);
            if (result.ExitCode != 0 || result.TimedOut ||
                !result.Stdout.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal))
            {
                throw new IOException("Removing the managed Local AI gateway settings failed.");
            }
        }

        CommandResult verifiedResult = await CaptureStateAsync(ctx, ct).ConfigureAwait(false);
        if (verifiedResult.ExitCode != 0 || verifiedResult.TimedOut)
            throw new IOException("Could not verify Local AI gateway removal during uninstall.");
        LocalAiGatewayPriorState verified = ParseSnapshot(verifiedResult.Stdout);
        bool primaryIsSafe = fallbackModel is not null
            ? verified.PrimaryModelExisted &&
              JsonEquals(verified.PrimaryModelJson!, JsonSerializer.Serialize(fallbackModel))
            : currentPrimary is not null
                ? verified.PrimaryModelExisted &&
                  JsonEquals(verified.PrimaryModelJson!, JsonSerializer.Serialize(currentPrimary))
                : !verified.PrimaryModelExisted;
        if (verified.ProviderExisted || !primaryIsSafe)
            throw new IOException("Managed Local AI gateway settings remained after uninstall cleanup.");
    }

    private static Task<CommandResult> CaptureStateAsync(SetupContext ctx, CancellationToken ct)
    {
        string script = $$"""
            set -eu
            {{ctx.WslPathPrefix}}
            capture_value() {
              key="$1"
              marker="$2"
              temp_file="$(mktemp)"
              error_file="$(mktemp)"
              if openclaw config get "$key" --json >"$temp_file" 2>"$error_file"; then
                printf '%s%s\n' "$marker" "$(base64 -w0 <"$temp_file")"
              elif grep -Fq "Config path not found: $key" "$error_file"; then
                printf '%s{{MissingValue}}\n' "$marker"
              else
                cat "$error_file" >&2
                rm -f "$temp_file" "$error_file"
                return 1
              fi
              rm -f "$temp_file" "$error_file"
            }
            capture_value '{{LocalAiGatewayConfigBuilder.ProviderPath}}' '{{ProviderMarker}}'
            capture_value '{{LocalAiGatewayConfigBuilder.PrimaryModelPath}}' '{{PrimaryMarker}}'
            """;
        return ctx.Commands.RunInWslAsync(
            ctx.DistroName!, script, TimeSpan.FromMinutes(1), ct: ct,
            user: ctx.Config.Wsl.User, inputViaStdin: true);
    }

    private static Task<CommandResult> ApplyBatchAsync(
        SetupContext ctx,
        string batchJson,
        string successMarker,
        CancellationToken ct)
    {
        var environment = new Dictionary<string, string>
        {
            [BatchVariable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(batchJson)),
        };
        string script = $$"""
            set -e
            {{ctx.WslPathPrefix}}
            batch_file="$(mktemp)"
            trap 'rm -f "$batch_file"' EXIT
            printf '%s' "$OPENCLAW_LOCAL_AI_BATCH_B64" | base64 -d > "$batch_file"
            openclaw config set --batch-file "$batch_file" --dry-run
            openclaw config set --batch-file "$batch_file"
            echo {{successMarker}}
            """;
        return ctx.Commands.RunInWslAsync(
            ctx.DistroName!, script, TimeSpan.FromMinutes(2), environment, ct,
            user: ctx.Config.Wsl.User, inputViaStdin: true);
    }

    private static LocalAiGatewayPriorState ParseSnapshot(string stdout)
    {
        (bool providerExists, string? provider) = ParseMarker(stdout, ProviderMarker);
        (bool primaryExists, string? primary) = ParseMarker(stdout, PrimaryMarker);
        return new(providerExists, provider, primaryExists, primary);
    }

    private static (bool Exists, string? Json) ParseMarker(string stdout, string marker)
    {
        string? value = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))?[marker.Length..];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Missing configuration marker '{marker}'.");
        if (string.Equals(value, MissingValue, StringComparison.Ordinal))
            return (false, null);
        if (value.Length > MaximumSnapshotBytes * 2)
            throw new InvalidDataException("The configuration snapshot is too large.");

        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length > MaximumSnapshotBytes)
            throw new InvalidDataException("The configuration snapshot is too large.");
        string json = Encoding.UTF8.GetString(bytes);
        using JsonDocument _ = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        return (true, json);
    }

    private static string ExtractOperationValue(string batchJson, int index)
    {
        using JsonDocument document = JsonDocument.Parse(batchJson);
        return document.RootElement[index].GetProperty("value").GetRawText();
    }

    private static bool JsonEquals(string left, string right)
    {
        using JsonDocument leftDocument = JsonDocument.Parse(left);
        using JsonDocument rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }
}
