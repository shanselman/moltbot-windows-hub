using System.Text;
using System.Text.RegularExpressions;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Bounded, read-only access to the managed llama-server stdout and stderr logs. The server writes
/// these through a rotating writer while we read, so every read tolerates concurrent writes and
/// deletes and never takes an exclusive handle.
/// </summary>
internal static partial class LocalAiLogTail
{
    /// <summary>GPU offload evidence appears well before the tail, so it needs a generous window.</summary>
    internal const int GpuEvidenceTailBytes = 2 * 1024 * 1024;

    /// <summary>Failure diagnostics are the last thing written, so a small tail suffices.</summary>
    internal const int DiagnosticTailBytes = 256 * 1024;

    private const int MaximumDiagnosticLineLength = 200;

    internal static async Task<string> ReadTailAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return string.Empty;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long count = Math.Min(stream.Length, maximumBytes);
        stream.Seek(-count, SeekOrigin.End);
        var bytes = new byte[checked((int)count)];
        int read = 0;
        while (read < bytes.Length)
        {
            int next = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken);
            if (next == 0)
                break;
            read += next;
        }
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    internal static async Task<string> ReadCombinedTailAsync(
        LocalAiPaths paths,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        await ReadTailAsync(paths.StandardOutputLogPath, maximumBytes, cancellationToken) + "\n" +
        await ReadTailAsync(paths.StandardErrorLogPath, maximumBytes, cancellationToken);

    /// <summary>
    /// Reads the salient failure lines from the llama-server logs. Diagnostics must never mask the
    /// failure being diagnosed, so an unreadable log yields an empty list rather than throwing.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ReadDiagnosticLinesAsync(
        LocalAiPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            string log = await ReadCombinedTailAsync(paths, DiagnosticTailBytes, cancellationToken);
            return ExtractDiagnosticLines(log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Keeps the last <paramref name="maximumLines"/> distinct lines that look like a failure, in
    /// file order. llama.cpp reports the proximate cause (a CUDA kernel fault, a nonzero instance
    /// exit status) close to the end of the log, so the tail is the interesting part.
    /// </summary>
    internal static IReadOnlyList<string> ExtractDiagnosticLines(string log, int maximumLines = 4)
    {
        if (string.IsNullOrWhiteSpace(log))
            return [];

        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawLine in log.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 ||
                RequestOrResponsePattern().IsMatch(line) ||
                !DiagnosticPattern().IsMatch(line))
                continue;
            line = NormalizeSingleLine(TokenSanitizer.SanitizeLogMessage(line));
            if (line.Length > MaximumDiagnosticLineLength)
                line = line[..MaximumDiagnosticLineLength];
            if (seen.Add(line))
                matches.Add(line);
        }

        return matches.Count <= maximumLines
            ? matches
            : matches[^maximumLines..];
    }

    private static string NormalizeSingleLine(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsSeparator(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    [GeneratedRegex(
        @"CUDA error|cudaError|exited with status|failed to load|failed to allocate|out of memory|error loading model|unable to load model",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(
        @"(?:\blog_server_[a-z_]*|\brequest|\bresponse)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestOrResponsePattern();
}
