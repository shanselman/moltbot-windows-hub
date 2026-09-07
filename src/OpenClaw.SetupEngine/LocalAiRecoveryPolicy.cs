using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

public static class LocalAiRecoveryPolicy
{
    public static bool CanRecoverExistingGateway(
        ExistingConfigDetector.ExistingConfig existing,
        string expectedGatewayId,
        string targetDistroName,
        string expectedGatewayUrl) =>
        existing.HasLocalGateway &&
        string.Equals(
            existing.LocalGatewayId,
            expectedGatewayId,
            StringComparison.Ordinal) &&
        existing.HasDistro &&
        existing.DistroIsAppOwned &&
        string.Equals(
            existing.DistroName,
            targetDistroName,
            StringComparison.OrdinalIgnoreCase) &&
        GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
            existing.LocalGatewayUrl,
            expectedGatewayUrl);
}

public sealed class ValidateLocalAiRecoveryGatewayStep : SetupStep
{
    private readonly Func<string, string, string?, string?, ExistingConfigDetector.ExistingConfig> _detect;
    private readonly Func<string, IReadOnlyList<GatewayRecord>> _loadGatewayRecords;
    private readonly bool _finalCheck;

    public ValidateLocalAiRecoveryGatewayStep(bool finalCheck = false)
        : this(ExistingConfigDetector.Detect, LoadGatewayRecords, finalCheck)
    {
    }

    internal ValidateLocalAiRecoveryGatewayStep(
        Func<string, string, string?, string?, ExistingConfigDetector.ExistingConfig> detect,
        Func<string, IReadOnlyList<GatewayRecord>> loadGatewayRecords,
        bool finalCheck = false)
    {
        _detect = detect ?? throw new ArgumentNullException(nameof(detect));
        _loadGatewayRecords =
            loadGatewayRecords ?? throw new ArgumentNullException(nameof(loadGatewayRecords));
        _finalCheck = finalCheck;
    }

    public override string Id => _finalCheck
        ? "revalidate-local-ai-recovery-gateway"
        : "validate-local-ai-recovery-gateway";
    public override string DisplayName => _finalCheck
        ? "Recheck gateway before Local AI recovery changes"
        : "Verify existing gateway for Local AI recovery";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var expectedGatewayId = ctx.Config.LocalAiRecoveryGatewayId;
        if (string.IsNullOrWhiteSpace(expectedGatewayId))
        {
            return Task.FromResult(StepResult.Terminal(
                "Local AI recovery is missing its managed gateway identity. Close recovery and retry."));
        }

        var owners = _loadGatewayRecords(ctx.DataDir)
            .Where(record =>
                GatewayRecordEditing.IsSetupManagedLocalRecord(record) &&
                !string.IsNullOrWhiteSpace(record.Id) &&
                !string.IsNullOrWhiteSpace(
                    GatewayRecordEditing.ResolveManagedDistroName(record)))
            .ToArray();
        if (owners.Length != 1 ||
            !string.Equals(owners[0].Id, expectedGatewayId, StringComparison.Ordinal) ||
            !string.Equals(
                GatewayRecordEditing.ResolveManagedDistroName(owners[0]),
                ctx.Config.DistroName,
                StringComparison.OrdinalIgnoreCase) ||
            !GatewayRecordEditing.AreEquivalentLoopbackEndpoints(
                owners[0].Url,
                ctx.Config.EffectiveGatewayUrl))
        {
            return Task.FromResult(StepResult.Terminal(
                "The managed gateway owner changed before Local AI recovery. Close recovery and review Connection settings."));
        }

        ExistingConfigDetector.ExistingConfig existing;
        try
        {
            existing = _detect(
                ctx.DataDir,
                ctx.Config.DistroName,
                ctx.LocalDataDir,
                expectedGatewayId);
        }
        catch (Exception ex)
        {
            ctx.Logger.Warn($"Local AI recovery gateway inspection failed: {ex.Message}");
            return Task.FromResult(StepResult.Terminal(
                "OpenClaw could not safely verify the existing managed gateway. Close recovery and run full setup."));
        }

        return Task.FromResult(
            LocalAiRecoveryPolicy.CanRecoverExistingGateway(
                existing,
                expectedGatewayId,
                ctx.Config.DistroName,
                ctx.Config.EffectiveGatewayUrl)
                ? StepResult.Ok("Existing app-managed gateway verified for Local AI recovery.")
                : StepResult.Terminal(
                    "The existing app-managed gateway is unavailable. Close recovery and run full setup."));
    }

    private static IReadOnlyList<GatewayRecord> LoadGatewayRecords(string dataDir)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        return registry.GetAll();
    }
}

public sealed class PreserveLocalAiRecoveryGatewayStep : SetupStep
{
    private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _restart;

    public PreserveLocalAiRecoveryGatewayStep()
        : this(StartGatewayStep.RestartAndWaitForHealthAsync)
    {
    }

    internal PreserveLocalAiRecoveryGatewayStep(
        Func<SetupContext, CancellationToken, Task<StepResult>> restart) =>
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));

    public override string Id => "preserve-local-ai-recovery-gateway";
    public override string DisplayName => "Preserve gateway during Local AI recovery";
    public override bool CanRetry => false;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
        Task.FromResult(StepResult.Ok("Gateway recovery guard armed."));

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!ctx.LocalAiRecoveryStoppedWsl)
            return;

        StepResult restart = await _restart(ctx, ct);
        if (!restart.IsSuccess)
            throw new InvalidOperationException(restart.Message);

        ctx.LocalAiRecoveryStoppedWsl = false;
    }
}
