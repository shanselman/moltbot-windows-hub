using System.Text;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiLogTailTests
{
    /// <summary>
    /// Abridged from a real failing run on a Blackwell sm_121 host: llama.cpp's flash-attention
    /// kernel fails to initialize, the model instance dies, and the router reports HTTP 500.
    /// </summary>
    private const string CudaFailureLog =
        """
        [54881] load_tensors:        CUDA0 model buffer size = 15621.78 MiB
        [54881] llama_kv_cache:      CUDA0 KV buffer size = 16384.00 MiB
        [54881] sched_reserve: graph splits = 2
        [54881] cmn  common_init_: warming up the model with an empty run - please wait ...
        [54881] CUDA error: shared object initialization failed
        [54881] D:\a\llama.cpp\llama.cpp\ggml\src\ggml-cuda\ggml-cuda.cu:107: CUDA error
        [54881]   current device: 0, in function ggml_cuda_flash_attn_ext_mma_f16_case at fattn-mma-f16.cuh:1945
        srv    operator(): instance name=qwen3.6-27b-mtp-q4-k-m exited with status -1073740791
        """;

    private const string CleanStartupLog =
        """
        srv          init: running without SSL
        srv          init: using 17 threads for HTTP server
        srv   load_models: Loaded 1 custom model presets
        srv  llama_server: listening on http://<host>:54883/
        """;

    /// <summary>
    /// Regression guard: routine ggml/CUDA initialization lines carry a "name:" prefix (the same
    /// shape llama.cpp uses for genuine failures) but are not themselves failure evidence. A
    /// pattern that matched any "ggml_xxx:"-prefixed line previously misclassified these as the
    /// root cause of an unrelated failure.
    /// </summary>
    private const string BenignGgmlStartupLog =
        """
        ggml_cuda_init: found 1 CUDA devices:
        ggml_backend_cuda_buffer_type_alloc_buffer: allocating 1234.56 MiB on device 0
        srv  llama_server: listening on http://<host>:54883/
        """;

    /// <summary>
    /// Regression guard: llama.cpp reports host/device memory allocation failures as
    /// "failed to allocate ... buffer" (e.g. from <c>ggml_gallocr_reserve_n_impl</c> or
    /// <c>llama_new_context_with_model</c>), a distinct failure mode from the CUDA
    /// flash-attention crash above and one that previously matched none of the recognized
    /// markers, silently dropping the actual root cause.
    /// </summary>
    private const string AllocationFailureLog =
        """
        [54881] llama_new_context_with_model: constructing llama_context
        [54881] llama_kv_cache: CUDA0 KV buffer size = 16384.00 MiB
        [54881] ggml_gallocr_reserve_n_impl: failed to allocate CUDA0 buffer of size 4294967296
        [54881] graph_reserve: failed to allocate compute buffers
        srv    operator(): instance name=qwen3.6-27b-mtp-q4-k-m exited with status -1073740791
        """;

    private const string VerboseRequestResponseLog =
        "srv  log_server_r: request: {\"messages\":[{\"role\":\"user\",\"content\":\"SENTINEL-PROMPT error: failed to load\"}]}\n" +
        "srv  log_server_r: response: {\"choices\":[{\"message\":{\"content\":\"SENTINEL-ASSISTANT CUDA error\"}}]}\n" +
        "[54881] CUDA error: Authorization: Bearer credential-sentinel-12345\n" +
        "[54881] cudaError: allocation failed\u2028retry disabled\n" +
        "srv    operator(): instance name=qwen3.6-27b-mtp-q4-k-m exited with status -1073740791";

    private const string ModelFormatFailureLog =
        "llama_model_load: error loading model architecture: unknown model architecture";

    [Fact]
    public void ExtractDiagnosticLines_ReturnsCudaAndExitStatusLines()
    {
        IReadOnlyList<string> lines = LocalAiLogTail.ExtractDiagnosticLines(CudaFailureLog);

        Assert.InRange(lines.Count, 1, 4);
        Assert.Contains(lines, line => line.Contains("CUDA error", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("exited with status", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("graph splits", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractDiagnosticLines_ReturnsAllocationFailureLines()
    {
        IReadOnlyList<string> lines = LocalAiLogTail.ExtractDiagnosticLines(AllocationFailureLog);

        Assert.Contains(lines, line => line.Contains("failed to allocate CUDA0 buffer", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("failed to allocate compute buffers", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("constructing llama_context", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractDiagnosticLines_IgnoresLogsWithoutFailureMarkers()
    {
        Assert.Empty(LocalAiLogTail.ExtractDiagnosticLines(CleanStartupLog));
    }

    [Fact]
    public void ExtractDiagnosticLines_IgnoresRoutineGgmlInitializationLines()
    {
        Assert.Empty(LocalAiLogTail.ExtractDiagnosticLines(BenignGgmlStartupLog));
    }

    [Fact]
    public void ExtractDiagnosticLines_RejectsVerbosePayloadsAndRedactsAcceptedDiagnostics()
    {
        IReadOnlyList<string> lines = LocalAiLogTail.ExtractDiagnosticLines(VerboseRequestResponseLog);

        Assert.DoesNotContain(lines, line => line.Contains("SENTINEL-PROMPT", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("SENTINEL-ASSISTANT", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("credential-sentinel-12345", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Authorization: [REDACTED]", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("allocation failed retry disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains('\u2028'));
        Assert.Contains(lines, line => line.Contains("exited with status", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractDiagnosticLines_ReturnsModelFormatFailure()
    {
        IReadOnlyList<string> lines = LocalAiLogTail.ExtractDiagnosticLines(ModelFormatFailureLog);

        Assert.Contains(lines, line => line.Contains("error loading model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadTailAsync_ReadsBoundedTailWhileFileIsOpenForWriting()
    {
        string path = Path.Combine(Path.GetTempPath(), $"openclaw-logtail-{Guid.NewGuid():N}.log");
        var payload = Encoding.UTF8.GetBytes(new string('a', 100) + new string('b', 50));
        await File.WriteAllBytesAsync(path, payload);
        try
        {
            // llama-server holds these files open while we read them.
            await using var writer = new FileStream(
                path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            string tail = await LocalAiLogTail.ReadTailAsync(path, 50, CancellationToken.None);

            Assert.Equal(new string('b', 50), tail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadTailAsync_ReturnsEmptyForMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"openclaw-missing-{Guid.NewGuid():N}.log");

        Assert.Equal(string.Empty, await LocalAiLogTail.ReadTailAsync(path, 1024, CancellationToken.None));
    }
}
