using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// The CUDA device facts the host probe needs, as a seam so failure handling can
/// be covered without NVIDIA hardware.
/// </summary>
internal interface ICudaDeviceReader
{
    CudaDriverAvailability TryInitialize();
    int? TryReadDeviceCount();
    int? TryReadCudaMajorVersion();
    int? TryReadDeviceHandle(int ordinal);
    string? TryReadDeviceName(int device);
    string? TryReadDeviceUuid(int device);
    long? TryReadDeviceLuid(int device);
    (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device);
}

/// <summary>Reads device facts from the real NVIDIA driver.</summary>
internal sealed class NvcudaDeviceReader : ICudaDeviceReader
{
    public CudaDriverAvailability TryInitialize() => NvcudaDriver.TryInitialize();

    public int? TryReadDeviceCount() => NvcudaDriver.TryReadDeviceCount();

    public int? TryReadCudaMajorVersion() => NvcudaDriver.TryReadCudaMajorVersion();

    public int? TryReadDeviceHandle(int ordinal) => NvcudaDriver.TryReadDeviceHandle(ordinal);

    public string? TryReadDeviceName(int device) => NvcudaDriver.TryReadDeviceName(device);

    public string? TryReadDeviceUuid(int device) => NvcudaDriver.TryReadDeviceUuid(device);

    public long? TryReadDeviceLuid(int device) => NvcudaDriver.TryReadDeviceLuid(device);

    public (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device) =>
        NvcudaDriver.WithContext(device, NvcudaDriver.TryReadMemoryInfo, null);
}

public interface IHostHardwareProbe
{
    HostHardwareInfo Probe();
}

/// <summary>
/// Reads the CUDA driver's device identity and the memory that actually backs
/// device allocations on this adapter. This is the sole GPU-memory source for
/// Local AI qualification, including UMA devices.
/// </summary>
/// <remarks>
/// <para>
/// Capacity is the CUDA-reported total capped by the adapter's DXGI dedicated
/// video memory. <c>cuMemGetInfo</c> alone is not a safe WDDM admission bound:
/// on a DGX Spark it advertised roughly 46 GiB because CUDA surfaces the shared
/// host pool, while llama-server still failed an approximately 15.81 GiB
/// allocation against roughly 15.9 GiB of real device memory. Capping never
/// raises reported capacity, and shared system memory is never added.
/// </para>
/// <para>
/// Only a failed <c>cuInit</c> proves that this machine has no NVIDIA GPU. Once
/// the driver initializes, every later read failure keeps the device in the list
/// with the missing facts left null, so qualification reports the retryable
/// <c>HardwareFactsIncomplete</c> state instead of the definitive
/// <c>NoNvidiaGpu</c> state that permanently hides Local AI.
/// </para>
/// </remarks>
public sealed class CudaHostHardwareProbe : IHostHardwareProbe
{
    internal const string UnidentifiedGpuName = "NVIDIA GPU";

    private readonly ICudaDeviceReader _reader;
    private readonly IGpuDedicatedMemoryProbe _dedicatedMemoryProbe;
    private readonly INvmlDedicatedMemoryProbe _nvmlMemoryProbe;

    public CudaHostHardwareProbe()
        : this(new NvcudaDeviceReader(), new DxgiDedicatedMemoryProbe(), new NvmlDedicatedMemoryProbe())
    {
    }

    internal CudaHostHardwareProbe(
        ICudaDeviceReader reader,
        IGpuDedicatedMemoryProbe? dedicatedMemoryProbe = null,
        INvmlDedicatedMemoryProbe? nvmlMemoryProbe = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _dedicatedMemoryProbe = dedicatedMemoryProbe ?? new DxgiDedicatedMemoryProbe();
        _nvmlMemoryProbe = nvmlMemoryProbe ?? new NvmlDedicatedMemoryProbe();
    }

    public HostHardwareInfo Probe()
    {
        PhysicalMemorySnapshot? physicalMemory = null;
        try { physicalMemory = PhysicalMemoryProbe.TryRead(); } catch { }

        IReadOnlyList<GpuInfo> gpus;
        try { gpus = CaptureCudaGpus(); } catch { gpus = []; }

        return new HostHardwareInfo(
            RuntimeInformation.OSArchitecture,
            physicalMemory?.TotalBytes,
            physicalMemory?.AvailableBytes,
            gpus,
            VulkanAvailable: false);
    }

    private IReadOnlyList<GpuInfo> CaptureCudaGpus()
    {
        // Only an absent driver or a driver that reports no device is proof that
        // this machine has no NVIDIA GPU. A driver that fails to initialize (for
        // example on a driver/runtime mismatch) leaves presence unknown, so it
        // must stay retryable instead of permanently hiding Local AI.
        switch (Read(() => (CudaDriverAvailability?)_reader.TryInitialize()))
        {
            case CudaDriverAvailability.Absent:
            case CudaDriverAvailability.NoDevice:
                return [];
            case CudaDriverAvailability.Ready:
                break;
            default:
                return [Unidentified()];
        }

        // Every failure from here on is a partial read, not an absent GPU.
        if (Read(() => _reader.TryReadDeviceCount()) is not { } devices)
            return [Unidentified()];

        if (devices <= 0)
            return [];

        int? cudaMajorVersion = Read(() => _reader.TryReadCudaMajorVersion());

        IReadOnlyDictionary<long, GpuAdapterMemory> adapterMemoryByLuid;
        try { adapterMemoryByLuid = _dedicatedMemoryProbe.CaptureAdapterMemoryByLuid(); }
        catch { adapterMemoryByLuid = new Dictionary<long, GpuAdapterMemory>(); }

        IReadOnlyDictionary<string, GpuAdapterMemory> adapterMemoryByUuid;
        try { adapterMemoryByUuid = _nvmlMemoryProbe.CaptureAdapterMemoryByUuid(); }
        catch { adapterMemoryByUuid = new Dictionary<string, GpuAdapterMemory>(); }

        return Enumerable.Range(0, devices)
            .Select(ordinal => CaptureGpu(ordinal, cudaMajorVersion, adapterMemoryByLuid, adapterMemoryByUuid))
            .ToList();
    }

    private GpuInfo CaptureGpu(
        int ordinal,
        int? cudaMajorVersion,
        IReadOnlyDictionary<long, GpuAdapterMemory> adapterMemoryByLuid,
        IReadOnlyDictionary<string, GpuAdapterMemory> adapterMemoryByUuid)
    {
        // Contained per device so one failing device or entry point cannot
        // discard the devices that read cleanly.
        try
        {
            if (_reader.TryReadDeviceHandle(ordinal) is not { } device)
                return Unidentified(cudaMajorVersion);

            // Every fact is read independently so one failure cannot discard the
            // others, and a missing UUID keeps the device visible as incomplete
            // rather than absent.
            var identifiedGpu = new GpuInfo(
                GpuVendor.Nvidia,
                Read(() => _reader.TryReadDeviceName(device)) ?? UnidentifiedGpuName,
                CudaMajorVersion: cudaMajorVersion,
                StableId: Read(() => _reader.TryReadDeviceUuid(device)));

            if (Read(() => _reader.TryReadMemoryInfo(device)) is not { } memory ||
                ResolveAdapterMemory(device, identifiedGpu.StableId, adapterMemoryByLuid, adapterMemoryByUuid)
                    is not { } adapterMemory)
            {
                // Without a dedicated-memory bound the CUDA total cannot be
                // trusted for admission, so capacity stays unknown and
                // qualification retries instead of over-qualifying this device.
                return identifiedGpu;
            }

            long capacityBytes = Math.Min(memory.TotalBytes, adapterMemory.DedicatedVideoMemoryBytes);
            return identifiedGpu with
            {
                GpuVisibleMemoryBytes = capacityBytes,
                FreeGpuVisibleMemoryBytes = ResolveFreeBytes(memory, adapterMemory, capacityBytes),
            };
        }
        catch
        {
            return Unidentified(cudaMajorVersion);
        }
    }

    /// <summary>
    /// The dedicated-memory bound for one device. Every source that identifies
    /// this exact device contributes, and the most conservative value wins, so a
    /// device is never admitted on a larger bound just because one source
    /// happened to resolve first. Both joins are on device identity, never on
    /// adapter name.
    /// </summary>
    private GpuAdapterMemory? ResolveAdapterMemory(
        int device,
        string? stableId,
        IReadOnlyDictionary<long, GpuAdapterMemory> adapterMemoryByLuid,
        IReadOnlyDictionary<string, GpuAdapterMemory> adapterMemoryByUuid)
    {
        GpuAdapterMemory? dxgiMemory = null;
        if (Read(() => _reader.TryReadDeviceLuid(device)) is { } luid &&
            adapterMemoryByLuid.TryGetValue(luid, out GpuAdapterMemory? byLuid) &&
            byLuid.DedicatedVideoMemoryBytes > 0)
        {
            dxgiMemory = byLuid;
        }

        GpuAdapterMemory? nvmlMemory = null;
        if (stableId is { Length: > 0 } &&
            adapterMemoryByUuid.TryGetValue(stableId, out GpuAdapterMemory? byUuid) &&
            byUuid.DedicatedVideoMemoryBytes > 0)
        {
            nvmlMemory = byUuid;
        }

        if (dxgiMemory is null)
            return nvmlMemory;
        if (nvmlMemory is null)
            return dxgiMemory;

        // DXGI reports this process's WDDM budget while NVML reports device-wide
        // unallocated memory. They answer different questions, so taking the
        // smaller of the two keeps the admission decision deterministic and
        // conservative regardless of which sources a host exposes.
        return new GpuAdapterMemory(
            Math.Min(dxgiMemory.DedicatedVideoMemoryBytes, nvmlMemory.DedicatedVideoMemoryBytes),
            MinimumOrNull(dxgiMemory.AvailableLocalBytes, nvmlMemory.AvailableLocalBytes));
    }

    private static long? MinimumOrNull(long? left, long? right) =>
        left is { } leftValue
            ? right is { } rightValue ? Math.Min(leftValue, rightValue) : leftValue
            : right;

    /// <summary>
    /// The adapter's own budget is the authority on how much memory this process
    /// may still use, because CUDA's free value can also count the shared host
    /// pool. CUDA's figure is only a fallback, and either way the result never
    /// exceeds the admission capacity.
    /// </summary>
    private static long ResolveFreeBytes(
        (long FreeBytes, long TotalBytes) memory,
        GpuAdapterMemory adapterMemory,
        long capacityBytes) =>
        Math.Min(adapterMemory.AvailableLocalBytes ?? memory.FreeBytes, capacityBytes);

    private static T? Read<T>(Func<T?> read)
        where T : struct
    {
        try { return read(); } catch { return null; }
    }

    private static string? Read(Func<string?> read)
    {
        try { return read(); } catch { return null; }
    }

    private static GpuInfo Unidentified(int? cudaMajorVersion = null) =>
        new(GpuVendor.Nvidia, UnidentifiedGpuName, CudaMajorVersion: cudaMajorVersion);
}
