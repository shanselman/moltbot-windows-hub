using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Whether catalog selection produced a complete native inference plan.</summary>
public enum LocalInferenceSelectionStatus
{
    Selected = 0,
    Unsupported = 1,
}

/// <summary>Stable reason returned when no inference plan can be selected.</summary>
public enum LocalInferenceSelectionFailureCode
{
    None = 0,
    RuntimeUnavailable = 1,
    NoNvidiaGpu = 2,
    UnknownModel = 3,
}

/// <summary>Whether a caller accepted the catalog default or named a model explicitly.</summary>
public enum LocalInferenceModelSelectionOrigin
{
    Default = 0,
    Explicit = 1,
}

/// <summary>A complete, immutable native inference choice.</summary>
public sealed record LocalInferencePlan(
    LlamaRuntimeVariant Runtime,
    LocalModelInfo Model,
    LocalInferenceRunProfile Profile,
    LocalInferenceModelSelectionOrigin ModelSelectionOrigin);

/// <summary>The deterministic result of selecting from the pinned local inference catalog.</summary>
public sealed record LocalInferenceSelectionResult
{
    private LocalInferenceSelectionResult(
        LocalInferenceSelectionStatus status,
        LocalInferenceSelectionFailureCode failureCode,
        LocalInferencePlan? plan)
    {
        Status = status;
        FailureCode = failureCode;
        Plan = plan;
    }

    public LocalInferenceSelectionStatus Status { get; }
    public LocalInferenceSelectionFailureCode FailureCode { get; }
    public LocalInferencePlan? Plan { get; }
    public bool IsSelected => Status == LocalInferenceSelectionStatus.Selected;

    internal static LocalInferenceSelectionResult Selected(LocalInferencePlan plan) =>
        new(LocalInferenceSelectionStatus.Selected, LocalInferenceSelectionFailureCode.None, plan);

    internal static LocalInferenceSelectionResult Unsupported(LocalInferenceSelectionFailureCode failureCode) =>
        new(LocalInferenceSelectionStatus.Unsupported, failureCode, null);
}

/// <summary>
/// Pure selection from a hardware snapshot and optional model ID. The CPU
/// architecture chooses only the native runtime. GPU names and CPU/GPU SKU
/// pairings are not part of qualification.
/// </summary>
public static class LocalInferenceSelector
{
    public static LocalInferenceSelectionResult Select(
        HostHardwareInfo hardware,
        string? requestedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        LlamaRuntimeVariant? runtime = LlamaRuntimeCatalog.Find(hardware.CpuArchitecture);
        if (runtime is null)
            return LocalInferenceSelectionResult.Unsupported(
                LocalInferenceSelectionFailureCode.RuntimeUnavailable);

        if (!hardware.HasNvidiaGpu)
            return LocalInferenceSelectionResult.Unsupported(LocalInferenceSelectionFailureCode.NoNvidiaGpu);

        LocalModelInfo? model;
        LocalInferenceRunProfile profile;
        LocalInferenceModelSelectionOrigin modelSelectionOrigin;
        if (string.IsNullOrWhiteSpace(requestedModelId))
        {
            (model, profile) = SelectDefaultModelAndProfile(hardware, runtime);
            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Default;
        }
        else
        {
            model = LocalModelCatalog.Find(requestedModelId);
            if (model is null)
                return LocalInferenceSelectionResult.Unsupported(LocalInferenceSelectionFailureCode.UnknownModel);
            profile = SelectBestFittingProfile(hardware, runtime, model) ??
                LocalModelCatalog.GetProfiles(model)[^1];
            modelSelectionOrigin = LocalInferenceModelSelectionOrigin.Explicit;
        }

        return LocalInferenceSelectionResult.Selected(
            new LocalInferencePlan(runtime, model, profile, modelSelectionOrigin));
    }

    private static (LocalModelInfo Model, LocalInferenceRunProfile Profile) SelectDefaultModelAndProfile(
        HostHardwareInfo hardware,
        LlamaRuntimeVariant runtime)
    {
        foreach (LocalModelInfo candidate in LocalModelCatalog.Models
                     .OrderByDescending(model => model.RecommendationPriority)
                     .ThenByDescending(model => model.Weights.SizeBytes))
        {
            LocalInferenceRunProfile? profile = SelectBestFittingProfile(hardware, runtime, candidate);
            if (profile is not null)
                return (candidate, profile);
        }

        LocalModelInfo fallback = LocalModelCatalog.Models.OrderBy(model => model.Weights.SizeBytes).First();
        return (fallback, LocalModelCatalog.GetProfiles(fallback)[^1]);
    }

    private static LocalInferenceRunProfile? SelectBestFittingProfile(
        HostHardwareInfo hardware,
        LlamaRuntimeVariant runtime,
        LocalModelInfo model) =>
        LocalModelCatalog.GetProfiles(model).FirstOrDefault(profile =>
            hardware.NvidiaGpus.Any(gpu =>
                LocalInferenceQualificationPolicy.HasRuntimePrerequisites(gpu, runtime) &&
                LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(gpu) >=
                    LocalInferenceQualificationPolicy.GetRequiredMemoryBytes(model, profile)));
}

internal static class LocalInferenceQualificationPolicy
{
    public static bool HasCompleteFacts(GpuInfo gpu) =>
        IsStableGpuId(gpu.StableId) &&
        gpu.GpuVisibleMemoryBytes is > 0 &&
        gpu.CudaMajorVersion is not null;

    public static bool HasRuntimePrerequisites(GpuInfo gpu, LlamaRuntimeVariant runtime)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(runtime);
        return HasCompleteFacts(gpu) &&
            gpu.CudaMajorVersion >= runtime.CudaVersion.Major;
    }

    public static long GetRequiredMemoryBytes(
        LocalModelInfo model,
        LocalInferenceRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        return SaturatingAdd(
            SaturatingAdd(
                SaturatingAdd(model.Weights.SizeBytes, GetKvCacheMemoryBytes(model.Recipe, profile)),
                GetDraftKvCacheMemoryBytes(model.Recipe, profile)),
            profile.RuntimeWorkspaceBytes);
    }

    internal static long GetKvCacheMemoryBytes(
        LocalModelRunRecipe recipe,
        LocalInferenceRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(profile);
        long vectorsPerTypePerToken = SaturatingMultiply(
            recipe.FullAttentionLayerCount,
            recipe.KeyValueHeadCount);
        long bytesPerToken = SaturatingAdd(
            SaturatingMultiply(
                vectorsPerTypePerToken,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.KeyCachePrecision)),
            SaturatingMultiply(
                vectorsPerTypePerToken,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.ValueCachePrecision)));
        return SaturatingMultiply(bytesPerToken, profile.ContextTokens);
    }

    internal static long GetDraftKvCacheMemoryBytes(
        LocalModelRunRecipe recipe,
        LocalInferenceRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(profile);

        // The pinned Qwen MTP artifacts contain one draft attention layer with
        // the same KV head count and head dimension as the target model.
        long bytesPerToken = SaturatingAdd(
            SaturatingMultiply(
                recipe.KeyValueHeadCount,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.DraftKeyCachePrecision)),
            SaturatingMultiply(
                recipe.KeyValueHeadCount,
                EncodedBytes(recipe.KeyValueHeadDimension, profile.DraftValueCachePrecision)));
        return SaturatingMultiply(bytesPerToken, profile.ContextTokens);
    }

    public static long GetEffectiveTotalMemoryBytes(GpuInfo gpu) =>
        gpu.GpuVisibleMemoryBytes is > 0 ? gpu.GpuVisibleMemoryBytes.Value : 0;

    public static long? GetEffectiveFreeMemoryBytes(GpuInfo gpu) =>
        gpu.FreeGpuVisibleMemoryBytes is >= 0 ? gpu.FreeGpuVisibleMemoryBytes.Value : null;

    private static bool IsStableGpuId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static long EncodedBytes(long elementCount, KvCachePrecision precision) => precision switch
    {
        KvCachePrecision.F16 => SaturatingMultiply(elementCount, 2),
        KvCachePrecision.Q8_0 => SaturatingMultiply((SaturatingAdd(elementCount, 31)) / 32, 34),
        _ => throw new ArgumentOutOfRangeException(nameof(precision)),
    };

    private static long SaturatingMultiply(long left, long right) =>
        left == 0 || right == 0
            ? 0
            : left > long.MaxValue / right
                ? long.MaxValue
                : left * right;

    public static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}
