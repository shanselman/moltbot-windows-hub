using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// What the Windows display stack reports about the memory that actually backs
/// device allocations on one adapter.
/// </summary>
/// <param name="DedicatedVideoMemoryBytes">Memory physically dedicated to the adapter.</param>
/// <param name="AvailableLocalBytes">
/// How much of the adapter's local segment this process may still use, or null
/// when the budget could not be read.
/// </param>
internal sealed record GpuAdapterMemory(long DedicatedVideoMemoryBytes, long? AvailableLocalBytes);

/// <summary>
/// Reads how much memory is physically dedicated to each NVIDIA adapter, keyed
/// by the Windows adapter LUID.
/// </summary>
internal interface IGpuDedicatedMemoryProbe
{
    IReadOnlyDictionary<long, GpuAdapterMemory> CaptureAdapterMemoryByLuid();
}

/// <summary>
/// Reads <c>DXGI_ADAPTER_DESC1.DedicatedVideoMemory</c>, the Windows display
/// stack's own statement of how much memory actually backs device allocations on
/// an adapter.
/// </summary>
/// <remarks>
/// This is the conservative WDDM admission bound Local AI qualification needs.
/// <c>cuMemGetInfo</c> is not safe on its own: on a DGX Spark it advertised
/// roughly 46 GiB because CUDA surfaces the WDDM shared host pool, while
/// llama-server still died allocating about 15.81 GiB against roughly 15.9 GiB
/// of real device memory. Shared system memory is host RAM, so it never backs a
/// device allocation and must never raise the number eligibility is judged
/// against. Only the dedicated figure is read here, and it is used solely to cap
/// the CUDA-reported total, never to increase it.
/// </remarks>
internal sealed class DxgiDedicatedMemoryProbe : IGpuDedicatedMemoryProbe
{
    private const uint NvidiaVendorId = 0x10DE;
    private const int QueryInterfaceSlot = 0;
    private const int ReleaseSlot = 2;
    private const int GetDesc1Slot = 10;
    private const int EnumAdapters1Slot = 12;
    private const int QueryVideoMemoryInfoSlot = 14;

    public IReadOnlyDictionary<long, GpuAdapterMemory> CaptureAdapterMemoryByLuid()
    {
        var memoryByLuid = new Dictionary<long, GpuAdapterMemory>();
        if (!OperatingSystem.IsWindows())
            return memoryByLuid;

        IntPtr factory;
        try
        {
            Guid factoryId = IidDxgiFactory1;
            if (CreateDXGIFactory1(ref factoryId, out factory) < 0 || factory == IntPtr.Zero)
                return memoryByLuid;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return memoryByLuid;
        }

        try
        {
            var enumerateAdapters = GetDelegate<EnumAdapters1>(factory, EnumAdapters1Slot);
            for (uint index = 0; ; index++)
            {
                if (enumerateAdapters(factory, index, out IntPtr adapter) < 0 || adapter == IntPtr.Zero)
                    break;

                try
                {
                    AddAdapter(adapter, memoryByLuid);
                }
                finally
                {
                    _ = GetDelegate<Release>(adapter, ReleaseSlot)(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // A partial adapter walk still yields the adapters already read.
        }
        finally
        {
            _ = GetDelegate<Release>(factory, ReleaseSlot)(factory);
        }

        return memoryByLuid;
    }

    private static void AddAdapter(IntPtr adapter, Dictionary<long, GpuAdapterMemory> memoryByLuid)
    {
        var getDescription = GetDelegate<GetDesc1>(adapter, GetDesc1Slot);
        if (getDescription(adapter, out DxgiAdapterDescription description) < 0 ||
            description.VendorId != NvidiaVendorId ||
            description.DedicatedVideoMemory == 0 ||
            description.DedicatedVideoMemory > long.MaxValue)
        {
            return;
        }

        long luid = ToLuid(description.AdapterLuidLowPart, description.AdapterLuidHighPart);
        memoryByLuid[luid] = new GpuAdapterMemory(
            (long)description.DedicatedVideoMemory,
            QueryAvailableLocalMemory(adapter));
    }

    /// <summary>
    /// How much of the adapter's local segment this process may still use,
    /// straight from the WDDM budget. This is the trustworthy free-memory figure
    /// when CUDA's own free value also counts the shared host pool.
    /// </summary>
    private static long? QueryAvailableLocalMemory(IntPtr adapter)
    {
        var queryInterface = GetDelegate<QueryInterface>(adapter, QueryInterfaceSlot);
        Guid adapter3Id = IidDxgiAdapter3;
        if (queryInterface(adapter, ref adapter3Id, out IntPtr adapter3) < 0 || adapter3 == IntPtr.Zero)
            return null;

        try
        {
            var queryMemory = GetDelegate<QueryVideoMemoryInfo>(adapter3, QueryVideoMemoryInfoSlot);
            if (queryMemory(adapter3, 0, DxgiMemorySegmentGroup.Local, out DxgiVideoMemoryInfo memory) < 0 ||
                memory.Budget == 0 ||
                memory.Budget < memory.CurrentUsage)
            {
                return null;
            }

            ulong available = memory.Budget - memory.CurrentUsage;
            return available <= long.MaxValue ? (long)available : null;
        }
        finally
        {
            _ = GetDelegate<Release>(adapter3, ReleaseSlot)(adapter3);
        }
    }

    internal static long ToLuid(uint lowPart, int highPart) =>
        (long)(((ulong)(uint)highPart << 32) | lowPart);

    private static T GetDelegate<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    private static readonly Guid IidDxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid IidDxgiAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

    private enum DxgiMemorySegmentGroup
    {
        Local = 0,
        NonLocal = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(IntPtr instance, ref Guid interfaceId, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfo(
        IntPtr instance,
        uint nodeIndex,
        DxgiMemorySegmentGroup segmentGroup,
        out DxgiVideoMemoryInfo memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr instance, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr instance, out DxgiAdapterDescription description);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
