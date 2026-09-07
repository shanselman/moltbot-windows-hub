using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// Reads dedicated device memory per NVIDIA GPU UUID, for hosts where no DXGI
/// adapter can supply the bound.
/// </summary>
internal interface INvmlDedicatedMemoryProbe
{
    IReadOnlyDictionary<string, GpuAdapterMemory> CaptureAdapterMemoryByUuid();
}

/// <summary>
/// Reads NVML's device memory totals, keyed by the same <c>GPU-...</c> UUID the
/// CUDA driver reports, so the join is an exact device identity match.
/// </summary>
/// <remarks>
/// NVML reports dedicated device memory only, never the WDDM shared host pool,
/// which makes it a safe admission bound. It covers supported CUDA hosts that
/// DXGI cannot describe, including TCC devices and headless GPUs. On the DGX
/// Spark recorded in <c>#1237</c> NVML reported 16,320 MiB under Windows while
/// CUDA advertised 46,332 MiB, so this source predicted the allocation that
/// actually failed there.
/// <para>
/// This is a bound source, not a guarantee: a device whose driver returns
/// <c>NOT_SUPPORTED</c> for memory info contributes nothing and stays retryable
/// rather than over-qualified. MIG compute instances are out of scope, because
/// this enumerates physical devices only.
/// </para>
/// </remarks>
internal sealed class NvmlDedicatedMemoryProbe : INvmlDedicatedMemoryProbe
{
    public IReadOnlyDictionary<string, GpuAdapterMemory> CaptureAdapterMemoryByUuid()
    {
        var memoryByUuid = new Dictionary<string, GpuAdapterMemory>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || !TryLoadNvml(out IntPtr library))
            return memoryByUuid;

        bool initialized = false;
        NvmlShutdown? shutdown = null;
        try
        {
            var initialize = GetDelegate<NvmlInitialize>(library, "nvmlInit_v2");
            shutdown = GetDelegate<NvmlShutdown>(library, "nvmlShutdown");
            var getCount = GetDelegate<NvmlDeviceGetCount>(library, "nvmlDeviceGetCount_v2");
            var getHandle = GetDelegate<NvmlDeviceGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            var getUuid = GetDelegate<NvmlDeviceGetString>(library, "nvmlDeviceGetUUID");
            var getMemory = GetDelegate<NvmlDeviceGetMemoryInfo>(library, "nvmlDeviceGetMemoryInfo");

            if (initialize() != NvmlSuccess)
                return memoryByUuid;

            initialized = true;
            if (getCount(out uint count) != NvmlSuccess)
                return memoryByUuid;

            for (uint index = 0; index < count; index++)
            {
                if (getHandle(index, out IntPtr device) != NvmlSuccess ||
                    getMemory(device, out NvmlMemory memory) != NvmlSuccess ||
                    memory.Total == 0 || memory.Total > long.MaxValue || memory.Free > memory.Total ||
                    ReadDeviceString(device, getUuid, DeviceUuidCapacity) is not { } uuid)
                {
                    continue;
                }

                memoryByUuid[uuid] = new GpuAdapterMemory((long)memory.Total, (long)memory.Free);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException or
            DllNotFoundException or MarshalDirectiveException or SEHException)
        {
            // A driver without the expected NVML exports simply supplies no bound.
        }
        finally
        {
            try
            {
                if (initialized && shutdown is not null)
                    _ = shutdown();
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or SEHException)
            {
                // Freeing the loader reference still has to happen.
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }

        return memoryByUuid;
    }

    /// <summary>
    /// Only fully qualified driver-owned locations are considered, so a
    /// <c>nvml.dll</c> dropped beside the application can never be loaded.
    /// </summary>
    internal static IReadOnlyList<string> GetNvmlLibraryCandidates()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll"),
        ];

        return candidates
            .Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryLoadNvml(out IntPtr library)
    {
        foreach (string candidate in GetNvmlLibraryCandidates())
        {
            try
            {
                if (NativeLibrary.TryLoad(candidate, out library))
                    return true;
            }
            catch (BadImageFormatException)
            {
                // Try the next explicit driver-owned candidate.
            }
        }

        library = IntPtr.Zero;
        return false;
    }

    private static T GetDelegate<T>(IntPtr library, string exportName)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, exportName));

    private static string? ReadDeviceString(IntPtr device, NvmlDeviceGetString getter, uint capacity)
    {
        var buffer = new byte[capacity];
        if (getter(device, buffer, capacity) != NvmlSuccess)
            return null;

        int terminator = Array.IndexOf(buffer, (byte)0);
        string value = Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private const int NvmlSuccess = 0;
    private const uint DeviceUuidCapacity = 96;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCount(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetString(IntPtr device, [Out] byte[] value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
}
