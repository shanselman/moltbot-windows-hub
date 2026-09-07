using System.Runtime.InteropServices;
using OpenClaw.Shared.Inference;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.Tests;

public class LocalAiGpuVerificationTests
{
    [Fact]
    public void ParseGpuLoadEvidence_ReadsFullOffloadAndCudaModelBuffer()
    {
        const string log = """
            load_tensors: offloaded 42/42 layers to GPU
            load_tensors:        CUDA0 model buffer size = 21087.70 MiB
            """;

        LocalAiGpuLogEvidence evidence = WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log);

        Assert.Equal(42, evidence.OffloadedLayers);
        Assert.Equal(42, evidence.TotalLayers);
        Assert.Equal(22_112_056_115L, evidence.CudaModelBufferBytes);
    }

    [Fact]
    public void ParseGpuLoadEvidence_AllowsMissingCudaModelBuffer()
    {
        const string log = "load_tensors: offloaded 42/42 layers to GPU";

        LocalAiGpuLogEvidence evidence = WindowsLocalAiGpuEvidenceProbe.ParseGpuLoadEvidence(log);

        Assert.Null(evidence.CudaModelBufferBytes);
    }

    [Fact]
    public void ResolveGpuMemoryEvidence_SkipsPostLoadProbeWhenCudaBufferIsReported()
    {
        const long totalBytes = 48L * 1024 * 1024 * 1024;
        const long freeBytes = 47L * 1024 * 1024 * 1024;
        var baseline = new HostHardwareInfo(
            Architecture.Arm64,
            null,
            null,
            [new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA RTX Spark N1X",
                GpuVisibleMemoryBytes: totalBytes,
                FreeGpuVisibleMemoryBytes: freeBytes,
                StableId: "000F:01:00.0")],
            VulkanAvailable: false);
        var logEvidence = new LocalAiGpuLogEvidence(42, 42, 21L * 1024 * 1024 * 1024);
        bool postLoadProbeCalled = false;

        var evidence = WindowsLocalAiGpuEvidenceProbe.ResolveGpuMemoryEvidence(
            "000F:01:00.0",
            baseline,
            logEvidence,
            () =>
            {
                postLoadProbeCalled = true;
                return HostHardwareInfo.Unknown;
            });

        Assert.False(postLoadProbeCalled);
        Assert.Equal(totalBytes, evidence.TotalBytes);
        Assert.Equal(freeBytes, evidence.FreeBeforeBytes);
        Assert.Null(evidence.FreeAfterBytes);
    }

    [Fact]
    public void HasRequiredGpuLoadEvidence_AcceptsFullOffloadCudaBuffer()
    {
        var evidence = new LocalAiGpuLoadEvidence(
            ProcessId: 123,
            SelectedGpuId: "GPU-123",
            CudaModulePath: @"C:\LocalAI\ggml-cuda.dll",
            OffloadedLayers: 42,
            TotalLayers: 42,
            TotalGpuVisibleBytes: 8L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesBeforeLoad: 7L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesAfterLoad: 6L * 1024 * 1024 * 1024,
            CudaModelBufferBytes: 21L * 1024 * 1024 * 1024);

        bool accepted = VerifyLocalAiGpuLoadStep.HasRequiredGpuLoadEvidence(
            evidence,
            minimumDeltaBytes: 10L * 1024 * 1024 * 1024);

        Assert.True(accepted);
    }

    [Fact]
    public void HasRequiredGpuLoadEvidence_AcceptsFullOffloadCudaDelta()
    {
        var evidence = new LocalAiGpuLoadEvidence(
            ProcessId: 123,
            SelectedGpuId: "GPU-123",
            CudaModulePath: @"C:\LocalAI\ggml-cuda.dll",
            OffloadedLayers: 42,
            TotalLayers: 42,
            TotalGpuVisibleBytes: 24L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesBeforeLoad: 20L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesAfterLoad: 5L * 1024 * 1024 * 1024,
            CudaModelBufferBytes: null);

        bool accepted = VerifyLocalAiGpuLoadStep.HasRequiredGpuLoadEvidence(
            evidence,
            minimumDeltaBytes: 10L * 1024 * 1024 * 1024);

        Assert.True(accepted);
    }

    [Fact]
    public void HasRequiredGpuLoadEvidence_RejectsWhenNeitherMemoryProofMeetsThreshold()
    {
        var evidence = new LocalAiGpuLoadEvidence(
            ProcessId: 123,
            SelectedGpuId: "GPU-123",
            CudaModulePath: @"C:\LocalAI\ggml-cuda.dll",
            OffloadedLayers: 42,
            TotalLayers: 42,
            TotalGpuVisibleBytes: 8L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesBeforeLoad: 7L * 1024 * 1024 * 1024,
            FreeGpuVisibleBytesAfterLoad: 6L * 1024 * 1024 * 1024,
            CudaModelBufferBytes: 2L * 1024 * 1024 * 1024);

        bool accepted = VerifyLocalAiGpuLoadStep.HasRequiredGpuLoadEvidence(
            evidence,
            minimumDeltaBytes: 10L * 1024 * 1024 * 1024);

        Assert.False(accepted);
    }
}
