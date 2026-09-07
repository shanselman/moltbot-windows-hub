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

public sealed class CleanupStaleGatewayStep : SetupStep
{
    public override string Id => "cleanup-gateway";
    public override string DisplayName => "Clean up stale gateway state";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        // Remove stale setup-state.json from AppData (legacy location)
        var stateFile = Path.Combine(ctx.DataDir, "setup-state.json");
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (AppData)");
        }

        // Also remove from LocalAppData (current write location)
        var localStateFile = Path.Combine(ctx.LocalDataDir, "setup-state.json");
        if (File.Exists(localStateFile))
        {
            File.Delete(localStateFile);
            ctx.Logger.Info("Deleted stale setup-state.json (LocalAppData)");
        }

        // Remove stale setup-managed records for our local URL. Multiple records
        // can exist when an older unmarked record sorts before managed records.
        var registry = new GatewayRegistry(ctx.DataDir, logger: new SetupOpenClawLogger(ctx.Logger));
        registry.Load();
        var staleRecords = new List<GatewayRecord>();
        foreach (var existing in registry.FindAllByUrl(ctx.GatewayUrl!))
        {
            // Preserve non-local records and SSH-tunneled gateways — they may be
            // remote gateways that happen to use localhost as a forwarded port.
            if (!PairOperatorStep.IsSetupManagedLocalRecord(existing, ctx))
            {
                ctx.Logger.Warn($"Skipping cleanup of gateway record {existing.Id}: " +
                    "not a SetupEngine-managed local gateway");
            }
            else
            {
                staleRecords.Add(existing);
            }
        }

        if (staleRecords.Count > 0)
        {
            var originalActiveId = registry.ActiveGatewayId;
            foreach (var staleRecord in staleRecords)
                registry.Remove(staleRecord.Id);

            // Persist the complete record and active-ID transition before deleting
            // identities so the durable registry never references a missing identity.
            registry.Save();

            var cleanupFailures = new List<(GatewayRecord Record, Exception Error)>();
            foreach (var staleRecord in staleRecords)
            {
                var identityDir = registry.GetIdentityDirectory(staleRecord.Id);
                if (Directory.Exists(identityDir))
                {
                    try
                    {
                        Directory.Delete(identityDir, recursive: true);
                        ctx.Logger.Info($"Deleted stale identity directory: {identityDir}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        cleanupFailures.Add((staleRecord, ex));
                        ctx.Logger.Warn(
                            $"Failed to delete stale identity directory {identityDir}: {ex.Message}");
                    }
                }
            }

            if (cleanupFailures.Count > 0)
            {
                foreach (var (record, _) in cleanupFailures)
                    registry.AddOrUpdate(record);

                if (cleanupFailures.Any(failure =>
                    string.Equals(
                        failure.Record.Id,
                        originalActiveId,
                        StringComparison.Ordinal)))
                {
                    registry.SetActive(originalActiveId);
                }

                registry.Save();
                throw new AggregateException(
                    "One or more stale gateway identities could not be removed. " +
                    "Their registry records were restored so cleanup can be retried.",
                    cleanupFailures.Select(failure => failure.Error));
            }

            ctx.Logger.Info(
                $"Removed {staleRecords.Count} stale gateway record(s) for {ctx.GatewayUrl}");
        }

        await Task.CompletedTask;
        return StepResult.Ok("Gateway state cleaned");
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        // Delete setup-state.json (written by VerifyEndToEndStep)
        var localDataPath = ctx.LocalDataDir;

        var stateFile = Path.Combine(localDataPath, "setup-state.json");
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
            ctx.Logger.Info("[Uninstall] Deleted setup-state.json");
        }
        else
        {
            ctx.Logger.Info("[Uninstall] setup-state.json already absent");
        }

        return Task.CompletedTask;
    }
}
