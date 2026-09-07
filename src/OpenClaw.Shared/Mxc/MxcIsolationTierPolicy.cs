namespace OpenClaw.Shared.Mxc;

internal static class MxcIsolationTierPolicy
{
    internal const string BaseContainer = "base-container";

    /// <summary>
    /// MXC 0.8 chooses BaseContainer per request. Its request compatibility
    /// check can fall through only for least-privilege mode, denied paths that
    /// the OS contract cannot enforce, proxy/directional-network policy, or
    /// denial capture. <see cref="MxcConfig"/> cannot express the latter three,
    /// and OpenClaw omits backend denied paths. Validate the remaining emitted
    /// fields here before relying on BaseContainer's non-cascading root grants.
    /// </summary>
    internal static bool IsSystemRunConfigBaseContainerCompatible(MxcConfig config) =>
        string.Equals(
            config.Version,
            MxcPolicyBuilder.SupportedPolicyVersion,
            StringComparison.Ordinal) &&
        config.Containment is null &&
        config.ProcessContainer?.LeastPrivilege == false &&
        config.Filesystem?.DeniedPaths is null;
}
