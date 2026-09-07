using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// GPU vendor, as far as the probe could determine it. <see cref="Unknown"/>
/// means "we saw an adapter but could not classify it" and must be treated the
/// same as "no usable accelerator" by the backend selector.
/// </summary>
public enum GpuVendor
{
    Unknown = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
    Other = 4,
}

/// <summary>
/// One detected graphics adapter.
/// </summary>
/// <param name="Vendor">Classified vendor.</param>
/// <param name="Name">Adapter name as reported by the source (e.g. "NVIDIA RTX 6000 Ada Generation").</param>
/// <param name="GpuVisibleMemoryBytes">
/// CUDA-visible memory in bytes, or null when unknown. On a discrete GPU this
/// is dedicated VRAM. On a unified-memory SKU it is the configured GPU-visible
/// allocation. The value must come from a trustworthy driver API, never the
/// 32-bit <c>Win32_VideoController.AdapterRAM</c> field.
/// </param>
/// <param name="FreeGpuVisibleMemoryBytes">Currently free CUDA-visible memory, or null when unknown.</param>
/// <param name="SharedGpuMemoryBytes">
/// Legacy shared-memory field. CUDA-only probing leaves this null.
/// This is not general available system RAM.
/// </param>
/// <param name="FreeSharedGpuMemoryBytes">
/// Legacy shared-memory field. CUDA-only probing leaves this null.
/// </param>
/// <param name="DriverVersion">Display driver version, when known.</param>
/// <param name="CudaMajorVersion">
/// Major version of the CUDA driver API the display driver supports, when known.
/// </param>
/// <param name="StableId">A CUDA driver-provided stable adapter identifier.</param>
public sealed record GpuInfo(
    GpuVendor Vendor,
    string Name,
    long? GpuVisibleMemoryBytes = null,
    long? FreeGpuVisibleMemoryBytes = null,
    long? SharedGpuMemoryBytes = null,
    long? FreeSharedGpuMemoryBytes = null,
    string? DriverVersion = null,
    int? CudaMajorVersion = null,
    string? StableId = null);

/// <summary>
/// Snapshot of the host's inference-relevant hardware. Every probed field is
/// optional. Unknown values remain unknown so a qualified selector can fail
/// closed instead of guessing a backend or recipe.
/// </summary>
/// <param name="CpuArchitecture">OS architecture (x64 / Arm64 in practice).</param>
/// <param name="TotalPhysicalMemoryBytes">Installed system RAM, or null when the query failed.</param>
/// <param name="AvailablePhysicalMemoryBytes">Currently free system RAM, or null when the query failed.</param>
/// <param name="Gpus">All detected adapters, in the order the source reported them.</param>
/// <param name="VulkanAvailable">True when a Vulkan loader is present on the machine.</param>
public sealed record HostHardwareInfo(
    Architecture CpuArchitecture,
    long? TotalPhysicalMemoryBytes,
    long? AvailablePhysicalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus,
    bool VulkanAvailable)
{
    /// <summary>
    /// The "we learned nothing" result. Qualified selectors must treat it as
    /// unsupported rather than selecting a fallback backend.
    /// </summary>
    public static HostHardwareInfo Unknown { get; } = new(
        RuntimeInformation.OSArchitecture,
        null,
        null,
        Array.Empty<GpuInfo>(),
        false);

    /// <summary>All adapters classified as NVIDIA.</summary>
    public IEnumerable<GpuInfo> NvidiaGpus => Gpus.Where(g => g.Vendor == GpuVendor.Nvidia);

    /// <summary>True when at least one NVIDIA adapter was detected.</summary>
    public bool HasNvidiaGpu => Gpus.Any(g => g.Vendor == GpuVendor.Nvidia);

}
