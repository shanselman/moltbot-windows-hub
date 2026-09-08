using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal interface IExtensionsRuntimeSource
{
    IOperatorGatewayClient? CurrentClient { get; }
    IReadOnlyList<string> GetAgentIds();
    string GetText(string resourceKey);
    string FormatText(string resourceKey, params object?[] args);
}

/// <summary>
/// Narrow, non-owning access to the app-managed Gateway client and agent cache.
/// Accessors are evaluated per operation because the active client changes on reconnect.
/// </summary>
internal sealed class ExtensionsRuntimeSource : IExtensionsRuntimeSource
{
    private readonly Func<IOperatorGatewayClient?> _getClient;
    private readonly Func<IReadOnlyList<string>> _getAgentIds;
    private readonly Func<string, string> _getText;
    private readonly Func<string, object?[], string> _formatText;

    public ExtensionsRuntimeSource(
        Func<IOperatorGatewayClient?> getClient,
        Func<IReadOnlyList<string>> getAgentIds,
        Func<string, string> getText,
        Func<string, object?[], string> formatText)
    {
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _getAgentIds = getAgentIds ?? throw new ArgumentNullException(nameof(getAgentIds));
        _getText = getText ?? throw new ArgumentNullException(nameof(getText));
        _formatText = formatText ?? throw new ArgumentNullException(nameof(formatText));
    }

    public IOperatorGatewayClient? CurrentClient => _getClient();

    public IReadOnlyList<string> GetAgentIds()
    {
        var ids = _getAgentIds()
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ids.Length == 0 ? ["main"] : ids;
    }

    public string GetText(string resourceKey) => _getText(resourceKey);

    public string FormatText(string resourceKey, params object?[] args) =>
        _formatText(resourceKey, args);
}
