using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.Inference;

/// <summary>What the CUDA driver could tell us about NVIDIA hardware presence.</summary>
internal enum CudaDriverAvailability
{
    /// <summary>No NVIDIA CUDA driver is installed, so there is no NVIDIA GPU.</summary>
    Absent = 0,

    /// <summary>The driver is installed and reports no CUDA device.</summary>
    NoDevice = 1,

    /// <summary>The driver initialized and devices can be enumerated.</summary>
    Ready = 2,

    /// <summary>
    /// The driver is installed but failed to initialize, for example on a
    /// driver/runtime mismatch. Hardware presence is unknown, so this must stay
    /// retryable rather than becoming a definitive no-GPU verdict.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// The single trusted entry point to the NVIDIA CUDA driver. Every import loads
/// <c>nvcuda.dll</c> from System32 only, so a hostile DLL beside the application
/// can never impersonate the driver. Native reads are wrapped so a missing or
/// renamed entry point degrades to "unknown" instead of tearing down the caller.
/// </summary>
internal static class NvcudaDriver
{
    internal const int CudaSuccess = 0;
    internal const int CudaErrorNoDevice = 100;
    internal const int CudaErrorUnknown = 999;
    internal const int CudaUuidSize = 16;

    private const int DeviceNameCapacity = 256;
    private const int LuidSize = 8;
    private const uint SingleNodeMask = 1;

    /// <summary>
    /// Classifies CUDA driver availability. Only <see cref="CudaDriverAvailability.Absent"/>
    /// and <see cref="CudaDriverAvailability.NoDevice"/> are trustworthy evidence
    /// that this machine has no CUDA-capable NVIDIA GPU.
    /// </summary>
    internal static CudaDriverAvailability TryInitialize()
    {
        if (!OperatingSystem.IsWindows())
            return CudaDriverAvailability.Absent;

        int status;
        try
        {
            status = CuInit(0);
        }
        catch (Exception ex) when (IsMissingEntryPoint(ex))
        {
            return CudaDriverAvailability.Absent;
        }

        return status switch
        {
            CudaSuccess => CudaDriverAvailability.Ready,
            CudaErrorNoDevice => CudaDriverAvailability.NoDevice,
            _ => CudaDriverAvailability.Failed,
        };
    }

    internal static int? TryReadDeviceCount() =>
        TryNative(() => CuDeviceGetCount(out int count) == CudaSuccess ? count : (int?)null, null);

    internal static int? TryReadDeviceHandle(int ordinal) =>
        TryNative(() => CuDeviceGet(out int device, ordinal) == CudaSuccess ? device : (int?)null, null);

    internal static int? TryReadCudaMajorVersion() =>
        TryNative(
            () => CuDriverGetVersion(out int driverVersion) == CudaSuccess && driverVersion > 0
                ? driverVersion / 1000
                : (int?)null,
            null);

    internal static string? TryReadDeviceName(int device) =>
        TryNative(
            () =>
            {
                var buffer = new byte[DeviceNameCapacity];
                return CuDeviceGetName(buffer, buffer.Length, device) == CudaSuccess ? DecodeUtf8(buffer) : null;
            },
            null);

    internal static string? TryReadDeviceUuid(int device) =>
        TryNative(
            () =>
            {
                if (CuDeviceGetUuid(out CudaUuid uuid, device) != CudaSuccess)
                    return null;

                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(ref uuid, 1));
                return ToCudaVisibleDevicesSelector(bytes);
            },
            null);

    /// <summary>
    /// The adapter LUID the Windows display stack uses for this CUDA device.
    /// It is the exact join key to the DXGI adapter that owns the device memory.
    /// A device that spans more than one node returns null, because adapter-wide
    /// capacity would overstate what a single node can serve.
    /// </summary>
    internal static long? TryReadDeviceLuid(int device) =>
        TryNative(
            () =>
            {
                var luid = new byte[LuidSize];
                return CuDeviceGetLuid(luid, out uint deviceNodeMask, device) == CudaSuccess &&
                        deviceNodeMask == SingleNodeMask
                    ? BitConverter.ToInt64(luid)
                    : (long?)null;
            },
            null);

    /// <summary>
    /// Runs <paramref name="action"/> inside a CUDA context for the device,
    /// returning <paramref name="fallback"/> when the context cannot be created.
    /// </summary>
    internal static T WithContext<T>(int device, Func<T> action, T fallback)
    {
        IntPtr context;
        try
        {
            if (CuCtxCreate(out context, 0, device) != CudaSuccess)
                return fallback;
        }
        catch (Exception ex) when (IsMissingEntryPoint(ex))
        {
            return fallback;
        }

        try
        {
            return action();
        }
        finally
        {
            _ = TryNative(() => CuCtxDestroy(context), CudaErrorUnknown);
        }
    }

    internal static (long FreeBytes, long TotalBytes)? TryReadMemoryInfo() =>
        TryNative(
            () =>
            {
                if (CuMemGetInfo(out nuint freeBytes, out nuint totalBytes) != CudaSuccess ||
                    totalBytes == 0 || totalBytes > long.MaxValue || freeBytes > totalBytes)
                {
                    return null;
                }

                return ((long FreeBytes, long TotalBytes)?)((long)freeBytes, (long)totalBytes);
            },
            null);

    internal static string ToCudaVisibleDevicesSelector(ReadOnlySpan<byte> uuid)
    {
        if (uuid.Length != CudaUuidSize)
            throw new ArgumentException($"A CUDA GPU UUID must contain {CudaUuidSize} bytes.", nameof(uuid));

        string hex = Convert.ToHexString(uuid).ToLowerInvariant();
        return $"GPU-{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static T TryNative<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (IsMissingEntryPoint(ex))
        {
            return fallback;
        }
    }

    private static bool IsMissingEntryPoint(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static string? DecodeUtf8(byte[] buffer)
    {
        int terminator = Array.IndexOf(buffer, (byte)0);
        string value = Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CudaUuid
    {
        public ulong FirstBytes;
        public ulong LastBytes;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuInit", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuInit(uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDriverGetVersion", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDriverGetVersion(out int driverVersion);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetCount", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetCount(out int count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGet", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGet(out int device, int ordinal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetName", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetName([Out] byte[] name, int length, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetUuid_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetUuid(out CudaUuid uuid, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGetLuid", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuDeviceGetLuid([Out] byte[] luid, out uint deviceNodeMask, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxCreate_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuCtxCreate(out IntPtr context, uint flags, int device);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxDestroy_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuCtxDestroy(IntPtr context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvcuda.dll", EntryPoint = "cuMemGetInfo_v2", CallingConvention = CallingConvention.StdCall)]
    private static extern int CuMemGetInfo(out nuint freeBytes, out nuint totalBytes);
}
