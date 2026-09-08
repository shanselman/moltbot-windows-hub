using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Shared, reusable <see cref="JsonSerializerOptions"/> singletons.
/// Prefer these over inline <c>new JsonSerializerOptions { … }</c> allocations
/// at call sites to avoid repeated heap allocation and settings drift.
/// </summary>
internal static class JsonSerializerOptionsCache
{
    /// <summary>
    /// Gateway protocol payloads use camelCase and evolve additively. Unknown
    /// properties are ignored while property names are matched case-insensitively.
    /// </summary>
    internal static readonly JsonSerializerOptions GatewayProtocol =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    /// <summary>
    /// Pretty-print JSON with no additional overrides.
    /// Suitable for writing human-readable configuration and diagnostic files.
    /// </summary>
    internal static readonly JsonSerializerOptions WriteIndented = new()
    {
        WriteIndented = true
    };
}
