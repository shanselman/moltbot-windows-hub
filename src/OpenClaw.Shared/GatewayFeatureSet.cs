using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Immutable method and event inventory advertised by the Gateway in
/// <c>hello-ok.features</c>. Clients must use this inventory instead of guessing
/// support from a version string.
/// </summary>
public sealed class GatewayFeatureSet
{
    private readonly HashSet<string> _methodSet;
    private readonly HashSet<string> _eventSet;

    public static GatewayFeatureSet Empty { get; } = new([], []);

    public GatewayFeatureSet(
        IEnumerable<string> methods,
        IEnumerable<string> events)
    {
        Methods = Normalize(methods);
        Events = Normalize(events);
        _methodSet = new HashSet<string>(Methods, StringComparer.Ordinal);
        _eventSet = new HashSet<string>(Events, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Methods { get; }
    public IReadOnlyList<string> Events { get; }

    public bool SupportsMethod(string method) =>
        !string.IsNullOrWhiteSpace(method) && _methodSet.Contains(method);

    public bool SupportsEvent(string eventName) =>
        !string.IsNullOrWhiteSpace(eventName) && _eventSet.Contains(eventName);

    internal static GatewayFeatureSet FromHelloOk(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        return new GatewayFeatureSet(
            ReadStringArray(features, "methods"),
            ReadStringArray(features, "events"));
    }

    private static string[] ReadStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] Normalize(IEnumerable<string> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
