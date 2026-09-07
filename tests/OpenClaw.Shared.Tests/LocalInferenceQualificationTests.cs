using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using System.Reflection;
using System.Runtime.InteropServices;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class LocalInferenceQualificationTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    [Fact]
    public void CudaVisibleDevicesSelector_FormatsDriverUuidLikeNvml()
    {
        byte[] cudaUuid = Convert.FromHexString("CC66BCA6B5FFDD70995CD81A07ADD980");

        Assert.Equal(
            "GPU-cc66bca6-b5ff-dd70-995c-d81a07add980",
            NvcudaDriver.ToCudaVisibleDevicesSelector(cudaUuid));
    }

    [Fact]
    public void CudaProbe_LoadsNativeDriverOnlyFromSystem32()
    {
        MethodInfo[] imports = typeof(NvcudaDriver).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is { } import &&
                import.Value.Contains("nvcuda", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(imports);
        Assert.All(imports, method =>
        {
            DefaultDllImportSearchPathsAttribute attribute = Assert.IsType<DefaultDllImportSearchPathsAttribute>(
                method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>());
            Assert.Equal(DllImportSearchPath.System32, attribute.Paths);
        });
    }

    [Fact]
    public void CudaProbe_MissingUuidKeepsDetectedNvidiaGpuAsIncompleteFacts()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Uuid = null };

        HostHardwareInfo hardware = Probe(reader);

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.StableId);
        Assert.Equal(32 * GiB, gpu.GpuVisibleMemoryBytes);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.None, result.SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_FailingUuidEntryPointKeepsDetectedNvidiaGpuAsIncompleteFacts()
    {
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 1,
            UuidFailure = () => new EntryPointNotFoundException("cuDeviceGetUuid_v2"),
        };

        HostHardwareInfo hardware = Probe(reader);

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.StableId);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.NotEqual(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_KeepsHealthyGpuWhenAnotherDeviceFailsItsUuidLookup()
    {
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 2,
            UuidByDevice = device => device == 0 ? null : "GPU-healthy",
        };

        HostHardwareInfo hardware = Probe(reader);

        Assert.Equal(2, hardware.Gpus.Count);
        Assert.Null(hardware.Gpus[0].StableId);
        Assert.Equal("GPU-healthy", hardware.Gpus[1].StableId);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-healthy", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void CudaProbe_FailedDeviceCountKeepsRetryableNvidiaFactsInsteadOfNoGpu()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = null };

        HostHardwareInfo hardware = Probe(reader);

        Assert.True(hardware.HasNvidiaGpu);
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void CudaProbe_FailedDeviceHandleKeepsRetryableNvidiaFacts()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, DeviceHandle = null };

        HostHardwareInfo hardware = Probe(reader);

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.StableId);
        Assert.Equal(
            LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
            LocalInferenceEligibility.Evaluate(hardware).FailureCode);
    }

    [Fact]
    public void CudaProbe_MissingNameStillReportsTheDetectedNvidiaDevice()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Name = null };

        GpuInfo gpu = Assert.Single(Probe(reader).Gpus);

        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.False(string.IsNullOrWhiteSpace(gpu.Name));
        Assert.Equal("GPU-stub", gpu.StableId);
    }

    [Fact]
    public void CudaProbe_AbsentDriverIsDefinitiveNoNvidiaGpu() =>
        AssertDefinitiveNoNvidiaGpu(CudaDriverAvailability.Absent);

    [Fact]
    public void CudaProbe_DriverReportingNoDeviceIsDefinitiveNoNvidiaGpu() =>
        AssertDefinitiveNoNvidiaGpu(CudaDriverAvailability.NoDevice);

    private static void AssertDefinitiveNoNvidiaGpu(CudaDriverAvailability availability)
    {
        var reader = new StubCudaDeviceReader { Availability = availability };

        HostHardwareInfo hardware = Probe(reader);

        Assert.False(hardware.HasNvidiaGpu);
        Assert.Equal(
            LocalInferenceSelectionFailureCode.NoNvidiaGpu,
            LocalInferenceEligibility.Evaluate(hardware).SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_DriverInitializationFailureStaysRetryableRatherThanNoGpu()
    {
        // A driver/runtime mismatch fails cuInit while NVIDIA hardware is still
        // present, so presence is unknown rather than disproven.
        var reader = new StubCudaDeviceReader { Availability = CudaDriverAvailability.Failed };

        HostHardwareInfo hardware = Probe(reader);

        Assert.True(hardware.HasNvidiaGpu);
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.NotEqual(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_ZeroReportedDevicesIsAlsoDefinitiveNoNvidiaGpu()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 0 };

        Assert.Equal(
            LocalInferenceSelectionFailureCode.NoNvidiaGpu,
            LocalInferenceEligibility.Evaluate(Probe(reader)).SelectionFailureCode);
    }

    [Fact]
    public void CudaProbe_CapsReportedCapacityAtDedicatedDeviceMemory()
    {
        // The recorded DGX Spark case: CUDA advertises about 46 GiB because it
        // surfaces the WDDM shared host pool, while only about 15.9 GiB of real
        // device memory backs llama-server allocations.
        GpuInfo gpu = Assert.Single(Probe(DgxSparkReader(), DgxSparkAdapter(12_000L * MiB)).Gpus);

        Assert.Equal(16_320L * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(12_000L * MiB, gpu.FreeGpuVisibleMemoryBytes);
        Assert.Null(gpu.SharedGpuMemoryBytes);
    }

    [Fact]
    public void Evaluate_DoesNotQualifyAnyModelOnTheRecordedDgxSparkCapacity()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Probe(DgxSparkReader(), DgxSparkAdapter(12_000L * MiB)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
    }

    [Fact]
    public void CudaProbe_PrefersTheAdapterBudgetOverCudaFreeMemory()
    {
        // CUDA reports 46,114 MiB free while the adapter's local budget says only
        // 4,000 MiB remains. Trusting the CUDA figure would claim the whole
        // dedicated segment is free and launch straight into an out-of-memory.
        GpuInfo gpu = Assert.Single(Probe(DgxSparkReader(), DgxSparkAdapter(4_000L * MiB)).Gpus);

        Assert.Equal(4_000L * MiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_FallsBackToCudaFreeMemoryWhenNoAdapterBudgetIsAvailable()
    {
        HostHardwareInfo hardware = Probe(DgxSparkReader(), DgxSparkAdapter(availableLocalBytes: null));

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(16_320L * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(16_320L * MiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_KeepsCapacityWhenCudaTotalSlightlyExceedsTheDedicatedBound()
    {
        // A discrete adapter normally reports a slightly larger CUDA total than
        // DXGI dedicated. That small gap must not blank out capacity.
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Memory = (15_061L * MiB, 16_375L * MiB) };
        var adapter = new StubDedicatedMemoryProbe(
            StubCudaDeviceReader.StubLuid,
            new GpuAdapterMemory(16_045L * MiB, 15_000L * MiB));

        GpuInfo gpu = Assert.Single(Probe(reader, adapter).Gpus);

        Assert.Equal(16_045L * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(15_000L * MiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_NeverRaisesCapacityAboveTheCudaReportedTotal()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Memory = (8 * GiB, 12 * GiB) };
        var adapter = new StubDedicatedMemoryProbe(
            StubCudaDeviceReader.StubLuid,
            new GpuAdapterMemory(24 * GiB, 8 * GiB));

        GpuInfo gpu = Assert.Single(Probe(reader, adapter).Gpus);

        Assert.Equal(12 * GiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(8 * GiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_LeavesCapacityUnknownWhenNoDedicatedBoundIsAvailable()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Memory = (46 * GiB, 46 * GiB) };

        HostHardwareInfo hardware = Probe(reader, new StubDedicatedMemoryProbe(), new StubNvmlMemoryProbe());

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Null(gpu.GpuVisibleMemoryBytes);
        Assert.Equal(
            LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete,
            LocalInferenceEligibility.Evaluate(hardware).FailureCode);
    }

    [Fact]
    public void CudaProbe_FallsBackToNvmlWhenNoDxgiAdapterDescribesTheDevice()
    {
        // The DGX Spark shape: CUDA advertises about 46 GiB, DXGI supplies no
        // usable adapter, and NVML reports the 16,320 MiB that actually backs
        // device allocations.
        HostHardwareInfo hardware = Probe(
            DgxSparkReader(),
            new StubDedicatedMemoryProbe(),
            NvmlSpark(15_000L * MiB));

        GpuInfo gpu = Assert.Single(hardware.Gpus);
        Assert.Equal(16_320L * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(15_000L * MiB, gpu.FreeGpuVisibleMemoryBytes);
        Assert.Equal(
            LocalInferenceEligibilityFailureCode.InsufficientGpuMemory,
            LocalInferenceEligibility.Evaluate(hardware).FailureCode);
    }

    [Fact]
    public void CudaProbe_FallsBackToNvmlWhenTheAdapterLuidCannotBeRead()
    {
        // A TCC or headless CUDA device has no DXGI adapter LUID at all, and
        // must stay supported rather than becoming permanently incomplete.
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 1,
            Luid = null,
            Memory = (30 * GiB, 48 * GiB),
        };
        var nvml = new StubNvmlMemoryProbe("GPU-stub", new GpuAdapterMemory(48 * GiB, 30 * GiB));

        GpuInfo gpu = Assert.Single(Probe(reader, new StubDedicatedMemoryProbe(), nvml).Gpus);

        Assert.Equal(48 * GiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(30 * GiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_FallsBackToNvmlWhenTheAdapterReportsZeroDedicatedMemory()
    {
        // A true UMA adapter can report no dedicated video memory through DXGI.
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Memory = (40 * GiB, 46 * GiB) };
        var dxgi = new StubDedicatedMemoryProbe(
            StubCudaDeviceReader.StubLuid,
            new GpuAdapterMemory(DedicatedVideoMemoryBytes: 0, AvailableLocalBytes: 0));
        var nvml = new StubNvmlMemoryProbe("GPU-stub", new GpuAdapterMemory(20 * GiB, 18 * GiB));

        GpuInfo gpu = Assert.Single(Probe(reader, dxgi, nvml).Gpus);

        Assert.Equal(20 * GiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(18 * GiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_PrefersTheDxgiBoundOverNvmlWhenBothDescribeTheDevice()
    {
        // Two sources answer different questions, so the conservative value wins
        // and the decision cannot depend on which source resolved.
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Memory = (15_061L * MiB, 16_375L * MiB) };
        var dxgi = new StubDedicatedMemoryProbe(
            StubCudaDeviceReader.StubLuid,
            new GpuAdapterMemory(16_045L * MiB, 15_277L * MiB));
        var nvml = new StubNvmlMemoryProbe("GPU-stub", new GpuAdapterMemory(16_376L * MiB, 8_889L * MiB));

        GpuInfo gpu = Assert.Single(Probe(reader, dxgi, nvml).Gpus);

        Assert.Equal(16_045L * MiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(8_889L * MiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_TakesTheSmallerDedicatedBoundWhenSourcesDisagree()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1, Memory = (40 * GiB, 46 * GiB) };
        var dxgi = new StubDedicatedMemoryProbe(
            StubCudaDeviceReader.StubLuid,
            new GpuAdapterMemory(24 * GiB, 20 * GiB));
        var nvml = new StubNvmlMemoryProbe("GPU-stub", new GpuAdapterMemory(16 * GiB, 14 * GiB));

        GpuInfo gpu = Assert.Single(Probe(reader, dxgi, nvml).Gpus);

        Assert.Equal(16 * GiB, gpu.GpuVisibleMemoryBytes);
        Assert.Equal(14 * GiB, gpu.FreeGpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_JoinsTheNvmlBoundCaseInsensitivelyAcrossMultipleDevices()
    {
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 2,
            UuidByDevice = device => device == 0 ? "GPU-AAAA" : "GPU-BBBB",
            Memory = (40 * GiB, 46 * GiB),
            Luid = null,
        };
        var nvml = new StubNvmlMemoryProbe(
            ("gpu-bbbb", new GpuAdapterMemory(20 * GiB, 18 * GiB)),
            ("gpu-aaaa", new GpuAdapterMemory(12 * GiB, 10 * GiB)));

        HostHardwareInfo hardware = Probe(reader, new StubDedicatedMemoryProbe(), nvml);

        Assert.Equal(12 * GiB, hardware.Gpus[0].GpuVisibleMemoryBytes);
        Assert.Equal(20 * GiB, hardware.Gpus[1].GpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_NvmlBoundIsJoinedByDeviceIdentityNotOrdinal()
    {
        var reader = new StubCudaDeviceReader
        {
            DeviceCount = 1,
            Uuid = "GPU-actual",
            Memory = (40 * GiB, 46 * GiB),
        };
        var nvml = new StubNvmlMemoryProbe("GPU-different", new GpuAdapterMemory(20 * GiB, 18 * GiB));

        GpuInfo gpu = Assert.Single(Probe(reader, new StubDedicatedMemoryProbe(), nvml).Gpus);

        Assert.Null(gpu.GpuVisibleMemoryBytes);
    }

    [Fact]
    public void CudaProbe_LeavesCapacityUnknownWhenTheNvmlProbeAlsoThrows()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1 };

        GpuInfo gpu = Assert.Single(
            Probe(reader, new ThrowingDedicatedMemoryProbe(), new ThrowingNvmlMemoryProbe()).Gpus);

        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.GpuVisibleMemoryBytes);
    }

    [Fact]
    public void NvmlProbe_LoadsOnlyFullyQualifiedDriverOwnedLibraries()
    {
        string[] allowedRoots =
        [
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll"),
        ];

        IReadOnlyList<string> candidates = NvmlDedicatedMemoryProbe.GetNvmlLibraryCandidates();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.True(Path.IsPathFullyQualified(candidate));
            Assert.Contains(candidate, allowedRoots, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void CudaProbe_LeavesCapacityUnknownWhenTheDedicatedMemoryProbeThrows()
    {
        var reader = new StubCudaDeviceReader { DeviceCount = 1 };

        GpuInfo gpu = Assert.Single(Probe(reader, new ThrowingDedicatedMemoryProbe()).Gpus);

        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.Null(gpu.GpuVisibleMemoryBytes);
    }

    [Fact]
    public void Evaluate_InconclusiveGpuIsNotHiddenByADefinitivelyUnsupportedGpu()
    {
        // The unreadable device could still be supported, so reporting the other
        // adapter's definitive verdict would disable recheck on this machine.
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            new GpuInfo(GpuVendor.Nvidia, "NVIDIA unreadable adapter", CudaMajorVersion: 13),
            Gpu("NVIDIA small adapter", "GPU-small", 16, 16));

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
    }

    [Fact]
    public void Evaluate_EligibleGpuStillWinsOverAnInconclusiveGpu()
    {
        HostHardwareInfo hardware = Hardware(
            RuntimeArchitecture.X64,
            new GpuInfo(GpuVendor.Nvidia, "NVIDIA unreadable adapter", CudaMajorVersion: 13),
            Gpu("NVIDIA capable adapter", "GPU-capable", 48, 48));

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-capable", result.SelectedGpu?.StableId);
    }

    [Theory]
    [InlineData(0x00018980u, 0, "8089010000000000")]
    [InlineData(0x00018980u, 0x0000007F, "808901007F000000")]
    [InlineData(0xFFFFFFFFu, -1, "FFFFFFFFFFFFFFFF")]
    [InlineData(0x00000000u, int.MinValue, "0000000000000080")]
    public void DxgiLuid_MatchesTheSignedCudaAdapterLuidEncoding(
        uint lowPart,
        int highPart,
        string cudaLuidHex)
    {
        Assert.Equal(
            BitConverter.ToInt64(Convert.FromHexString(cudaLuidHex)),
            DxgiDedicatedMemoryProbe.ToLuid(lowPart, highPart));
    }

    private static StubCudaDeviceReader DgxSparkReader() =>
        new() { DeviceCount = 1, Memory = (46_114L * MiB, 46_332L * MiB) };

    private static StubDedicatedMemoryProbe DgxSparkAdapter(long? availableLocalBytes) =>
        new(StubCudaDeviceReader.StubLuid, new GpuAdapterMemory(16_320L * MiB, availableLocalBytes));

    private static StubNvmlMemoryProbe NvmlSpark(long? freeBytes) =>
        new("GPU-stub", new GpuAdapterMemory(16_320L * MiB, freeBytes));

    private static HostHardwareInfo Probe(
        ICudaDeviceReader reader,
        IGpuDedicatedMemoryProbe? dedicatedMemoryProbe = null,
        INvmlDedicatedMemoryProbe? nvmlMemoryProbe = null) =>
        new CudaHostHardwareProbe(
            reader,
            dedicatedMemoryProbe ?? new StubDedicatedMemoryProbe(
                StubCudaDeviceReader.StubLuid,
                new GpuAdapterMemory(32 * GiB, 32 * GiB)),
            nvmlMemoryProbe ?? new StubNvmlMemoryProbe())
            .Probe();

    private sealed class StubCudaDeviceReader : ICudaDeviceReader
    {
        internal const long StubLuid = 0x18980;

        public CudaDriverAvailability Availability { get; init; } = CudaDriverAvailability.Ready;
        public int? DeviceCount { get; init; } = 1;
        public int? DeviceHandle { get; init; } = 0;
        public string? Name { get; init; } = "NVIDIA GeForce RTX 5090";
        public string? Uuid { get; init; } = "GPU-stub";
        public long? Luid { get; init; } = StubLuid;
        public (long FreeBytes, long TotalBytes)? Memory { get; init; } = (32 * GiB, 32 * GiB);
        public Func<int, string?>? UuidByDevice { get; init; }
        public Func<Exception>? UuidFailure { get; init; }

        public CudaDriverAvailability TryInitialize() => Availability;

        public int? TryReadDeviceCount() => DeviceCount;

        public int? TryReadCudaMajorVersion() => 13;

        public int? TryReadDeviceHandle(int ordinal) => DeviceHandle is null ? null : ordinal;

        public string? TryReadDeviceName(int device) => Name;

        public string? TryReadDeviceUuid(int device) =>
            UuidFailure is not null
                ? throw UuidFailure()
                : UuidByDevice is not null ? UuidByDevice(device) : Uuid;

        public long? TryReadDeviceLuid(int device) => Luid;

        public (long FreeBytes, long TotalBytes)? TryReadMemoryInfo(int device) => Memory;
    }

    private sealed class StubDedicatedMemoryProbe : IGpuDedicatedMemoryProbe
    {
        private readonly Dictionary<long, GpuAdapterMemory> _memoryByLuid = [];

        public StubDedicatedMemoryProbe()
        {
        }

        public StubDedicatedMemoryProbe(long luid, GpuAdapterMemory memory) =>
            _memoryByLuid[luid] = memory;

        public IReadOnlyDictionary<long, GpuAdapterMemory> CaptureAdapterMemoryByLuid() => _memoryByLuid;
    }

    private sealed class ThrowingDedicatedMemoryProbe : IGpuDedicatedMemoryProbe
    {
        public IReadOnlyDictionary<long, GpuAdapterMemory> CaptureAdapterMemoryByLuid() =>
            throw new InvalidOperationException("DXGI faulted.");
    }

    private sealed class StubNvmlMemoryProbe : INvmlDedicatedMemoryProbe
    {
        private readonly Dictionary<string, GpuAdapterMemory> _memoryByUuid =
            new(StringComparer.OrdinalIgnoreCase);

        public StubNvmlMemoryProbe()
        {
        }

        public StubNvmlMemoryProbe(string uuid, GpuAdapterMemory memory) =>
            _memoryByUuid[uuid] = memory;

        public StubNvmlMemoryProbe(params (string Uuid, GpuAdapterMemory Memory)[] entries)
        {
            foreach ((string uuid, GpuAdapterMemory memory) in entries)
                _memoryByUuid[uuid] = memory;
        }

        public IReadOnlyDictionary<string, GpuAdapterMemory> CaptureAdapterMemoryByUuid() => _memoryByUuid;
    }

    private sealed class ThrowingNvmlMemoryProbe : INvmlDedicatedMemoryProbe
    {
        public IReadOnlyDictionary<string, GpuAdapterMemory> CaptureAdapterMemoryByUuid() =>
            throw new InvalidOperationException("NVML faulted.");
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX Spark N1X", LlamaRuntimeCatalog.X64RuntimeId)]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA GeForce RTX 5090", LlamaRuntimeCatalog.Arm64RuntimeId)]
    public void Evaluate_RoutesRuntimeByArchitectureWithoutGpuSkuPairing(
        RuntimeArchitecture architecture,
        string gpuName,
        string expectedRuntimeId)
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(architecture, Gpu(gpuName, "GPU-generic", totalGiB: 32, freeGiB: 32)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(expectedRuntimeId, result.Plan?.Runtime.Id);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.IntermediateContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.Q8_0, result.Plan?.Profile.KeyCachePrecision);
    }

    [Fact]
    public void Evaluate_UnsetModelChoosesHighestPriorityModelThatFitsTotalCapacity()
    {
        var cases = new[]
        {
            (TotalBytes: 34_190_458_880L, FreeBytes: 32_432_455_680L,
                ModelId: LocalModelCatalog.Qwen38_27BModelId,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0,
                RequiredBytes: 31_253_556_128L),
            (TotalBytes: 24 * GiB, FreeBytes: 24 * GiB,
                ModelId: LocalModelCatalog.Qwen38_27BModelId,
                ContextTokens: LocalModelCatalog.MinimumContextTokens,
                Precision: KvCachePrecision.F16,
                RequiredBytes: 25_322_810_272L),
        };
        foreach (var testCase in cases)
        {
            GpuInfo gpu = Gpu("NVIDIA arbitrary adapter", "GPU-capacity", 1, 1) with
            {
                GpuVisibleMemoryBytes = testCase.TotalBytes,
                FreeGpuVisibleMemoryBytes = testCase.FreeBytes,
            };
            LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
                Hardware(RuntimeArchitecture.X64, gpu));

            Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
            Assert.Equal(testCase.ModelId, result.Plan?.Model.Id);
            Assert.Equal(testCase.ContextTokens, result.Plan?.Profile.ContextTokens);
            Assert.Equal(testCase.Precision, result.Plan?.Profile.KeyCachePrecision);
            Assert.Equal(testCase.RequiredBytes, result.RequiredTotalMemoryBytes);
            Assert.True(result.Plan?.Profile.ContextTokens >= LocalModelCatalog.MinimumContextTokens);
            Assert.Equal(LocalInferenceModelSelectionOrigin.Default, result.Plan?.ModelSelectionOrigin);
        }
    }

    [Fact]
    public void Evaluate_UnsetModelRejects16GiBCapacity()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.DoesNotContain(LocalModelCatalog.Models, model => model.Id == "qwen3.5-9b-mtp-q4-k-m");
    }

    [Fact]
    public void Evaluate_Removed16GiBModelIdIsUnknown()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-32", 32, 32)),
            "qwen3.5-9b-mtp-q4-k-m");

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.UnknownModel, result.SelectionFailureCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Evaluate_ExplicitModelNeverDowngradesAndReportsExactCapacity()
    {
        var cases = new[]
        {
            (ModelId: LocalModelCatalog.Qwen38_27BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 31_253_556_128L),
            (ModelId: LocalModelCatalog.Qwen35BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 32_532_584_736L),
            (ModelId: LocalModelCatalog.Qwen27BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 31_895_889_024L),
            (ModelId: LocalModelCatalog.Qwen35BModelId, TotalGiB: 16,
                Status: LocalInferenceEligibilityStatus.Unsupported,
                ContextTokens: LocalModelCatalog.MinimumContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 27_742_689_568L),
        };
        foreach (var testCase in cases)
        {
            LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
                Hardware(RuntimeArchitecture.X64, Gpu(
                    "NVIDIA arbitrary adapter", "GPU-explicit", testCase.TotalGiB, testCase.TotalGiB)),
                testCase.ModelId);

            Assert.Equal(testCase.Status, result.Status);
            Assert.Equal(testCase.ModelId, result.Plan?.Model.Id);
            Assert.Equal(testCase.ContextTokens, result.Plan?.Profile.ContextTokens);
            Assert.Equal(testCase.Precision, result.Plan?.Profile.KeyCachePrecision);
            Assert.Equal(testCase.RequiredBytes, result.RequiredTotalMemoryBytes);
            Assert.Equal(testCase.TotalGiB * GiB, result.DetectedTotalMemoryBytes);
            if (testCase.Status == LocalInferenceEligibilityStatus.Unsupported)
                Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        }
    }

    [Theory]
    [InlineData(LocalModelCatalog.Qwen35BModelId, 5_120, 512, 2_720, 272, 8)]
    [InlineData(LocalModelCatalog.Qwen38_27BModelId, 16_384, 1_024, 8_704, 544, 8)]
    [InlineData(LocalModelCatalog.Qwen27BModelId, 16_384, 1_024, 8_704, 544, 8)]
    public void GetRequiredMemoryBytes_IncludesRecipeKvCacheAndWorkspace(
        string modelId,
        long expectedF16CacheMiB,
        long expectedF16DraftCacheMiB,
        long expectedQ8CacheMiB,
        long expectedQ8DraftCacheMiB,
        long expectedQ8WorkspaceGiB)
    {
        LocalModelInfo model = LocalModelCatalog.Find(modelId)!;
        LocalInferenceRunProfile f16Profile = LocalModelCatalog.GetProfiles(model)[0];
        LocalInferenceRunProfile q8Profile = LocalModelCatalog.GetProfiles(model)[1];

        long f16Required = LocalInferenceEligibility.GetRequiredMemoryBytes(model, f16Profile);
        long q8Required = LocalInferenceEligibility.GetRequiredMemoryBytes(model, q8Profile);

        Assert.Equal(
            model.Weights.SizeBytes +
            (expectedF16CacheMiB + expectedF16DraftCacheMiB) * 1024 * 1024 +
            LocalModelCatalog.RuntimeWorkspaceReserveBytes,
            f16Required);
        Assert.Equal(
            model.Weights.SizeBytes +
            (expectedQ8CacheMiB + expectedQ8DraftCacheMiB) * 1024 * 1024 +
            expectedQ8WorkspaceGiB * GiB,
            q8Required);
    }

    [Fact]
    public void Evaluate_RanksEligibleBeforeBusyAndUnsupportedAdapters()
    {
        GpuInfo unsupported = Gpu("NVIDIA incompatible", "GPU-old", 48, 48) with { CudaMajorVersion = 12 };
        GpuInfo busy = Gpu("NVIDIA busy", "GPU-busy", 32, 1);
        GpuInfo eligible = Gpu("NVIDIA ready", "GPU-ready", 32, 32);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, unsupported, busy, eligible),
            LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-ready", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_IgnoresLegacySharedMemoryFields()
    {
        GpuInfo gpu = Gpu("NVIDIA generic unified memory", "GPU-shared", 8, 8) with
        {
            SharedGpuMemoryBytes = 16 * GiB,
            FreeSharedGpuMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.Arm64, gpu));

        // #1253 owns the memory semantics: shared/unified memory is ignored, so only the
        // 8 GiB dedicated device memory admits. #1281 owns the catalog: Qwen3.5 9B is
        // retired, so the unsupported-fallback plan is now the smallest offered model.
        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(8 * GiB, result.DetectedTotalMemoryBytes);
        Assert.Equal(8 * GiB, result.AvailableFreeMemoryBytes);
    }

    [Fact]
    public void Evaluate_RanksEligibleAdaptersByFreeThenTotalThenUuid()
    {
        GpuInfo moreTotal = Gpu("NVIDIA total", "GPU-z", 64, 42);
        GpuInfo moreFree = Gpu("NVIDIA free", "GPU-b", 48, 43);
        GpuInfo sameFreeAndTotalLowerUuid = Gpu("NVIDIA tie", "GPU-a", 48, 43);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, moreTotal, moreFree, sameFreeAndTotalLowerUuid),
            LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal("GPU-a", result.SelectedGpu?.StableId);
    }

    [Theory]
    [InlineData(null, 13, LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)]
    [InlineData("GPU-cuda", 12, LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow)]
    public void Evaluate_RequiresStableIdAndCompatibleCuda(
        string? stableId,
        int cudaMajor,
        LocalInferenceEligibilityFailureCode expectedFailure)
    {
        GpuInfo gpu = Gpu("NVIDIA arbitrary", stableId, 32, 32) with
        {
            CudaMajorVersion = cudaMajor,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(expectedFailure, result.FailureCode);
    }

    [Fact]
    public void Evaluate_IdentifiedGpuWithoutMemoryIsIncompleteRatherThanAbsent()
    {
        GpuInfo gpu = Gpu("NVIDIA identified", "GPU-identified", 32, 32) with
        {
            GpuVisibleMemoryBytes = null,
            FreeGpuVisibleMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.Equal("GPU-identified", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_ReportsNoNvidiaGpu()
    {
        var hardware = new HostHardwareInfo(
            RuntimeArchitecture.X64,
            null,
            null,
            [new GpuInfo(GpuVendor.Amd, "AMD GPU")],
            false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    private static HostHardwareInfo Hardware(RuntimeArchitecture architecture, params GpuInfo[] gpus) =>
        new(architecture, 64 * GiB, 48 * GiB, gpus, false);

    private static GpuInfo Gpu(
        string name,
        string? stableId,
        long totalGiB,
        long freeGiB) =>
        new(
            GpuVendor.Nvidia,
            name,
            totalGiB * GiB,
            freeGiB * GiB,
            DriverVersion: "616.30",
            CudaMajorVersion: 13,
            StableId: stableId);

}
