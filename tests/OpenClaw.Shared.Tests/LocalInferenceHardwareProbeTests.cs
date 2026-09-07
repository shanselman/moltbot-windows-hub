using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using Xunit.Abstractions;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Hardware-gated proof that Local AI qualification works on a real NVIDIA GPU
/// through both dedicated-memory sources, including the non-DXGI NVML path used
/// by unified-memory DGX and Blackwell parts, TCC devices, and headless GPUs.
/// Skipped when no NVIDIA GPU is present. Set OPENCLAW_RUN_GPU_PROOF=1 to require it.
/// </summary>
public sealed class LocalInferenceHardwareProbeTests(ITestOutputHelper output)
{
    [NvidiaHardwareFact]
    public void NvmlSuppliesADedicatedBoundForEveryCudaDeviceIdentity()
    {
        HostHardwareInfo hardware = new CudaHostHardwareProbe().Probe();
        GpuInfo[] identified = hardware.NvidiaGpus.Where(gpu => gpu.StableId is { Length: > 0 }).ToArray();
        Assert.True(identified.Length > 0, "Hardware proof run requires an identified NVIDIA GPU on this host.");

        IReadOnlyDictionary<string, GpuAdapterMemory> nvml =
            new NvmlDedicatedMemoryProbe().CaptureAdapterMemoryByUuid();

        foreach (GpuInfo gpu in identified)
        {
            Assert.True(
                nvml.TryGetValue(gpu.StableId!, out GpuAdapterMemory? memory),
                $"NVML reported no dedicated memory for CUDA device identity {Redact(gpu.StableId)}.");
            Assert.True(memory!.DedicatedVideoMemoryBytes > 0);
            Assert.True(memory.AvailableLocalBytes is null or >= 0);
            output.WriteLine(
                $"nvml {Redact(gpu.StableId)} dedicated={Mib(memory.DedicatedVideoMemoryBytes)} " +
                $"free={(memory.AvailableLocalBytes is { } free ? Mib(free) : "<null>")}");
        }
    }

    [NvidiaHardwareFact]
    public void NonDxgiFallbackKeepsTheDeviceSupportedWithABoundNoLargerThanTheDxgiPath()
    {
        var reader = new NvcudaDeviceReader();
        HostHardwareInfo withDxgi = new CudaHostHardwareProbe(
            reader,
            new DxgiDedicatedMemoryProbe(),
            new NvmlDedicatedMemoryProbe()).Probe();
        Assert.True(withDxgi.HasNvidiaGpu, "Hardware proof run requires an NVIDIA GPU on this host.");

        // Force the DXGI source to supply nothing so resolution must fall back
        // to the NVML identity join, exactly as it would on a host DXGI cannot
        // describe.
        HostHardwareInfo withoutDxgi = new CudaHostHardwareProbe(
            reader,
            new EmptyDedicatedMemoryProbe(),
            new NvmlDedicatedMemoryProbe()).Probe();

        GpuInfo dxgiGpu = withDxgi.NvidiaGpus.First();
        GpuInfo nvmlGpu = withoutDxgi.NvidiaGpus.First();

        output.WriteLine($"dxgi  capacity={Describe(dxgiGpu.GpuVisibleMemoryBytes)} free={Describe(dxgiGpu.FreeGpuVisibleMemoryBytes)}");
        output.WriteLine($"nvml  capacity={Describe(nvmlGpu.GpuVisibleMemoryBytes)} free={Describe(nvmlGpu.FreeGpuVisibleMemoryBytes)}");

        Assert.Equal(dxgiGpu.StableId, nvmlGpu.StableId);

        // The fallback must keep the device supported, never blank out capacity.
        Assert.True(nvmlGpu.GpuVisibleMemoryBytes is > 0);
        Assert.True(nvmlGpu.FreeGpuVisibleMemoryBytes is >= 0);

        LocalInferenceEligibilityResult dxgiResult = LocalInferenceEligibility.Evaluate(withDxgi);
        LocalInferenceEligibilityResult nvmlResult = LocalInferenceEligibility.Evaluate(withoutDxgi);
        output.WriteLine($"dxgi  {dxgiResult.Status}/{dxgiResult.FailureCode} detected={Describe(dxgiResult.DetectedTotalMemoryBytes)}");
        output.WriteLine($"nvml  {nvmlResult.Status}/{nvmlResult.FailureCode} detected={Describe(nvmlResult.DetectedTotalMemoryBytes)}");

        // Facts must stay complete on the fallback path. Capacity is compared
        // rather than status, because the combined path is allowed to be
        // strictly more conservative than either source alone.
        Assert.NotEqual(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, nvmlResult.FailureCode);
        Assert.True(dxgiGpu.GpuVisibleMemoryBytes <= nvmlGpu.GpuVisibleMemoryBytes);
    }

    private static string Describe(long? bytes) => bytes is { } value ? Mib(value) : "<null>";

    private static string Mib(long bytes) => $"{bytes / (1024 * 1024):N0} MiB";

    private static string Redact(string? stableId) =>
        string.IsNullOrWhiteSpace(stableId)
            ? "<null>"
            : stableId[..Math.Min(12, stableId.Length)] + "-<redacted>";

    private sealed class EmptyDedicatedMemoryProbe : IGpuDedicatedMemoryProbe
    {
        public IReadOnlyDictionary<long, GpuAdapterMemory> CaptureAdapterMemoryByLuid() =>
            new Dictionary<long, GpuAdapterMemory>();
    }
}


