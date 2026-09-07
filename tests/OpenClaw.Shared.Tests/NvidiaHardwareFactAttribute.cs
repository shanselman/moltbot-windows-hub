using System;
using OpenClaw.Shared.Inference;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Runs a proof that needs a real NVIDIA GPU. Ordinary runs skip it when no
/// NVIDIA device is present, so GPU-less CI stays green, while an explicit
/// hardware-proof run (<c>OPENCLAW_RUN_GPU_PROOF=1</c>) requires the device and
/// fails loudly if it is missing.
/// </summary>
public sealed class NvidiaHardwareFactAttribute : FactAttribute
{
    private const string EnvVar = "OPENCLAW_RUN_GPU_PROOF";

    public NvidiaHardwareFactAttribute()
    {
        if (IsHardwareProofRequired || HasNvidiaGpu())
            return;

        Skip = $"No NVIDIA GPU detected. Set {EnvVar}=1 to require this hardware proof.";
    }

    internal static bool IsHardwareProofRequired =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar));

    private static bool HasNvidiaGpu()
    {
        try
        {
            return new CudaHostHardwareProbe().Probe().HasNvidiaGpu;
        }
        catch
        {
            return false;
        }
    }
}
