using Xunit;

namespace OpenClaw.TestSupport;

/// <summary>
/// Detects whether this process may create symbolic links. On Windows that needs
/// Developer Mode or an elevated token, so an ordinary developer machine cannot run
/// symlink proofs even though CI (whose runners are administrators) can.
/// </summary>
public static class SymbolicLinkSupport
{
    /// <summary>Set to any value to turn the skip into a hard failure.</summary>
    public const string RequireEnvironmentVariable = "OPENCLAW_REQUIRE_SYMLINK_TESTS";

    private static readonly Lazy<bool> s_available = new(Probe, isThreadSafe: true);

    public static bool IsAvailable => s_available.Value;

    public static bool IsRequired =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RequireEnvironmentVariable));

    /// <summary>
    /// Creates a symbolic link, asserting success. Call this only from a test gated by
    /// <see cref="SymbolicLinkFactAttribute"/> -- silently skipping the link and
    /// continuing would leave a test that passes without proving anything.
    /// </summary>
    public static void CreateSymbolicLink(string linkPath, string targetPath)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "Symbolic links are unavailable in this environment; gate the test with [SymbolicLinkFact].");
        }

        File.CreateSymbolicLink(linkPath, targetPath);
    }

    private static bool Probe()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"openclaw-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "target");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(directory, "link"), target);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A probe leftover in the temp directory must not fail a test run.
            }
        }
    }
}

/// <summary>
/// Runs a proof that needs real symbolic links. Ordinary runs report it as skipped --
/// never as passed -- so nobody mistakes an unexercised symlink path for a verified
/// one. Set <see cref="SymbolicLinkSupport.RequireEnvironmentVariable"/> to require the
/// capability and fail loudly when it is missing.
/// </summary>
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    public SymbolicLinkFactAttribute()
    {
        if (SymbolicLinkSupport.IsRequired || SymbolicLinkSupport.IsAvailable)
            return;

        Skip = "Creating symbolic links requires Developer Mode or elevation. " +
            $"Set {SymbolicLinkSupport.RequireEnvironmentVariable}=1 to require this proof.";
    }
}
