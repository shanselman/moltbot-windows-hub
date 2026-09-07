namespace OpenClaw.Shared.Inference.Catalog;

public enum LocalInferenceEligibilityStatus
{
    Eligible = 0,
    EligibleButBusy = 1,
    Unsupported = 2,
}

public enum LocalInferenceEligibilityFailureCode
{
    None = 0,
    CatalogSelectionFailed = 1,
    HardwareFactsIncomplete = 2,
    InsufficientGpuMemory = 3,
    DriverTooOld = 4,
    CudaCapabilityTooLow = 5,
}

public sealed record LocalInferenceEligibilityResult(
    LocalInferenceEligibilityStatus Status,
    LocalInferenceEligibilityFailureCode FailureCode,
    LocalInferenceSelectionFailureCode SelectionFailureCode,
    LocalInferencePlan? Plan,
    GpuInfo? SelectedGpu,
    long RequiredTotalMemoryBytes,
    long? DetectedTotalMemoryBytes,
    long RequiredFreeMemoryBytes,
    long? AvailableFreeMemoryBytes)
{
    public bool CanInstall => Status is
        LocalInferenceEligibilityStatus.Eligible or
        LocalInferenceEligibilityStatus.EligibleButBusy;
}

/// <summary>
/// Applies the pinned model capacity, driver, and CUDA guardrails after catalog
/// selection. Total GPU memory is stable capacity. Free GPU memory is launch
/// readiness and never changes the selected model automatically.
/// </summary>
public static class LocalInferenceEligibility
{
    public const long RuntimeWorkspaceReserveBytes = LocalModelCatalog.RuntimeWorkspaceReserveBytes;
    public static Version MinimumNvidiaDriverVersion { get; } = new(615, 0);

    public static long GetRequiredMemoryBytes(
        LocalModelInfo model,
        LocalInferenceRunProfile profile) =>
        LocalInferenceQualificationPolicy.GetRequiredMemoryBytes(model, profile);

    public static LocalInferenceEligibilityResult Evaluate(
        HostHardwareInfo hardware,
        string? requestedModelId = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        LocalInferenceSelectionResult selection = LocalInferenceSelector.Select(hardware, requestedModelId);
        if (!selection.IsSelected || selection.Plan is null)
        {
            return Unsupported(
                LocalInferenceEligibilityFailureCode.CatalogSelectionFailed,
                selection.FailureCode);
        }

        LocalInferencePlan plan = selection.Plan;
        long requiredMemoryBytes = GetRequiredMemoryBytes(plan.Model, plan.Profile);
        CandidateAssessment? selected = hardware.NvidiaGpus
            .Select(gpu => Assess(gpu, plan.Runtime, requiredMemoryBytes))
            .OrderBy(candidate => StatusRank(candidate.Status))
            .ThenBy(candidate => DefinitivenessRank(candidate.FailureCode))
            .ThenByDescending(candidate => candidate.FreeMemoryBytes.HasValue)
            .ThenByDescending(candidate => candidate.FreeMemoryBytes ?? long.MinValue)
            .ThenByDescending(candidate => candidate.TotalMemoryBytes)
            .ThenBy(candidate => candidate.Gpu.StableId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Gpu.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is null)
            return Unsupported(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete);

        return new LocalInferenceEligibilityResult(
            selected.Status,
            selected.FailureCode,
            LocalInferenceSelectionFailureCode.None,
            plan,
            selected.Gpu,
            requiredMemoryBytes,
            selected.TotalMemoryBytes > 0 ? selected.TotalMemoryBytes : null,
            requiredMemoryBytes,
            selected.FreeMemoryBytes);
    }

    private static LocalInferenceEligibilityResult Unsupported(
        LocalInferenceEligibilityFailureCode failureCode,
        LocalInferenceSelectionFailureCode selectionFailureCode = LocalInferenceSelectionFailureCode.None,
        GpuInfo? selectedGpu = null) =>
        new(
            LocalInferenceEligibilityStatus.Unsupported,
            failureCode,
            selectionFailureCode,
            null,
            selectedGpu,
            0,
            selectedGpu is null ? null : LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(selectedGpu),
            0,
            selectedGpu is null ? null : LocalInferenceQualificationPolicy.GetEffectiveFreeMemoryBytes(selectedGpu));

    private static CandidateAssessment Assess(
        GpuInfo gpu,
        LlamaRuntimeVariant runtime,
        long requiredMemoryBytes)
    {
        long totalMemoryBytes = LocalInferenceQualificationPolicy.GetEffectiveTotalMemoryBytes(gpu);
        long? freeMemoryBytes = LocalInferenceQualificationPolicy.GetEffectiveFreeMemoryBytes(gpu);
        if (!LocalInferenceQualificationPolicy.HasCompleteFacts(gpu))
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        if (gpu.CudaMajorVersion < runtime.CudaVersion.Major)
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        if (totalMemoryBytes < requiredMemoryBytes)
        {
            return UnsupportedCandidate(
                gpu,
                LocalInferenceEligibilityFailureCode.InsufficientGpuMemory,
                totalMemoryBytes,
                freeMemoryBytes);
        }

        LocalInferenceEligibilityStatus status =
            freeMemoryBytes is not null && freeMemoryBytes < requiredMemoryBytes
                ? LocalInferenceEligibilityStatus.EligibleButBusy
                : LocalInferenceEligibilityStatus.Eligible;
        return new CandidateAssessment(
            gpu,
            status,
            LocalInferenceEligibilityFailureCode.None,
            totalMemoryBytes,
            freeMemoryBytes);
    }

    private static CandidateAssessment UnsupportedCandidate(
        GpuInfo gpu,
        LocalInferenceEligibilityFailureCode failureCode,
        long totalMemoryBytes,
        long? freeMemoryBytes) =>
        new(
            gpu,
            LocalInferenceEligibilityStatus.Unsupported,
            failureCode,
            totalMemoryBytes,
            freeMemoryBytes);

    private static int StatusRank(LocalInferenceEligibilityStatus status) => status switch
    {
        LocalInferenceEligibilityStatus.Eligible => 0,
        LocalInferenceEligibilityStatus.EligibleButBusy => 1,
        _ => 2,
    };

    /// <summary>
    /// Ranks an inconclusive candidate ahead of a definitively incompatible one.
    /// A GPU whose facts could not be read might still work, so reporting the
    /// retryable state keeps recheck available instead of showing a permanent
    /// "this device cannot run Local AI" verdict from a different adapter.
    /// </summary>
    private static int DefinitivenessRank(LocalInferenceEligibilityFailureCode failureCode) =>
        failureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete ? 0 : 1;

    private sealed record CandidateAssessment(
        GpuInfo Gpu,
        LocalInferenceEligibilityStatus Status,
        LocalInferenceEligibilityFailureCode FailureCode,
        long TotalMemoryBytes,
        long? FreeMemoryBytes);
}
